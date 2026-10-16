// Ported from play/hudWindow.ts itemCell() and its '.eq' cell look: the new UI's item cell, bound to (grid, index) of
// the real models instead of a lab item name. One ItemCells service per UiRuntime (itemCells(rt, deps)) owns every
// cell: it renders each from the model (icon via libPicture, stack count, durability state, locked / queued / picked
// state, hero cells greyed while no hero is summoned) and re-renders on the models' change events, coalesced to one
// pass a frame and diffed per cell (a cell writes the DOM only when what it shows changed). Used by the belt, the
// inventory, character, storage, trade ... windows; pointer handling (pick, drag, drop, right-click) is drag.ts's, on
// one delegated listener: cells carry data-cell="grid:index".
//
// The item tooltip is not built here: the integrator wires `onHover` to the tooltip service (core/tooltip.ts) with its
// ItemLabelContext; hovers are suppressed while an item is dragged or picked.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { ItemType } from "../../../protocol/enums.generated.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { Emitter } from "../../../game/emitter.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, rafCoalesce, setText } from "./dom.ts";
import type { UiRuntime } from "./runtime.ts";
import {
  canPick, cellKey, cellLocked, isCustom, itemAt, sameCell,
  type CellGrid, type CellRef, type CustomGrid, type CustomTarget, type ItemModels, type RouteDeps,
} from "./dragRoutes.ts";

const CSS = `
.hudm .icell { position: relative; width: var(--cs, 40px); height: var(--cs, 40px); flex: none; box-sizing: border-box; pointer-events: auto;
  cursor: pointer; user-select: none; -webkit-user-drag: none; touch-action: none; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .icell::after { content: ""; position: absolute; inset: 1px; border-radius: calc(var(--rad) - 1px); pointer-events: none;
  box-shadow: inset 0 0 0 1px rgba(0,0,0,.9), inset 0 0 6px 1px rgba(0,0,0,.75); }
.hudm .icell > i { position: absolute; inset: 0; background: center / auto no-repeat; image-rendering: pixelated; pointer-events: none; }
.hudm .icell > .e { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; opacity: .28; pointer-events: none; }
.hudm .icell.has > .e { display: none; }
.hudm .icell > b { position: absolute; right: 3px; bottom: 1px; z-index: 1; font-size: 10px; font-weight: 700; color: #fff; pointer-events: none;
  text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .icell > kbd { position: absolute; left: 3px; top: 1px; z-index: 1; font: 700 9px/1 inherit; color: var(--dim); pointer-events: none; text-shadow: 0 0 2px #000; }
.hudm .icell > s { position: absolute; left: 0; right: 0; bottom: 0; height: 2px; z-index: 1; pointer-events: none; text-decoration: none; display: none; }
.hudm .icell.low > s { display: block; background: var(--warn); }
.hudm .icell.broken > s { display: block; background: var(--bad); }
.hudm .icell.broken > i { filter: sepia(1) hue-rotate(-50deg) saturate(3) brightness(.7); }
.hudm .icell.sealed::before { content: ""; position: absolute; left: 3px; bottom: 3px; width: 6px; height: 6px; z-index: 1; border-radius: 50%;
  background: var(--info); box-shadow: 0 0 0 1px #000; pointer-events: none; }
.hudm .icell:hover { border-color: var(--ac); }
.hudm .icell.empty-cell { cursor: default; }
.hudm .icell.ro { cursor: default; }
.hudm .icell.lock > i, .hudm .icell.pick > i { opacity: .4; }
.hudm .icell.lock { outline: 1px dashed var(--warn); outline-offset: -2px; cursor: progress; }
.hudm .icell.pick { border-color: var(--ac); }
.hudm .icell.off { opacity: .35; filter: grayscale(1); cursor: not-allowed; }
`;

