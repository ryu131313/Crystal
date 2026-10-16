// Ported from play/hudDrag.ts: the new UI's item drag and pick, REQUEST-BASED. The mock moved stacks locally
// (DragHost.set); here every drop is routed by dragRoutes.ts to the one model method that sends the packet and locks
// the cells (Inventory / HeroItems / ItemChanges / NpcDrop), and nothing changes until the server answers.
//
// Gestures on item cells (itemCells.ts; one delegated listener on the UI root):
//  - press and move 5 px on a filled cell: drag. A ghost follows the pointer (Layers.drag, position: absolute, pointer
//    coordinates divided by the scale measured once per gesture: toLocal), the source dims, the tooltip hides; the cell
//    under the pointer lights green with what will happen (Move, Swap, Stack, Equip, Store (free cell) ...) or red with
//    the reason; cells outside the source grid and the bag that would take it are outlined. Esc cancels.
//  - Shift-drag of a stack in the bag / NPC storage: Split (C.SplitItem has no target: the server places the new stack).
//  - drop on the world (the map, not the UI): the shared world-drop rules (src/game/itemDrop.ts: DontDrop refusal,
//    Yes / No for one item, the amount box for a stack) with the new UI's popups. Bag / belt and hero bag / belt only.
//  - click (no move): the desktop pick (GameScene.SelectedCell through the shared ItemPick model, so a pick and a drag
//    never fight): the icon follows the pointer, the next cell click places it by the same routes, a map click drops it
//    (mapClick(), from GameUi.mapMouseDown), Esc or a right-click clears it.
//  - Shift + left click on a stack: split amount; Shift + right click on a bag item: link it into chat (hooks.onLink);
//    Ctrl + right click on a bag / worn item: its sockets (ItemChanges.openSockets, then hooks.onSockets); Ctrl + drop
//    on another bag item: combine (after the desktop's question); right click: MirItemCell.UseItem / RemoveItem.
// On drop the source cell is re-read (the item must still be the one picked up: the mock's stale-stack bug), locked
// cells show and refuse, and every request goes through a SendPacer (src/game/sendPacer.ts: burst 5, then ~3 a second;
// MaxPacket is 50 socket reads per 5 s and players behind WebRelay share one IP). Queued requests show their cells busy.

import type { UserItem } from "../../../protocol/server.ts";
import { MirGridType } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import type { Grid } from "../../../game/itemPick.ts";
import { ItemDrop, type DropPrompts } from "../../../game/itemDrop.ts";
import { SendPacer } from "../../../game/sendPacer.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, later, rafCoalesce, setText } from "./dom.ts";
import { scaleOf } from "./layers.ts";
import type { UiRuntime } from "./runtime.ts";
import { itemCells, type ItemCell, type ItemCellDeps, type ItemCells } from "./itemCells.ts";
import {
  cellKey, itemAt, routeDrop, routeSplit, routeUse, sameCell, split, worldDrop,
  type CellRef, type CustomGrid, type CustomTarget, type Verdict,
} from "./dragRoutes.ts";

const CSS = `
.hudm.dragging, .hudm.dragging * { cursor: grabbing !important; }
.hudm .icell.dz-src > i { opacity: .3; }
.hudm .icell.dz-ok { outline: 2px solid var(--ok); outline-offset: -2px; }
.hudm .icell.dz-no { outline: 2px solid var(--bad); outline-offset: -2px; }
.hudm .icell.dz-hint { outline: 1px dashed color-mix(in srgb, var(--ok) 70%, transparent); outline-offset: -2px; }
/* The ghost and label move by transform on their own compositor layers (will-change): a move repaints nothing. */
.hudm .dghost { position: absolute; left: 0; top: 0; width: 44px; height: 44px; margin: -22px 0 0 -22px; pointer-events: none; will-change: transform;
  border-radius: 6px; background: center / auto no-repeat; image-rendering: pixelated; filter: drop-shadow(0 6px 8px rgba(0,0,0,.8)); }
.hudm .dghost b { position: absolute; right: 2px; bottom: 0; font-size: 11px; font-weight: 700; color: #fff; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .dlabel { position: absolute; left: 0; top: 0; pointer-events: none; will-change: transform; padding: 3px 9px; border-radius: 10px;
  font-size: 11px; font-weight: 700; white-space: nowrap; color: #fff; background: rgba(10,14,22,.92); border: 1px solid var(--fr); box-shadow: 0 4px 12px rgba(0,0,0,.6); }
.hudm .dlabel.ok { border-color: var(--ok); color: color-mix(in srgb, var(--ok) 40%, #fff); }
.hudm .dlabel.no { border-color: var(--bad); color: var(--bad-soft); }
.hudm .dtoast { position: absolute; pointer-events: none; padding: 5px 12px; border-radius: 12px; font-size: 12px; font-weight: 700; white-space: nowrap;
  color: var(--bad-soft); background: rgba(60,10,10,.92); border: 1px solid var(--bad); animation: hudm-dtoast 1.6s ease-out forwards; }
@keyframes hudm-dtoast { 0% { opacity: 0; transform: translateY(6px); } 15%, 75% { opacity: 1; transform: none; } 100% { opacity: 0; } }
`;

