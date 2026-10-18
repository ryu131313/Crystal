// Ported from play/hudWindow.ts (HudWindow and its static window manager): the new UI's window base class and its
// per-runtime manager. Changes from the mock:
//  - no module state: one WindowManager per UiRuntime (z order, the window list, saved positions), dropped on dispose;
//  - no window key listener: the integrator routes keys through the input router (src/game/input.ts) to
//    WindowManager.handleKey (front window's key() hook, then Esc = closeTop, Shift+Esc = closeAll);
//  - drag by pointer events with setPointerCapture (a release outside the browser ends the drag);
//  - positions in localStorage "crystal.ui.windows" (the lab keeps "crystal.lab.windows");
//  - lifecycle helpers: timers / ticker subscriptions that run only while shown, scoped listeners, dirty-when-closed
//    rendering (invalidate / render), dispose.
// Kept: z band 10-39 with renumber, the front glow, magnetic snapping to the layer's edges and centre and to other open
// windows (guide lines; Alt moves freely), windows kept on screen (open, drag, layout change), double-click a title to
// put the window back, closed() / onClose on every close path.

import { Emitter } from "../../../game/emitter.ts";
import { isTextTarget } from "../../../game/input.ts";
import { h, later, rafCoalesce, windowInterval } from "./dom.ts";
import type { Switch } from "./dom.ts";
import type { Layers } from "./layers.ts";
import type { Ticker, FrameFn } from "./ticker.ts";

const STORE = "crystal.ui.windows", SNAP = 10, KEEP = 60, TITLE_KEEP = 34;
/** Window z-order band: below the scenes (40/41) and the tooltip (50) layers. front() renumbers the open windows back
 *  into the band when it fills up, so the z-index never creeps above those layers. */
export const Z_BASE = 10, Z_MAX = 39;

type Pos = { x: number; y: number };

/** What a window needs from its runtime (UiRuntime implements it). */
export interface WindowHost {
  readonly wm: WindowManager;
  readonly layers: Layers;
  readonly ticker: Ticker;
  /** The UI scope: aborts on dispose (log-out). */
  readonly signal: AbortSignal;
}

export class WindowManager {
  private readonly all: UiWindow[] = [];
  private zTop = Z_BASE;
  private readonly saved: Record<string, Pos>;
  /** True while a window is being dragged by its title. */
  dragging = false;
  /** Fires when a window opens or closes (the menu strip's open marks). */
  readonly changed = new Emitter<UiWindow>();

  constructor(readonly layers: Layers, private readonly signal: AbortSignal) {
    this.saved = (() => {
      try {
        const v = JSON.parse(localStorage.getItem(STORE) ?? "{}") as unknown;
        return v && typeof v === "object" ? v as Record<string, Pos> : {};
      } catch { return {}; }
    })();
  }

