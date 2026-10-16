// Shared window base for the scene lab's UI tester windows (hudCharacter.ts, hudInventory.ts). The windows live in the
// HUD mock's `.wins` layer inside `.hudm`, so they take its theme variables (--pa / --pb / --pc panels, --fr frame,
// --ac accent, --tx text, --dim labels, --rad corners, --gl glow). Look (the user's picks, 2026-10-15): an accent title
// on a lighter bar with an accent line under it (strongest in the middle, fading to both edges), the page below it
// darkening towards the bottom, a close cross, drag by the title, item cells like the HUD's slots.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";

const CSS = `
.hudm .wins { position: absolute; left: 0; top: 0; transform-origin: 0 0; pointer-events: none; }
.hudm .hwin { position: absolute; pointer-events: auto; color: var(--tx);
  --page: linear-gradient(180deg, color-mix(in srgb, var(--pb) 85%, var(--pa)) 0%, color-mix(in srgb, var(--pb) 68%, #000) 30%,
    color-mix(in srgb, var(--pb) 42%, #000) 70%, color-mix(in srgb, var(--pb) 25%, #000) 100%); }
.hudm .hwin .panel { position: relative; z-index: 1; border-radius: var(--rad); background: var(--page); border: 1px solid var(--fr);
  box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 0 10px 30px rgba(0,0,0,.7); }
.hudm .hwin .title { position: relative; height: 30px; display: flex; align-items: center; justify-content: center; cursor: move; user-select: none;
  font-size: 13px; font-weight: 700; letter-spacing: .28em; text-indent: .28em; color: var(--ac); text-shadow: 0 0 calc(8px * var(--gl)) var(--glow);
  background: linear-gradient(180deg, var(--pc), var(--pa)); border-radius: calc(var(--rad) - 1px) calc(var(--rad) - 1px) 0 0; }
.hudm .hwin .title::after { content: ""; position: absolute; left: 0; right: 0; bottom: 0; height: 2px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 35%, transparent) 22%, var(--ac) 50%, color-mix(in srgb, var(--ac) 35%, transparent) 78%, transparent);
  filter: drop-shadow(0 0 calc(4px * var(--gl)) var(--ac)); }
.hudm .hwin .x { position: absolute; right: 8px; top: 6px; width: 18px; height: 18px; display: flex; align-items: center; justify-content: center;
  font-size: 16px; line-height: 1; color: var(--dim); cursor: pointer; border-radius: calc(var(--rad) * .6); letter-spacing: 0; text-indent: 0; text-shadow: none; }
.hudm .hwin .x:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .hwin .eq { position: relative; width: 44px; height: 44px; flex: none; cursor: pointer; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hwin .eq::after { content: ""; position: absolute; inset: 1px; border-radius: calc(var(--rad) - 1px); pointer-events: none;
  box-shadow: inset 0 0 0 1px rgba(0,0,0,.9), inset 0 0 6px 1px rgba(0,0,0,.75); }
.hudm .hwin .eq i { position: absolute; inset: 0; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .hwin .eq b { position: absolute; right: 3px; bottom: 1px; z-index: 1; font-size: 10px; font-weight: 700; color: #fff; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .hwin .eq:hover { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .hwin .eq.sel { border-color: var(--ac); box-shadow: 0 0 0 1px #000, inset 0 0 10px var(--glow); }
.hudm .hwin .eq.empty { cursor: default; }
.hudm .hwin .eq.empty:hover { border-color: var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hwin .btn { height: 22px; padding: 0 12px; display: inline-flex; align-items: center; justify-content: center; cursor: pointer; user-select: none;
  font-size: 11px; font-weight: 600; letter-spacing: .06em; color: var(--tx); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8);
  background: linear-gradient(180deg, var(--pc), var(--pb)); box-shadow: 0 0 0 1px #000; }
.hudm .hwin.front .panel { border-color: color-mix(in srgb, var(--ac) 55%, var(--fr)); box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 0 10px 30px rgba(0,0,0,.7), 0 0 calc(14px * var(--gl)) color-mix(in srgb, var(--ac) 22%, transparent); }
/* While dragged, a window moves by transform on its own layer (no relayout, no repaint of its big shadow per move). */
.hudm .hwin.wdrag { will-change: transform; }
.hudm .wguide { position: absolute; z-index: 100000; pointer-events: none; background: var(--ac); box-shadow: 0 0 6px var(--glow); opacity: .85; }
.hudm .wguide.v { top: 0; bottom: 0; width: 1px; } .hudm .wguide.h { left: 0; right: 0; height: 1px; }
.hudm .hwin .btn:hover { border-color: var(--ac); color: #fff; box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
`;