/** Pixels of movement that turn a press into a drag. */
const DRAG_START = 5;

/** Hooks the integrator / other windows set. */
export interface DragHooks {
  /** Shift + right click on a bag / belt item (MirItemCell): link it into the chat box (ChatLog.addLink + the chat view's text). */
  onLink?(item: UserItem, cell: CellRef): void;
  /** Ctrl + right click opened an item's sockets (ItemChanges.openSockets returned true): show the socket window. */
  onSockets?(item: UserItem): void;
  /** An item was picked up / dragged (MirItemCell PlayItemSound: SoundManager + itemSound by ItemType). */
  onPickSound?(item: UserItem): void;
}

/** A left click on a filled cell with nothing picked: return true when it was used (NPCDropDialog's bag-click target). */
export type CellClickHook = (cell: CellRef, item: UserItem) => boolean;

export type DragDeps = ItemCellDeps;

interface Press { cell: ItemCell; ref: CellRef; x: number; y: number; shift: boolean; ctrl: boolean; button: number }
interface Drag { from: CellRef; id: bigint; split: boolean; src: ItemCell; over: ItemCell | null }

/** The runtime's drag / pick controller. One per UiRuntime: dragController(rt, deps). */
export class DragController {
  /** True while an item is dragged or picked (the HUD hides hover hints). */
  readonly active = new Emitter<boolean>();
  hooks: DragHooks = {};
  private readonly cells: ItemCells;
  private readonly signal: AbortSignal;
  private readonly pacer: SendPacer;
  private readonly jobs = new Map<Uint8Array, { run: () => void; keys: string[] }>();
  private readonly queuedKeys = new Map<string, number>();
  private readonly clickHooks: CellClickHook[] = [];
  private readonly itemDrop: ItemDrop;
  private press: Press | null = null;
  private drag: Drag | null = null;
  private ghost: HTMLElement | null = null;
  private ghostCount: HTMLElement | null = null;
  private label: HTMLElement | null = null;
  private ghostImg = -2;
  /** The drag layer box and scale, measured once per gesture (and after a layout change). */
  private box: { left: number; top: number; s: number } | null = null;
  private last: [number, number] = [0, 0];
  private hinted: ItemCell[] = [];
  private swallowClick = false;
  private wasActive = false;
  private readonly follow: (x: number, y: number) => void;