  /** @internal UiWindow's constructor. */
  add(w: UiWindow): void { this.all.push(w); }
  /** The touch layout (UiRuntime.mobile): windows dock as sheets (no drag, no saved positions). */
  get mobile(): boolean { return this.layers.root.classList.contains("mobile"); }
  /** @internal The touch layout's dock: a window opens on the right; next to another open right-docked window it takes
   *  the left when both fit (the bag beside a merchant / storage), else it covers it. */
  dockX(w: UiWindow, width: number): number {
    const W = this.layers.layout.width;
    for (const o of this.all) {
      if (o === w || !o.open) continue;
      const ow = parseFloat(o.el.dataset.dockW ?? "") || o.el.offsetWidth, ox = parseFloat(o.el.style.left) || 0;
      if (Math.abs(ox + ow - W) < 2 && ow + width <= W) return 0;
    }
    return Math.max(0, W - width);
  }
  /** @internal */
  savedPos(key: string): Pos | undefined {
    const p = this.saved[key];
    return p && Number.isFinite(p.x) && Number.isFinite(p.y) ? p : undefined;
  }
  /** @internal */
  savePos(key: string, p: Pos | null): void {
    if (p) this.saved[key] = { x: Math.round(p.x), y: Math.round(p.y) }; else delete this.saved[key];
    this.persist();
  }
  private persist(): void {
    try { localStorage.setItem(STORE, JSON.stringify(this.saved)); } catch { /* storage blocked: positions just aren't kept */ }
  }
  /** @internal The next z for a window brought to the front. */
  raise(w: UiWindow): void {
    if (w.el.style.zIndex !== String(this.zTop)) {
      if (this.zTop >= Z_MAX) this.renumber(w);
      else w.el.style.zIndex = String(++this.zTop);
    }
    this.markFront();
  }
  /** Packs the open windows back into the z band in their current order, `top` last (closed ones drop their z). */
  private renumber(top: UiWindow): void {
    const order = this.all.filter((w) => w !== top && w.open).sort((a, b) => +(a.el.style.zIndex || 0) - +(b.el.style.zIndex || 0));
    this.zTop = Z_BASE;
    for (const w of order) w.el.style.zIndex = String(this.zTop = Math.min(Z_MAX - 1, this.zTop + 1));
    for (const w of this.all) if (!w.open && w !== top) w.el.style.zIndex = "";
    top.el.style.zIndex = String(this.zTop = Math.min(Z_MAX, this.zTop + 1));
  }
  /** @internal */
  markFront(): void {
    const top = this.topmost();
    for (const w of this.all) w.el.classList.toggle("front", w === top);
  }

  /** The front open window, if any. */
  topmost(): UiWindow | undefined {
    let best: UiWindow | undefined;
    for (const w of this.all) if (w.open && (!best || +(w.el.style.zIndex || 0) > +(best.el.style.zIndex || 0))) best = w;
    return best;
  }
  /** Whether any window is open (Esc then closes it instead of opening the game menu). */
  anyOpen(): boolean { return this.all.some((w) => w.open); }
  /** Every window built so far (lazy windows appear after their first open). */
  get windows(): readonly UiWindow[] { return this.all; }

  /** Closes the front window (Esc). False when none is open or the UI is hidden. */
  closeTop(): boolean {
    if (this.layers.root.hidden) return false;
    const top = this.topmost();
    if (!top) return false;
    top.toggle(false);
    return true;
  }
  /** Closes every open window (Shift+Esc = the desktop's Close All bind, log-out). False when none was open. */
  closeAll(): boolean {
    let any = false;
    for (const w of this.all) if (w.open) { any = true; w.toggle(false); }
    return any;
  }
  /** Keeps every open window on screen after the layer changed size (resolution, HUD scale, narrow layout). */
  reclampAll(): void { for (const w of this.all) if (w.open) w.reclamp(); }
  /** Forgets every remembered position and puts open windows back where they start. */
  resetPositions(): void {
    for (const k of Object.keys(this.saved)) delete this.saved[k];
    this.persist();
    for (const w of this.all) w.resetPosition();
  }

  /** The window layer's key handler, for the input router's "window" layer (the integrator registers it):
   *  1. the front window's key() hook (NPC digit picks, help page arrows, rebind capture, ...);
   *  2. Escape (not repeated): in a window's text field it blurs the field; else Shift+Esc closes all, Esc the front window.
   *  Returns true when consumed (and then calls preventDefault). Does nothing while the UI is hidden or a window drags. */
  handleKey(e: KeyboardEvent): boolean {
    if (this.layers.root.hidden || this.dragging) return false;
    const top = this.topmost();
    if (top) {
      let used = false;
      try { used = top.key(e) === true; } catch (err) { console.error(err); }
      if (used) { e.preventDefault(); return true; }
    }
    if (e.key !== "Escape" || e.repeat || e.ctrlKey || e.altKey || e.metaKey) return false;
    if (isTextTarget(e)) {
      const t = (e.target instanceof HTMLElement ? e.target : document.activeElement) as HTMLElement | null;
      if (t && this.layers.wins.contains(t)) { t.blur(); e.preventDefault(); return true; }
      return false;
    }
    const used = e.shiftKey ? this.closeAll() : this.closeTop();
    if (used) e.preventDefault();
    return used;
  }