export const h = <K extends keyof HTMLElementTagNameMap>(tag: K, cls = "", ...kids: (Node | string)[]): HTMLElementTagNameMap[K] => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  n.append(...kids);
  return n;
};

export function injectCss(id: string, css: string): void {
  if (document.getElementById(id)) return;
  const style = document.createElement("style");
  style.id = id;
  style.textContent = css;
  document.head.append(style);
}

// — Performance helpers (Knowledgebase/Future Ideas/SceneLab-UI-Review/Performance.md) —

const lastText = new WeakMap<Node, string>();
/** Sets `textContent` only when the string changes: assigning the same text still replaces the text node and dirties
 *  layout, which adds up for per-frame HUD text. Keeps its own copy, so the DOM is never read back. */
export function setText(el: Node, s: string): void {
  if (lastText.get(el) === s) return;
  lastText.set(el, s);
  el.textContent = s;
}

/** Restarts a CSS animation started by `cls` without the `void offsetWidth` reflow hack (a forced layout, often in
 *  the middle of the frame's DOM writes): the class comes off now and back on two frame callbacks later, so at least
 *  one style pass sees it removed whether this runs in an event handler or in the frame loop. Not set yet: it goes on
 *  at once. */
export function restartAnim(el: Element, cls: string): void {
  if (!el.classList.contains(cls)) { el.classList.add(cls); return; }
  el.classList.remove(cls);
  requestAnimationFrame(() => requestAnimationFrame(() => el.classList.add(cls)));
}

/** Wraps a high-rate handler (mousemove, input) so it runs at most once a frame, with the latest arguments. */
export function rafCoalesce<A extends unknown[]>(fn: (...args: A) => void): (...args: A) => void {
  let pending: A | null = null;
  return (...args: A) => {
    if (!pending) requestAnimationFrame(() => { const a = pending!; pending = null; fn(...a); });
    pending = args;
  };
}

/** An interval that only runs while `on()` is called and stops on `off()` (start it in a window's shown(), stop it in
 *  closed()), so closed windows cost nothing. `on()` while running does nothing. */
export function windowInterval(fn: () => void, ms: number): { on(): void; off(): void } {
  let id = 0;
  return {
    on() { if (!id) id = window.setInterval(fn, ms); },
    off() { if (id) { window.clearInterval(id); id = 0; } },
  };
}

/** Glow via opacity: `.halo` is a child whose outer box-shadow (`--halo`) stays fixed and only its opacity pulses,
 *  which runs on the compositor; animating box-shadow itself repaints every frame. The parent must not clip it
 *  (no overflow: hidden). `--halo-t` sets the period. */
const HALO_CSS = `
.hudm .halo { position: absolute; inset: -1px; border-radius: inherit; pointer-events: none; opacity: 0; box-shadow: var(--halo, none);
  animation: hudm-halo var(--halo-t, 1.6s) ease-in-out infinite; }
@keyframes hudm-halo { 50% { opacity: 1; } }
`;
injectCss("hudm-halo-css", HALO_CSS);