/** Options for one cell. */
export interface CellOptions {
  /** Edge length in HUD px (default 40). */
  size?: number;
  /** Extra classes on the cell. */
  cls?: string;
  /** Drop filter: "" = this cell takes the item, else the refusal shown on the drag label (hero belt: potions only). */
  filter?: (item: UserItem, info: ItemInfo | undefined) => string;
  /** Slot art while empty (an svg / icon node, built once). */
  empty?: Node;
  /** The key hint drawn in the corner (belt "1".."8", "9", "0"). */
  key?: string;
  /** Display only: no pick, drag, drop or right-click. */
  readOnly?: boolean;
  /** Greys the cell out and refuses drops (default for hero grids: no live hero). */
  disabled?: () => boolean;
  /** The owner's scope (a window's): the cell is dropped from the service when it aborts. Default: the UI scope. */
  signal?: AbortSignal;
}

/** A cell bound to (grid, index). Index can be re-pointed (paged grids) with setRef. */
export class ItemCell {
  readonly el: HTMLDivElement;
  private readonly icon = h("i");
  private readonly count = h("b");
  private readonly bar = h("s");
  private _ref: CellRef;
  /** The last drawn state (diff key) and image. */
  private shown = "";
  private img = -2;
  private disposedFlag = false;
  /** Removes the cell's hook on a caller's `signal` (set by ItemCells.cell; dispose drops it, so a rebuilt cell is not
   *  kept alive by a long-lived window signal). */
  unhook: (() => void) | null = null;

  constructor(private readonly owner: ItemCells, ref: CellRef, readonly opts: CellOptions) {
    this._ref = ref;
    this.el = h("div", "icell" + (opts.cls ? " " + opts.cls : "") + (opts.readOnly ? " ro" : ""), this.icon, this.count, this.bar);
    if (opts.size) this.el.style.setProperty("--cs", opts.size + "px");
    if (opts.empty) this.el.append(h("span", "e", opts.empty));
    if (opts.key) this.el.append(h("kbd", "", opts.key));
    this.count.hidden = true;
    this.el.dataset.cell = cellKey(ref);
  }

  get ref(): CellRef { return this._ref; }
  get disposed(): boolean { return this.disposedFlag; }
  /** The item the cell shows now (read from the model, never cached). */
  get item(): UserItem | null { return itemAt(this.owner.deps, this._ref); }
  /** Whether drops are refused because the cell is greyed out. */
  get off(): boolean { return this.opts.disabled?.() ?? false; }

  /** Re-points the cell (a storage page, a scrolled list) and redraws it. */
  setRef(ref: CellRef): void {
    if (sameCell(ref, this._ref)) return;
    this._ref = ref;
    this.el.dataset.cell = cellKey(ref);
    this.render();
  }

  /** The drop filter's verdict for `item` ("" = fine). */
  refusal(item: UserItem): string {
    if (this.opts.readOnly) return "";
    if (this.off) return "Unavailable";
    return this.opts.filter?.(item, this.owner.deps.models.itemInfos.get(item.ItemIndex)) ?? "";
  }

  /** Draws the cell from the model; writes the DOM only for what changed. */
  render(): void {
    if (this.disposedFlag) return;
    const d = this.owner.deps, item = this.item;
    const info = item ? d.models.itemInfos.get(item.ItemIndex) : undefined;
    const image = item && info ? info.Image : -1;
    const stack = !!item && !!info && info.StackSize > 1;
    let dura = "";
    if (item && info && !stack && item.MaxDura > 0) dura = item.CurrentDura <= 0 ? "broken" : item.CurrentDura * 5 < item.MaxDura ? "low" : "";
    const sealed = !!item?.Sealed && dateFromBinary(item.Sealed.ExpiryDate).getTime() > Date.now();
    const off = this.off;
    const locked = !!item && (cellLocked(d, this._ref) || this.owner.isBusy(this._ref)) || (!item && cellLocked(d, this._ref));
    const picked = this.owner.isPicked(this._ref);
    const key = `${item ? String(item.UniqueID) : ""}|${image}|${stack ? item!.Count : ""}|${dura}|${sealed}|${off}|${locked}|${picked}`;
    if (key === this.shown) return;
    this.shown = key;
    const c = this.el.classList;
    c.toggle("has", !!item);
    c.toggle("empty-cell", !item);
    c.toggle("low", dura === "low");
    c.toggle("broken", dura === "broken");
    c.toggle("sealed", sealed);
    c.toggle("off", off);
    c.toggle("lock", locked);
    c.toggle("pick", picked);
    this.count.hidden = !stack;
    if (stack) setText(this.count, String(item!.Count));
    if (image !== this.img) {
      this.img = image;
      if (image < 0) this.icon.style.backgroundImage = "";
      else {
        void libPicture(Lib.Items, image).then((pic) => {
          if (this.img !== image || this.disposedFlag) return; // the cell moved on while it loaded
          this.icon.style.backgroundImage = pic ? `url(${pic.url})` : "";
        });
      }
    }
    if (item && info) this.el.setAttribute("aria-label", info.Name + (stack ? ` (${item.Count})` : ""));
    else this.el.removeAttribute("aria-label");
  }