  /** Closes every window (closed() / onClose run) and removes them (UiRuntime.dispose). */
  dispose(): void {
    for (const w of this.all.slice()) {
      try { w.dispose(); } catch (err) { console.error(err); }
    }
    this.all.length = 0;
    this.dragging = false;
  }

  /** @internal Snap lines for a dragged window: layer edges and centre, other open windows, '.snap' parts of the layer. */
  snapLines(self: UiWindow, W: number, H: number): { xs: number[]; ys: number[] } {
    const xs: number[] = [0, W, W / 2], ys: number[] = [0, H];
    for (const o of this.all) if (o !== self && o.open) { const l = o.el.offsetLeft, t = o.el.offsetTop; xs.push(l, l + o.el.offsetWidth); ys.push(t, t + o.el.offsetHeight); }
    for (const el of this.layers.wins.querySelectorAll<HTMLElement>(":scope > .snap")) {
      if (el.hidden) continue;
      const l = el.offsetLeft, t = el.offsetTop;
      xs.push(l, l + el.offsetWidth); ys.push(t, t + el.offsetHeight);
    }
    return { xs, ys };
  }

  /** @internal */
  get scope(): AbortSignal { return this.signal; }
}

/** The new UI's window base (the mock's HudWindow). Construct it lazily (UiRuntime.registerWindow); the element is
 *  appended to the windows layer at once, hidden. Override shown() / closed() / key() / render() / disposed(). */
export abstract class UiWindow {
  readonly el: HTMLElement;
  /** Position in the windows layer; x = null puts the window `right` px from the layer's right edge on first show. */
  protected x: number | null;
  protected y: number;
  protected readonly wm: WindowManager;
  /** The UI scope (aborts on dispose): pass it to addEventListener / later / debounce. */
  protected readonly signal: AbortSignal;
  /** The position key (the first word of `cls`). */
  readonly wmKey: string;
  private readonly wmStart: [number | null, number];
  private wmFitted = false;
  private readonly subs: Switch[] = [];
  private dirty = true;
  private disposedFlag = false;
  private readonly renderSoon: () => void;

  /** Runs after every close (title-bar cross, Esc, Shift+Esc / closeAll, toggle(false) from code, dispose). */
  onClose: (() => void) | null = null;

  constructor(protected readonly host: WindowHost, cls: string, x: number | null, y: number, private readonly right = 40) {
    this.wm = host.wm;
    this.signal = host.signal;
    this.el = h("div", "hwin " + cls);
    this.el.hidden = true;
    this.wmKey = cls.split(" ")[0]!;
    this.wmStart = [x, y];
    const p = this.wm.savedPos(this.wmKey);
    this.x = p ? p.x : x;
    this.y = p ? p.y : y;
    this.wm.add(this);
    this.el.addEventListener("pointerdown", () => this.front(), { capture: true, signal: this.signal });
    this.renderSoon = rafCoalesce(() => { if (this.open && this.dirty) { this.dirty = false; this.render(); } }, this.signal);
    host.layers.wins.append(this.el);
  }

  get open(): boolean { return !this.el.hidden; }

  /** Whether this is the front open window of a showing UI (keys meant for one window check it). */
  get isFront(): boolean { return this.open && !this.el.closest("[hidden]") && this.wm.topmost() === this; }