/** An item cell: Items.Lib icon at 1:1, optional stack count, name on hover; click selects (one per window). */
export function itemCell(owner: HTMLElement, title: string, image: number, count = 0): HTMLElement {
  const i = h("i");
  const el = h("div", "eq", i);
  if (image < 0) { el.classList.add("empty"); return el; }
  el.title = title;
  el.dataset.item = title.replace(/^[^:]+: /, "").replace(/ \(\d+\)$/, ""); // the item tooltip (hudTooltip.ts) looks it up by name
  if (count > 1) el.dataset.count = String(count);
  if (count > 1) el.append(h("b", "", String(count)));
  void libPicture(Lib.Items, image).then((pic) => { if (pic) i.style.backgroundImage = `url(${pic.url})`; });
  el.addEventListener("click", () => {
    for (const s of owner.querySelectorAll(".eq.sel")) if (s !== el) s.classList.remove("sel");
    el.classList.toggle("sel");
  });
  return el;
}

/** The lab's window manager (2026-10-15): z-order (a click or opening brings a window to the front, which gets a glow),
 *  Esc closes the front window and Shift+Esc all of them (the desktop's Close All bind is Escape), magnetic snapping to
 *  the layer's edges and centre and to other open windows while dragging (with a guide line; hold Alt to move freely),
 *  windows kept on screen (on open, on drag and on window resize), positions remembered per window in localStorage
 *  "crystal.lab.windows" (lab only, never the game's settings), double-click a title to put the window back. */
const STORE = "crystal.lab.windows", SNAP = 10, KEEP = 60;
/** Window z-order band: below the full-screen scenes (login / select z 40, loading 41) and the item tooltip (50). front()
 *  renumbers the open windows back into the band when it fills up, so the z-index never creeps above those layers. */
const Z_BASE = 10, Z_MAX = 39;
const saved: Record<string, { x: number; y: number }> = (() => { try { return JSON.parse(localStorage.getItem(STORE) ?? "{}") as Record<string, { x: number; y: number }>; } catch { return {}; } })();
const save = () => { try { localStorage.setItem(STORE, JSON.stringify(saved)); } catch { /* storage blocked: positions just aren't kept */ } };
let zTop = Z_BASE;
const all: HudWindow[] = [];
let keysHooked = false;
/** Full-screen scenes own the keyboard while they show. */
const sceneUp = () => !!document.querySelector(".hudm .lgscr:not([hidden]), .hudm .slscr:not([hidden]), .hudm .ldscr:not([hidden])");

export abstract class HudWindow {
  readonly el: HTMLElement;
  /** Position in the windows layer; x = null puts the window `right` px from the layer's right edge on first show. */
  protected x: number | null;
  protected y: number;
  private readonly wmKey: string;
  private readonly wmStart: [number | null, number];
  private wmFitted = false;

  constructor(cls: string, x: number | null, y: number, private readonly right = 40) {
    injectCss("hudm-win-css", CSS);
    this.el = h("div", "hwin " + cls);
    this.el.hidden = true;
    this.wmKey = cls.split(" ")[0]!;
    this.wmStart = [x, y];
    const p = saved[this.wmKey];
    this.x = p ? p.x : x;
    this.y = p ? p.y : y;
    all.push(this);
    this.el.addEventListener("mousedown", () => this.front(), true);
    if (!keysHooked) { keysHooked = true; HudWindow.hookKeys(); }
  }

  get open(): boolean { return !this.el.hidden; }

  /** Whether this is the front open window of a showing HUD (keys meant for one window check it). */
  get isFront(): boolean { return this.open && !this.el.closest("[hidden]") && HudWindow.topmost() === this; }

  /** Runs after every close (title-bar cross, Esc, Shift+Esc / closeAll, toggle(false) from code). */
  onClose: (() => void) | null = null;

  toggle(show = this.el.hidden): void {
    const was = this.open;
    this.el.hidden = !show;
    if (!show) { HudWindow.markFront(); if (was) { this.closed(); this.onClose?.(); } return; }
    if (this.x === null) this.x = this.wmRightX();
    this.front();
    this.place();
    this.shown();
    // Some windows build their content in shown(): fit them on screen again once they have their real size.
    if (!this.wmFitted) { this.wmFitted = true; window.setTimeout(() => this.place()); }
  }