  /** Forces a full redraw on the next render (theme / info changes). */
  invalidate(): void { this.shown = ""; }

  /** Drops the cell from the service and removes it from the page. */
  dispose(): void {
    if (this.disposedFlag) return;
    this.disposedFlag = true;
    this.unhook?.();
    this.unhook = null;
    this.owner.forget(this);
    this.el.remove();
  }
}

/** The models + clock a cell service reads (GameUiContext.models fits `models`). */
export interface ItemCellDeps {
  readonly models: ItemModels;
  /** performance.now(). */
  now(): number;
}

/** Every item cell of one UiRuntime, the custom drop targets, and the render pass. */
export class ItemCells {
  /** Routing deps for dragRoutes.ts (models, clock, custom targets). */
  readonly deps: RouteDeps;
  /** After every render pass (the drag re-checks its source / pick here). */
  readonly rendered = new Emitter<void>();
  /** Hover in / out of a filled cell (null = left / suppressed): the integrator shows the item tooltip here. */
  onHover: (cell: ItemCell | null, item: UserItem | null) => void = () => {};
  /** Extra "waiting" state from outside (drag.ts: requests queued in the send pacer). */
  busy: (ref: CellRef) => boolean = () => false;
  private readonly cells = new Set<ItemCell>();
  private readonly byEl = new WeakMap<Element, ItemCell>();
  private readonly targets = new Map<CustomGrid, CustomTarget>();
  private readonly signal: AbortSignal;
  private hovered: ItemCell | null = null;
  /** The item last reported for the hovered cell (a model change re-reports only when it differs). */
  private hoveredItem: UserItem | null = null;
  private suppressed = false;
  private readonly schedule: () => void;

  constructor(private readonly rt: UiRuntime, d: ItemCellDeps) {
    injectCss("core-item-cells", CSS);
    this.signal = rt.signal;
    this.deps = { models: d.models, now: () => d.now(), custom: (g) => this.targets.get(g) };
    this.schedule = rafCoalesce(() => this.renderAll(), this.signal);
    const m = d.models, on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    on(m.inventory.subscribe(this.schedule)); // bag, equipment, trade, storage, guild, refine, locks (ItemChanges / HeroItems lock here too)
    on(m.hero.onChange(this.schedule));
    on(m.itemPick.changed.on(this.schedule));
    on(m.npcSession.drop.changed.on(this.schedule));
    on(m.npcSession.drop.shown.on(this.schedule));
    on(m.npcSession.drop.hidden.on(this.schedule));
    // One delegated hover listener for every cell (no per-cell listeners).
    rt.root.addEventListener("pointerover", (e) => this.hover(this.cellOf(e.target)), { signal: this.signal });
    rt.root.addEventListener("pointerout", (e) => {
      const to = this.cellOf(e.relatedTarget);
      if (to !== this.hovered) this.hover(to);
    }, { signal: this.signal });
  }