  constructor(private readonly rt: UiRuntime, deps: DragDeps) {
    injectCss("core-drag", CSS);
    this.signal = rt.signal;
    this.cells = itemCells(rt, deps);
    this.cells.busy = (r) => this.queuedKeys.has(cellKey(r));
    this.pacer = new SendPacer((tok) => this.runJob(tok), () => deps.now());
    const m = deps.models;
    this.itemDrop = new ItemDrop({ pick: m.itemPick, inventory: m.inventory, itemInfos: m.itemInfos, hero: m.hero, heroItems: m.heroItems }, this.prompts());

    const sig = { signal: this.signal };
    const root = rt.root;
    root.addEventListener("pointerdown", (e) => this.down(e), sig);
    root.addEventListener("contextmenu", (e) => this.context(e), sig);
    this.follow = rafCoalesce((x: number, y: number) => this.move(x, y), this.signal);
    window.addEventListener("pointermove", (e) => {
      const pk = this.cells.deps.models.itemPick, ghost = this.picking || pk.gold || pk.hudGold; // picked gold has a ghost too
      if (!this.press && !this.drag && !ghost) return;
      this.last = [e.clientX, e.clientY];
      if (this.press && !this.drag && e.buttons === 0) { this.press = null; return; } // released outside the page
      if (this.drag && e.buttons === 0) { this.cancel(); return; }
      if (this.press && !this.drag && Math.hypot(e.clientX - this.press.x, e.clientY - this.press.y) > DRAG_START) this.start();
      else if (this.drag || ghost) this.follow(e.clientX, e.clientY);
    }, sig);
    window.addEventListener("pointerup", (e) => this.up(e), sig);
    window.addEventListener("pointercancel", () => { this.press = null; this.cancel(); }, sig);
    window.addEventListener("blur", () => { this.press = null; this.cancel(); }, sig);
    // Swallow the click that ends a drag (it would act on the cell / control under the pointer).
    window.addEventListener("click", (e) => { if (this.swallowClick) { e.stopPropagation(); e.preventDefault(); this.swallowClick = false; } }, { capture: true, signal: this.signal });
    rt.layoutChanged.on(() => { this.box = null; });
    window.addEventListener("resize", () => { this.box = null; }, sig);
    m.itemPick.changed.on(() => this.pickChanged());
    this.cells.rendered.on(() => this.recheck());
    this.signal.addEventListener("abort", () => this.dispose(), { once: true });
  }

  /** The cell service (cells are created through it: controller.cells.cell(grid, index, opts)). */
  get itemCells(): ItemCells { return this.cells; }
  /** Whether an item is being dragged. */
  get dragging(): boolean { return !!this.drag; }
  /** Whether a cell is picked (the desktop pick). */
  get picking(): boolean { return !!this.cells.deps.models.itemPick.cell; }

  // — Public API —

  /** A window's custom drop-only target ("x:<name>"); returns its removal. */
  addTarget(grid: CustomGrid, target: CustomTarget): () => void { return this.cells.addTarget(grid, target); }

  /** A left-click hook on filled cells with nothing picked (the NPC drop panel); returns its removal. Last added runs first. */
  addClickHook(fn: CellClickHook): () => void {
    this.clickHooks.unshift(fn);
    return () => { const i = this.clickHooks.indexOf(fn); if (i >= 0) this.clickHooks.splice(i, 1); };
  }

  /** GameUi.mapMouseDown (left button): drops the picked item / gold on the world (itemDrop.ts). True = consumed. */
  mapClick(): boolean {
    if (this.drag) return true;
    const m = this.cells.deps.models;
    const pick = m.itemPick.cell;
    if (pick && pick.grid !== "inv" && pick.grid !== "heroInv") { m.itemPick.clear(); return true; } // GameScene only clears other picks
    return this.itemDrop.mapClick() || this.itemDrop.dropGold();
  }

  /** The router's "modal" layer: Esc cancels a drag or clears the pick. */
  handleKey(e: KeyboardEvent): boolean {
    if (e.type !== "keydown" || e.key !== "Escape") return false;
    if (this.drag) { this.cancel(); e.preventDefault(); return true; }
    const m = this.cells.deps.models.itemPick;
    if (m.cell || m.gold || m.hudGold) { m.clear(); m.setGold(false); m.setHudGold(false); e.preventDefault(); return true; }
    return false;
  }

  /** Sends a model request through the pacer; `cells` show busy until it has gone out. */
  request(run: () => void, cells: readonly CellRef[] = []): void {
    if (this.signal.aborted) return;
    const tok = new Uint8Array(0);
    const keys = cells.map(cellKey);
    for (const k of keys) this.queuedKeys.set(k, (this.queuedKeys.get(k) ?? 0) + 1);
    this.jobs.set(tok, { run, keys });
    this.pacer.send(tok);
    if (keys.length && this.jobs.has(tok)) this.cells.refresh(); // still queued: show it
  }

  /** Cancels the drag (the pick stays). */
  cancel(): void {
    if (!this.drag) return;
    this.endDrag();
  }