  /** Called each time the window opens. */
  protected shown(): void {}
  /** Called each time the window closes (any close path, see onClose). */
  protected closed(): void {}

  private wmRightX(): number { return Math.max(0, (this.el.parentElement?.clientWidth ?? 1024) - this.el.offsetWidth - this.right); }

  /** Brings the window to the front. */
  front(): void {
    if (this.el.style.zIndex !== String(zTop)) {
      if (zTop >= Z_MAX) HudWindow.renumber(this);
      else this.el.style.zIndex = String(++zTop);
    }
    HudWindow.markFront();
  }
  /** Packs the open windows back into the z band in their current order, `top` last (closed ones drop their z). */
  private static renumber(top: HudWindow): void {
    const order = all.filter((w) => w !== top && w.open).sort((a, b) => +(a.el.style.zIndex || 0) - +(b.el.style.zIndex || 0));
    zTop = Z_BASE;
    for (const w of order) w.el.style.zIndex = String(zTop = Math.min(Z_MAX - 1, zTop + 1));
    for (const w of all) if (!w.open && w !== top) w.el.style.zIndex = "";
    top.el.style.zIndex = String(zTop = Math.min(Z_MAX, zTop + 1));
  }
  private static markFront(): void {
    const top = HudWindow.topmost();
    for (const w of all) w.el.classList.toggle("front", w === top);
  }
  static topmost(): HudWindow | undefined {
    let best: HudWindow | undefined;
    for (const w of all) if (w.open && (!best || +(w.el.style.zIndex || 0) > +(best.el.style.zIndex || 0))) best = w;
    return best;
  }
  /** Closes every open window (Shift+Esc, the lab's Close all button). */
  static closeAll(): void { for (const w of all) if (w.open) w.toggle(false); }
  /** Whether any window is open (Esc then closes it instead of opening the game menu). */
  static anyOpen(): boolean { return all.some((w) => w.open); }
  /** Keeps every open window on screen after the layer changed size (resolution preview, HUD scale). */
  static reclampAll(): void { for (const w of all) if (w.open) w.place(); }
  /** Forgets every remembered position and puts open windows back where they start. */
  static resetPositions(): void {
    for (const k of Object.keys(saved)) delete saved[k];
    save();
    for (const w of all) { [w.x, w.y] = w.wmStart; if (w.open) { if (w.x === null) w.x = w.wmRightX(); w.place(); } }
  }
  private static hookKeys(): void {
    window.addEventListener("keydown", (e) => {
      if (e.key !== "Escape" || sceneUp() || document.body.classList.contains("hudm-dragging") || (e.target as HTMLElement).closest?.("input, textarea, select")) return;
      const top = HudWindow.topmost();
      if (!top || top.el.closest("[hidden]")) return; // none open, or the HUD is hidden
      e.preventDefault(); e.stopImmediatePropagation();
      if (e.shiftKey) HudWindow.closeAll(); else top.toggle(false);
    });
    // Keep open windows on screen when the layer changes size (resolution previews, auto scale).
    window.addEventListener("resize", () => HudWindow.reclampAll());
  }

  /** Applies x / y, clamped so at least KEEP px of the window, and its title, stay inside the layer. */
  protected place(): void {
    const layer = this.el.parentElement;
    if (layer && this.x !== null) {
      const W = layer.clientWidth || 1024, H = layer.clientHeight || 768, w = this.el.offsetWidth || 300;
      this.x = Math.min(Math.max(this.x, KEEP - w), W - KEEP);
      this.y = Math.min(Math.max(this.y, 0), H - 34);
    }
    this.el.style.left = (this.x ?? 0) + "px";
    this.el.style.top = this.y + "px";
  }