  toggle(show = this.el.hidden): void {
    if (this.disposedFlag) return;
    const was = this.open;
    if (!show) {
      this.el.hidden = true;
      this.wm.markFront();
      if (was) {
        for (const s of this.subs) s.off();
        try { this.closed(); } catch (err) { console.error(err); }
        try { this.onClose?.(); } catch (err) { console.error(err); }
        this.wm.changed.emit(this);
      }
      return;
    }
    this.el.hidden = false;
    if (this.x === null) this.x = this.rightX();
    this.front();
    if (this.dirty) { this.dirty = false; this.render(); }
    this.place();
    if (!was) {
      this.shown();
      for (const s of this.subs) s.on();
      this.wm.changed.emit(this);
    }
    // Some windows build their content in shown(): fit them on screen again once they have their real size.
    if (!this.wmFitted) { this.wmFitted = true; later(() => { if (this.open) this.place(); }, 0, this.signal); }
  }

  /** Brings the window to the front. */
  front(): void { if (this.open) this.wm.raise(this); }

  // — Hooks —

  /** Called each time the window opens (after render() when dirty, before the shown-only timers start). */
  protected shown(): void {}
  /** Called each time the window closes (any close path, see onClose); the shown-only timers are already stopped. */
  protected closed(): void {}
  /** Keys while this is the front window (routed by WindowManager.handleKey). True = consumed. */
  key(_e: KeyboardEvent): boolean { return false; }
  /** Builds / refreshes the content. Called on open when dirty and once a frame after invalidate() while open. */
  protected render(): void {}
  /** Called once by dispose() after the window closed (release what the scope does not). */
  protected disposed(): void {}

  // — Lifecycle helpers (Performance.md: timers only while shown, dirty-when-closed) —

  /** Marks the content stale: re-rendered on the next frame while open, else on the next open. Call it from model
   *  events instead of rendering directly, so a closed window costs nothing. */
  protected invalidate(): void {
    this.dirty = true;
    if (this.open) this.renderSoon();
  }

  /** Switches `sub` on while the window is shown (starts now if open). Returns it. */
  protected whileShown<S extends Switch>(sub: S): S {
    this.subs.push(sub);
    if (this.open) sub.on();
    return sub;
  }
  /** Runs `fn` every frame while shown (the UI ticker). */
  protected frameShown(fn: FrameFn): Switch { return this.whileShown(this.host.ticker.frame(fn)); }
  /** Runs `fn` at most every `ms` while shown, on the UI ticker (countdowns, clocks). */
  protected everyShown(ms: number, fn: FrameFn): Switch { return this.whileShown(this.host.ticker.every(ms, fn)); }
  /** A real setInterval while shown (work that must run without frames, e.g. a sprite loop in a background tab). */
  protected intervalShown(fn: () => void, ms: number): Switch { return this.whileShown(windowInterval(fn, ms, this.signal)); }
  /** addEventListener in the UI scope (removed on dispose). Never a window keydown (use key()). */
  protected listen<K extends keyof HTMLElementEventMap>(target: HTMLElement, type: K, fn: (e: HTMLElementEventMap[K]) => void, opts?: AddEventListenerOptions): void;
  protected listen<K extends keyof DocumentEventMap>(target: Document, type: K, fn: (e: DocumentEventMap[K]) => void, opts?: AddEventListenerOptions): void;
  protected listen(target: EventTarget, type: string, fn: (e: Event) => void, opts?: AddEventListenerOptions): void {
    target.addEventListener(type, fn, { ...opts, signal: this.signal });
  }

  // — Placement —

  private rightX(): number { return Math.max(0, this.wm.layers.layout.width - (this.el.offsetWidth || 300) - this.right); }