  /** A short refusal near the pointer (drag layer). */
  toast(text: string, clientX = this.last[0], clientY = this.last[1]): void {
    if (!text || this.signal.aborted) return;
    const [x, y] = this.local(clientX, clientY);
    const t = h("div", "dtoast", text);
    t.setAttribute("role", "status");
    t.style.left = x + 14 + "px";
    t.style.top = y - 30 + "px";
    this.rt.layers.drag.append(t);
    later(() => t.remove(), 1700, this.signal);
  }

  dispose(): void {
    this.endDrag();
    this.removeGhost();
    this.pacer.dispose();
    this.jobs.clear();
    this.queuedKeys.clear();
    this.clickHooks.length = 0;
  }

  // — Pointer —

  private down(e: PointerEvent): void {
    const cell = this.cells.cellOf(e.target);
    if (!cell || cell.opts.readOnly || this.drag) return;
    if (e.button !== 0) return; // right clicks: contextmenu
    e.preventDefault(); // no text selection / native image drag
    this.last = [e.clientX, e.clientY];
    this.press = { cell, ref: cell.ref, x: e.clientX, y: e.clientY, shift: e.shiftKey, ctrl: e.ctrlKey, button: e.button };
  }

  private up(e: PointerEvent): void {
    this.last = [e.clientX, e.clientY];
    if (this.drag) { e.preventDefault(); this.drop(e); return; }
    const p = this.press;
    this.press = null;
    if (!p || e.button !== 0) return;
    const now = this.cells.cellOf(document.elementFromPoint(e.clientX, e.clientY));
    if (now === p.cell) this.click(p.cell, e);
  }

  /** Right click (MirItemCell.OnMouseClick, button Right). */
  private context(e: MouseEvent): void {
    const cell = this.cells.cellOf(e.target);
    if (!cell) return;
    e.preventDefault();
    e.stopPropagation();
    if (cell.opts.readOnly || this.drag) return;
    const d = this.cells.deps, m = d.models, ref = cell.ref, item = itemAt(d, ref);
    this.last = [e.clientX, e.clientY];
    if (e.shiftKey && ref.grid === "inv" && item && !m.itemPick.cell) { this.hooks.onLink?.(item, ref); return; }
    if (e.ctrlKey) {
      // OpenItem: the socket dialog for a bag / worn item (never uses it)
      if (!item || (ref.grid !== "inv" && ref.grid !== "eq") || this.cells.isPicked(ref)) return;
      m.itemPick.clear();
      if (m.itemChanges.openSockets(item)) this.hooks.onSockets?.(item);
      return;
    }
    m.itemPick.clear();
    const v = routeUse(d, ref);
    if (v.ok && v.run) this.request(v.run, [ref]);
  }

  /** A left click without a drag: the desktop pick / place (InventoryWindow.leftClick). */
  private click(cell: ItemCell, e: PointerEvent): void {
    const d = this.cells.deps, m = d.models, ref = cell.ref, item = itemAt(d, ref);
    const pick = m.itemPick;
    if (pick.gold || pick.hudGold) return; // GameScene.PickedUpGold: cell clicks do nothing
    if (!pick.cell) {
      if (e.shiftKey && item) { this.askSplit(ref, item); return; }
      if (item && ref.grid === "inv") for (const fn of this.clickHooks) if (fn(ref, item)) return;
      if ((ref.grid === "heroHP" || ref.grid === "heroMP") && item) { // a filled auto-pot cell, nothing picked: clear it
        const grid = ref.grid === "heroHP" ? MirGridType.HeroHPItem : MirGridType.HeroMPItem;
        this.request(() => m.heroItems.clearAutoPotItem(grid));
        return;
      }
      if (item && this.cells.pickable(cell)) { // pickable = a filled, unlocked cell of a grid with move cases (an ItemPick grid)
        this.hooks.onPickSound?.(item);
        this.box = null;
        pick.set({ grid: ref.grid as Grid, index: ref.index });
      }
      return;
    }
    const from: CellRef = pick.cell;
    pick.clear();
    if (sameCell(from, ref)) return;
    this.apply(from, cell, this.verdict(from, cell, e.ctrlKey), e.clientX, e.clientY);
  }