  /** A title bar (accent text, line under it) with the close cross; dragging it moves the window, double-click resets it. */
  protected titleBar(text: string, closeHint: string | null): HTMLElement {
    const t = h("div", "title", text);
    if (closeHint !== null) {
      const close = h("div", "x", "×");
      close.title = closeHint + " · Esc closes the front window, Shift+Esc all";
      close.addEventListener("click", () => this.toggle(false));
      t.append(close);
    }
    t.title = "Drag to move (Alt: no snapping) · double-click to reset";
    t.addEventListener("dblclick", (e) => {
      if ((e.target as HTMLElement).closest(".x")) return;
      delete saved[this.wmKey]; save();
      [this.x, this.y] = this.wmStart;
      if (this.x === null) this.x = this.wmRightX();
      this.place();
    });
    this.drag(t);
    return t;
  }

  /** Drag by a handle (the HUD layer is scaled, so screen pixels are divided by its scale), snapping to the layer's
   *  edges and centre and to other open windows' edges within SNAP px, with a guide line on each snapped axis. */
  protected drag(handle: HTMLElement): void {
    handle.addEventListener("mousedown", (e) => {
      if ((e.target as HTMLElement).closest(".x, input, button, select, textarea")) return;
      e.preventDefault();
      this.front();
      const scale = this.el.getBoundingClientRect().width / this.el.offsetWidth || 1;
      const sx = e.clientX, sy = e.clientY, ox = this.x ?? 0, oy = this.y;
      const layer = this.el.parentElement!;
      const gx = h("div", "wguide v"), gy = h("div", "wguide h");
      gx.hidden = gy.hidden = true;
      layer.append(gx, gy);
      const w = this.el.offsetWidth, hh = this.el.offsetHeight, W = layer.clientWidth, H = layer.clientHeight;
      const xs: number[] = [0, W, W / 2], ys: number[] = [0, H];
      for (const o of all) if (o !== this && o.open) { const l = o.el.offsetLeft, t = o.el.offsetTop; xs.push(l, l + o.el.offsetWidth); ys.push(t, t + o.el.offsetHeight); }
      /** The snapped position for one axis and the line it snapped to (edges and centre of the window against each line). */
      const snap = (v: number, size: number, lines: number[]): [number, number | null] => {
        let best: [number, number | null] = [v, null], d = SNAP;
        for (const L of lines) for (const [edge, pos] of [[v, L], [v + size, L - size], [v + size / 2, L - size / 2]] as const) {
          const dd = Math.abs(edge - L);
          if (dd < d) { d = dd; best = [pos, L]; }
        }
        return best;
      };
      // Moves clamp like place() with the sizes measured above and move the window by transform; left / top are
      // written once on mouseup. No layout reads per move.
      this.el.classList.add("wdrag");
      const move = (m: MouseEvent) => {
        let x = ox + (m.clientX - sx) / scale, y = oy + (m.clientY - sy) / scale, lx: number | null = null, ly: number | null = null;
        if (!m.altKey) { [x, lx] = snap(x, w, xs); [y, ly] = snap(y, hh, ys); }
        x = Math.min(Math.max(x, KEEP - w), W - KEEP);
        y = Math.min(Math.max(y, 0), H - 34);
        this.x = x; this.y = y;
        this.el.style.transform = `translate(${x - ox}px, ${y - oy}px)`;
        gx.hidden = lx === null; if (lx !== null) gx.style.left = lx + "px";
        gy.hidden = ly === null; if (ly !== null) gy.style.top = ly + "px";
      };
      const up = () => {
        window.removeEventListener("mousemove", move); window.removeEventListener("mouseup", up);
        gx.remove(); gy.remove();
        this.el.classList.remove("wdrag"); this.el.style.transform = "";
        this.place();
        if (this.x !== null && (this.x !== ox || this.y !== oy)) { saved[this.wmKey] = { x: Math.round(this.x), y: Math.round(this.y) }; save(); }
      };
      window.addEventListener("mousemove", move);
      window.addEventListener("mouseup", up);
    });
  }
}