  /** Applies x / y, clamped so at least KEEP px of the window, and its title, stay inside the layer. */
  protected place(): void {
    if (this.wm.mobile) { // docked sheet: full height from the top, right (or left beside another window)
      // A window bigger than the layer shrinks to fit (not below 70 %); what is still too tall scrolls.
      const { width: W, height: H } = this.wm.layers.layout, st = this.el.style;
      st.transform = ""; st.maxHeight = "none";
      const w = this.el.offsetWidth || 300, nat = this.el.scrollHeight || 1;
      const s = Math.max(0.7, Math.min(1, W / w, H / nat));
      st.transformOrigin = "0 0";
      st.transform = s < 1 ? `scale(${s.toFixed(3)})` : "";
      st.maxHeight = (H / s).toFixed(0) + "px";
      this.el.dataset.dockW = String(w * s);
      this.x = this.wm.dockX(this, w * s);
      this.y = 0;
      this.el.style.left = this.x + "px";
      this.el.style.top = "0px";
      return;
    }
    if (this.x !== null) {
      const { width: W, height: H } = this.wm.layers.layout, w = this.el.offsetWidth || 300;
      this.x = Math.min(Math.max(this.x, KEEP - w), W - KEEP);
      this.y = Math.min(Math.max(this.y, 0), H - TITLE_KEEP);
    }
    this.el.style.left = (this.x ?? 0) + "px";
    this.el.style.top = this.y + "px";
  }
  /** @internal WindowManager.reclampAll. */
  reclamp(): void { this.place(); }
  /** @internal WindowManager.resetPositions. */
  resetPosition(): void {
    [this.x, this.y] = this.wmStart;
    if (this.open) { if (this.x === null) this.x = this.rightX(); this.place(); }
  }

  /** A title bar (accent text, line under it) with the close cross; dragging it moves the window, double-click resets it. */
  protected titleBar(text: string, closeHint: string | null): HTMLElement {
    const t = h("div", "title", text);
    if (closeHint !== null) {
      const close = h("button", "x", "×");
      close.type = "button";
      close.title = closeHint + " · Esc closes the front window, Shift+Esc all";
      close.setAttribute("aria-label", "Close");
      close.addEventListener("click", () => this.toggle(false), { signal: this.signal });
      t.append(close);
    }
    if (this.wm.mobile) { this.swipeClose(t); return t; } // docked sheets do not move
    t.title = "Drag to move (Alt: no snapping) · double-click to reset";
    t.addEventListener("dblclick", (e) => {
      if ((e.target as HTMLElement).closest(".x")) return;
      this.wm.savePos(this.wmKey, null);
      this.resetPosition();
    }, { signal: this.signal });
    this.drag(t);
    return t;
  }