  // — Drag —

  private start(): void {
    const p = this.press!;
    this.press = null;
    const d = this.cells.deps, m = d.models, item = itemAt(d, p.ref);
    if (!item || !this.cells.pickable(p.cell) || m.itemPick.cell || m.itemPick.gold || m.itemPick.hudGold) return;
    const splitting = p.shift && (p.ref.grid === "inv" || p.ref.grid === "npcStorage") && item.Count > 1;
    this.drag = { from: p.ref, id: item.UniqueID, split: splitting, src: p.cell, over: null };
    this.hooks.onPickSound?.(item);
    this.box = null;
    const hints = this.hintCells(); // every layout read before the first write
    this.local(this.last[0], this.last[1]);
    p.cell.el.classList.add("dz-src");
    for (const c of hints) c.el.classList.add("dz-hint");
    this.hinted = hints;
    this.rt.root.classList.add("dragging");
    this.cells.suppressHover(true);
    this.showGhost(item);
    this.setActive();
    this.move(this.last[0], this.last[1]);
  }

  /** Outlines the cells outside the source grid and the bag that would take the item (ring slots for a ring, the belt
   *  for a potion, the open trade / storage), measured once at drag start. */
  private hintCells(): ItemCell[] {
    const d = this.drag!, out: ItemCell[] = [];
    for (const c of this.cells.all()) {
      const g = c.ref.grid;
      if (c.opts.readOnly || g === d.from.grid || g === "inv" || !c.el.isConnected || c.el.offsetParent === null) continue;
      if (this.verdict(d.from, c, false).ok) out.push(c);
    }
    return out;
  }

  /** What dropping the dragged / picked item on `cell` does (the cell's own filter first, then the routes). */
  private verdict(from: CellRef, cell: ItemCell, ctrl: boolean): Verdict {
    const d = this.cells.deps;
    if (cell.opts.readOnly) return { ok: false, label: "" };
    if (this.drag?.split) {
      if (cell.ref.grid !== from.grid) return { ok: false, label: "Split goes to a free cell of the same grid" };
      return routeSplit(d, from);
    }
    const item = itemAt(d, from);
    if (item && !sameCell(from, cell.ref)) {
      const why = cell.refusal(item);
      if (why) return { ok: false, label: why };
    }
    return routeDrop(d, from, cell.ref, { ctrl });
  }

  private move(x: number, y: number): void {
    const dr = this.drag;
    // Hit-test before any write (one hit test, no forced layout); the ghost and label are pointer-events: none.
    const hit = dr ? document.elementFromPoint(x, y) : null;
    const [lx, ly] = this.local(x, y);
    if (this.ghost) this.ghost.style.transform = `translate(${lx}px, ${ly}px) scale(1.12) rotate(-4deg)`;
    if (!dr) return;
    const cell = this.cells.cellOf(hit) ?? null;
    if (dr.over && dr.over !== cell) dr.over.el.classList.remove("dz-ok", "dz-no");
    dr.over = cell;
    let text = "", ok = false;
    if (cell && cell !== dr.src) {
      const v = this.verdict(dr.from, cell, false);
      ok = v.ok; text = v.label;
      cell.el.classList.toggle("dz-ok", ok);
      cell.el.classList.toggle("dz-no", !ok && !!text);
    } else if (!cell && this.overWorld(hit)) {
      const why = worldDrop(this.cells.deps, dr.from);
      if (why !== null && !dr.split) { ok = !why; text = why || "Drop on the ground"; }
    }
    const lb = this.label!;
    lb.hidden = !text;
    setText(lb, text);
    const cls = "dlabel " + (ok ? "ok" : "no");
    if (lb.className !== cls) lb.className = cls;
    if (text) lb.style.transform = `translate(${lx + 20}px, ${ly + 16}px)`;
  }