  /** A new cell for (grid, index). Append `cell.el` where it goes; it redraws itself on model changes. */
  cell(grid: CellGrid, index: number, opts: CellOptions = {}): ItemCell {
    const o: CellOptions = { ...opts };
    if (!o.disabled && (grid === "heroInv" || grid === "heroEq" || grid === "heroHP" || grid === "heroMP")) {
      const hero = this.deps.models.hero;
      o.disabled = () => !hero.info || hero.dead;
    }
    const c = new ItemCell(this, { grid, index }, o);
    this.cells.add(c);
    this.byEl.set(c.el, c);
    const sig = o.signal ?? this.signal;
    if (sig !== this.signal) {
      // no runtime signal on this hook (its abort step would keep every disposed cell reachable): the runtime's dispose
      // disposes every cell, and ItemCell.dispose removes the hook
      const onAbort = (): void => c.dispose();
      sig.addEventListener("abort", onAbort, { once: true });
      c.unhook = () => sig.removeEventListener("abort", onAbort);
    }
    c.render();
    return c;
  }

  /** The cell an element belongs to (the delegated pointer handlers). */
  cellOf(target: EventTarget | null): ItemCell | undefined {
    const el = target instanceof Element ? target.closest(".icell[data-cell]") : null;
    const c = el ? this.byEl.get(el) : undefined;
    return c && !c.disposed && this.rt.root.contains(c.el) ? c : undefined;
  }

  /** Every live cell (drag hints). */
  all(): Iterable<ItemCell> { return this.cells; }

  /** Registers a custom drop target ("x:<name>"); returns its removal. */
  addTarget(grid: CustomGrid, target: CustomTarget): () => void {
    this.targets.set(grid, target);
    this.refresh();
    return () => { if (this.targets.get(grid) === target) { this.targets.delete(grid); this.refresh(); } };
  }

  /** Redraws every cell on the next frame (S.NewItemInfo: UiEvent "itemInfo"). */
  refresh(): void { this.schedule(); }

  /** Redraws every cell on the next frame, ignoring the diff (theme change). */
  invalidate(): void {
    for (const c of this.cells) c.invalidate();
    this.schedule();
  }

  /** Stops / restarts hover reports (the drag hides the tooltip while an item is held). */
  suppressHover(on: boolean): void {
    this.suppressed = on;
    if (on) this.hover(undefined);
  }

  isPicked(r: CellRef): boolean {
    const p = this.deps.models.itemPick.cell;
    return !!p && p.grid === r.grid && p.index === r.index;
  }

  isBusy(r: CellRef): boolean { return this.busy(r); }

  /** Whether the cell can be picked up (filled, unlocked, a grid with move cases, not greyed / read-only). */
  pickable(c: ItemCell): boolean {
    return !c.opts.readOnly && !c.off && !this.isBusy(c.ref) && canPick(this.deps, c.ref);
  }

  /** ItemCell.dispose. */
  forget(c: ItemCell): void {
    this.cells.delete(c);
    if (this.hovered === c) this.hover(undefined);
  }

  /** Drops every cell (runtime dispose via rt.service). */
  dispose(): void {
    for (const c of [...this.cells]) c.dispose();
    this.targets.clear();
  }

  private renderAll(): void {
    for (const c of this.cells) c.render(); // diffed: an unchanged cell writes nothing
    const h = this.hovered;
    if (h && !this.suppressed && h.item !== this.hoveredItem) this.report(h);
    this.rendered.emit();
  }

  private hover(c: ItemCell | undefined): void {
    const next = this.suppressed ? null : c ?? null;
    if (next === this.hovered) return;
    this.hovered = next;
    this.report(next);
  }

  private report(c: ItemCell | null): void {
    this.hoveredItem = c?.item ?? null;
    try { this.onHover(c, this.hoveredItem); } catch (err) { console.error(err); }
  }
}

/** The runtime's cell service (built on first use; `deps` is required the first time). */
export function itemCells(rt: UiRuntime, deps?: ItemCellDeps): ItemCells {
  return rt.service("itemCells", (r) => {
    if (!deps) throw new Error("itemCells: the first call needs the models");
    return new ItemCells(r, deps);
  });
}

/** A drop filter for the HUD's hero belt cells (hero inventory 0-1): potions only (client-side check). */
export function potionsOnly(_item: UserItem, info: ItemInfo | undefined): string {
  return info && info.Type !== ItemType.Potion ? "Potions only" : "";
}

/** Whether a grid is a custom one (re-exported for callers that only import cells). */
export { isCustom };