  /** A docked sheet (touch layout): a swipe down on its title slides it down and closes it past 90 px (or a quick
   *  flick); a shorter one springs back. Only the `translate` property moves (the sheet's fit scale is `transform`). */
  private swipeClose(handle: HTMLElement): void {
    handle.style.touchAction = "none";
    handle.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || (e.target as HTMLElement).closest(".x, input, button, select, textarea, a, .nodrag")) return;
      try { handle.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ }
      const sy = e.clientY, t0 = e.timeStamp, st = this.el.style;
      const scale = this.el.getBoundingClientRect().height / (this.el.offsetHeight || 1) || 1;
      let dy = 0;
      const move = (ev: PointerEvent) => {
        if (ev.pointerId !== e.pointerId) return;
        dy = Math.max(0, (ev.clientY - sy) / scale);
        st.translate = dy > 4 ? `0 ${dy.toFixed(0)}px` : "";
        st.opacity = dy > 4 ? String(Math.max(0.4, 1 - dy / 400)) : "";
      };
      const end = (ev: PointerEvent) => {
        if (ev.pointerId !== e.pointerId) return;
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", end);
        handle.removeEventListener("pointercancel", end);
        const flick = dy > 30 && dy / Math.max(1, ev.timeStamp - t0) > 0.6;
        st.translate = ""; st.opacity = "";
        if (ev.type === "pointerup" && (dy > 90 || flick)) this.toggle(false);
      };
      handle.addEventListener("pointermove", move, { signal: this.signal });
      handle.addEventListener("pointerup", end, { signal: this.signal });
      handle.addEventListener("pointercancel", end, { signal: this.signal });
    }, { signal: this.signal });
  }

  /** Drag by a handle with pointer capture. The layer is scaled (fit scale x HUD scale), so screen pixels are divided by
   *  the window's measured scale. Snaps to the layer's edges and centre and to other open windows' edges within SNAP px,
   *  with a guide line on each snapped axis. Everything is measured once on pointerdown; moves only write transform;
   *  left / top are committed on release. */
  protected drag(handle: HTMLElement): void {
    handle.style.touchAction = "none";
    handle.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || this.wm.dragging) return;
      if ((e.target as HTMLElement).closest(".x, input, button, select, textarea, a, .nodrag")) return;
      e.preventDefault();
      this.front();
      try { handle.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ }
      const scale = this.el.getBoundingClientRect().width / (this.el.offsetWidth || 1) || 1;
      const sx = e.clientX, sy = e.clientY, ox = this.x ?? 0, oy = this.y;
      const { width: W, height: H } = this.wm.layers.layout;
      const w = this.el.offsetWidth, hh = this.el.offsetHeight;
      const { xs, ys } = this.wm.snapLines(this, W, H);
      const guides = this.wm.layers.guides;
      const gx = h("div", "wguide v"), gy = h("div", "wguide h");
      gx.hidden = gy.hidden = true;
      guides.append(gx, gy);
      /** The snapped position for one axis and the line it snapped to (edges and centre of the window against each line). */
      const snap = (v: number, size: number, lines: number[]): [number, number | null] => {
        let best: [number, number | null] = [v, null], d = SNAP;
        for (const L of lines) for (const [edge, pos] of [[v, L], [v + size, L - size], [v + size / 2, L - size / 2]] as const) {
          const dd = Math.abs(edge - L);
          if (dd < d) { d = dd; best = [pos, L]; }
        }
        return best;
      };
      this.wm.dragging = true;
      this.el.classList.add("wdrag");
      const ac = new AbortController();
      const opts = { signal: ac.signal };
      const move = (m: PointerEvent) => {
        if (m.pointerId !== e.pointerId) return;
        let x = ox + (m.clientX - sx) / scale, y = oy + (m.clientY - sy) / scale, lx: number | null = null, ly: number | null = null;
        if (!m.altKey) { [x, lx] = snap(x, w, xs); [y, ly] = snap(y, hh, ys); }
        x = Math.min(Math.max(x, KEEP - w), W - KEEP);
        y = Math.min(Math.max(y, 0), H - TITLE_KEEP);
        this.x = x; this.y = y;
        this.el.style.transform = `translate(${x - ox}px, ${y - oy}px)`;
        gx.hidden = lx === null; if (lx !== null) gx.style.left = lx + "px";
        gy.hidden = ly === null; if (ly !== null) gy.style.top = ly + "px";
      };
      const end = () => {
        if (ac.signal.aborted) return;
        ac.abort();
        this.wm.dragging = false;
        gx.remove(); gy.remove();
        this.el.classList.remove("wdrag"); this.el.style.transform = "";
        if (this.disposedFlag) return;
        this.place();
        if (this.x !== null && (this.x !== ox || this.y !== oy)) this.wm.savePos(this.wmKey, { x: this.x, y: this.y });
      };
      handle.addEventListener("pointermove", move, opts);
      handle.addEventListener("pointerup", end, opts);
      handle.addEventListener("pointercancel", end, opts);
      handle.addEventListener("lostpointercapture", end, opts);
      this.signal.addEventListener("abort", end, opts);
    }, { signal: this.signal });
  }

  /** Closes the window (closed() / onClose run), stops its timers and removes it (WindowManager.dispose). */
  dispose(): void {
    if (this.disposedFlag) return;
    this.toggle(false);
    for (const s of this.subs) s.off();
    this.subs.length = 0;
    this.disposedFlag = true;
    try { this.disposed(); } catch (err) { console.error(err); }
    this.el.remove();
  }
}