  private drop(e: PointerEvent): void {
    const dr = this.drag!;
    this.swallowClick = true;
    later(() => { this.swallowClick = false; }, 0, this.signal);
    const hit = document.elementFromPoint(e.clientX, e.clientY);
    const cell = this.cells.cellOf(hit);
    const world = !cell && this.overWorld(hit);
    this.endDrag();
    const d = this.cells.deps, m = d.models;
    // The source must still hold the item that was picked up (a potion drunk, an item moved by the server meanwhile).
    const now = itemAt(d, dr.from);
    if (!now || now.UniqueID !== dr.id) { this.toast("It's no longer there", e.clientX, e.clientY); return; }
    if (cell) {
      if (cell === dr.src) return;
      if (dr.split) {
        const v = cell.ref.grid === dr.from.grid ? routeSplit(d, dr.from) : { ok: false, label: "" };
        if (v.ok) this.askSplit(dr.from, now); else this.toast(v.label, e.clientX, e.clientY);
        return;
      }
      this.apply(dr.from, cell, this.verdict(dr.from, cell, e.ctrlKey), e.clientX, e.clientY);
      return;
    }
    if (world && !dr.split) {
      const why = worldDrop(d, dr.from);
      if (why === null) return;
      if (why) { this.toast(why, e.clientX, e.clientY); return; }
      m.itemPick.set({ grid: dr.from.grid as Grid, index: dr.from.index }); // ItemDrop reads the pick
      this.itemDrop.mapClick();
    }
  }

  /** Runs a verdict: the question first when it has one, then the paced request; a refusal shows as a toast. */
  private apply(from: CellRef, cell: ItemCell, v: Verdict, x: number, y: number): void {
    if (!v.ok || !v.run) { if (v.label) this.toast(v.label, x, y); return; }
    const run = v.run, cells = [from, cell.ref];
    if (!v.ask) { this.request(run, cells); return; }
    const id = itemAt(this.cells.deps, from)?.UniqueID;
    void this.rt.popups.ask(v.ask, { kind: "question" }).then((yes) => {
      if (!yes || this.signal.aborted) return;
      if (itemAt(this.cells.deps, from)?.UniqueID !== id) { this.toast("It's no longer there"); return; }
      this.request(run, cells);
    });
  }

  /** MirItemCell Shift+left: MirAmountBox (Split Amount, up to Count - 1), then C.SplitItem. */
  private askSplit(ref: CellRef, item: UserItem): void {
    const d = this.cells.deps;
    const v = routeSplit(d, ref);
    if (!v.ok) { if (v.label) this.toast(v.label); return; }
    const info = d.models.itemInfos.get(item.ItemIndex);
    const id = item.UniqueID;
    void this.rt.popups.amount({
      title: "Split Amount:", max: item.Count - 1, min: 1, value: Math.max(1, Math.floor(item.Count / 2)),
      item: { name: info?.Name ?? "", img: info?.Image ?? -1, count: item.Count }, leftLabel: "stay in the stack",
    }).then((n) => {
      if (n === null || n <= 0 || this.signal.aborted) return;
      const now = itemAt(d, ref);
      if (!now || now.UniqueID !== id) { this.toast("It's no longer there"); return; }
      if (n >= now.Count) return;
      this.request(() => { const err = split(d, ref, n); if (err) this.toast(err); }, [ref]);
    });
  }

  private endDrag(): void {
    const dr = this.drag;
    if (!dr) return;
    this.drag = null;
    dr.src.el.classList.remove("dz-src");
    dr.over?.el.classList.remove("dz-ok", "dz-no");
    for (const c of this.hinted) c.el.classList.remove("dz-hint");
    this.hinted = [];
    this.rt.root.classList.remove("dragging");
    if (this.label) this.label.hidden = true;
    if (!this.picking) { this.removeGhost(); this.cells.suppressHover(false); }
    this.setActive();
  }

  // — Pick —

  private pickChanged(): void {
    const m = this.cells.deps.models, cell = m.itemPick.cell;
    if (this.drag) { this.setActive(); return; }
    const item = cell ? itemAt(this.cells.deps, cell) : null;
    const gold = m.itemPick.gold || m.itemPick.hudGold;
    if (item || gold) {
      this.cells.suppressHover(true);
      this.showGhost(item, gold);
      this.move(this.last[0], this.last[1]);
    } else {
      this.removeGhost();
      this.cells.suppressHover(false);
    }
    this.setActive();
  }

  /** After each cell render pass: a drag whose source item left ends; a pick whose cell emptied clears. */
  private recheck(): void {
    const d = this.cells.deps;
    const dr = this.drag;
    if (dr) {
      const now = itemAt(d, dr.from);
      if (!now || now.UniqueID !== dr.id) { this.endDrag(); this.toast("It's no longer there"); }
      return;
    }
    const pick = d.models.itemPick.cell;
    if (pick) {
      const item = itemAt(d, pick);
      if (!item) d.models.itemPick.clear();
      else this.showGhost(item); // a count change redraws the ghost's number
    }
  }

  // — Ghost / helpers —

  private showGhost(item: UserItem | null, gold = false): void {
    if (!this.ghost) {
      this.ghostCount = h("b");
      this.ghost = h("div", "dghost", this.ghostCount);
      this.label = h("div", "dlabel");
      this.label.hidden = true;
      this.label.setAttribute("aria-live", "polite");
      this.rt.layers.drag.append(this.ghost, this.label);
      this.ghostImg = -2;
    }
    const info = item ? this.cells.deps.models.itemInfos.get(item.ItemIndex) : undefined;
    const img = gold ? 116 : info?.Image ?? -1; // Items 116: the gold icon (GameScene.DrawControl)
    const stack = !!item && !!info && info.StackSize > 1;
    this.ghostCount!.hidden = !stack;
    if (stack) setText(this.ghostCount!, String(item!.Count));
    if (img === this.ghostImg) return;
    this.ghostImg = img;
    const g = this.ghost;
    g.style.backgroundImage = "";
    if (img >= 0) void libPicture(Lib.Items, img).then((pic) => { if (pic && this.ghost === g && this.ghostImg === img) g.style.backgroundImage = `url(${pic.url})`; });
  }

  private removeGhost(): void {
    this.ghost?.remove();
    this.label?.remove();
    this.ghost = this.label = this.ghostCount = null;
    this.ghostImg = -2;
  }

  /** A pointer position in the drag layer's own (HUD) pixels; the layer box is measured once per gesture. */
  private local(clientX: number, clientY: number): [number, number] {
    if (!this.box) {
      const el = this.rt.layers.drag, r = el.getBoundingClientRect();
      this.box = { left: r.left, top: r.top, s: scaleOf(el, r) || 1 };
    }
    const b = this.box;
    return [(clientX - b.left) / b.s, (clientY - b.top) / b.s];
  }

  /** The world under the pointer: inside the game stage but not on the UI (the root's layers take no pointer events). */
  private overWorld(hit: Element | null): boolean {
    const root = this.rt.root, stage = root.parentElement;
    return !!hit && !!stage && stage.contains(hit) && !root.contains(hit) && !this.rt.popups.open;
  }

  private setActive(): void {
    const on = !!this.drag || this.picking;
    if (on === this.wasActive) return;
    this.wasActive = on;
    this.active.emit(on);
  }

  private runJob(tok: Uint8Array): void {
    const job = this.jobs.get(tok);
    if (!job) return;
    this.jobs.delete(tok);
    for (const k of job.keys) {
      const n = (this.queuedKeys.get(k) ?? 1) - 1;
      if (n > 0) this.queuedKeys.set(k, n); else this.queuedKeys.delete(k);
    }
    try { job.run(); } catch (err) { console.error(err); }
    if (job.keys.length) this.cells.refresh();
  }

  /** The YES / NO, amount and refusal prompts itemDrop.ts asks with (the new UI's popups). */
  private prompts(): DropPrompts {
    const p = this.rt.popups;
    return {
      confirm: (text, yesFn) => { void p.ask(text, { kind: "question" }).then((y) => { if (y) yesFn(); }); },
      amount: (title, image, max, ok) => { void p.amount({ title, max, item: image >= 0 ? { name: "", img: image } : undefined }).then((n) => { if (n !== null) ok(n); }); },
      refuse: (text) => { void p.confirm({ text, kind: "warning", buttons: "ok" }); },
    };
  }
}

/** The runtime's drag controller (built on first use; `deps` is required the first time). */
export function dragController(rt: UiRuntime, deps?: DragDeps): DragController {
  return rt.service("drag", (r) => {
    if (!deps) throw new Error("dragController: the first call needs the models");
    return new DragController(r, deps);
  });
}

