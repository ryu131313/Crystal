// Drag and drop for the lab mocks (2026-10-15). Windows register "hosts" (a list of cells: the bag, equipment, the HUD
// belt, a storage page, the trade offer) and bind their cells; the manager does the rest:
//  - press and move 5 px on a filled cell to pick the item up: a ghost icon follows the pointer, the source dims, the
//    item tooltip hides; every cell under the pointer lights green (with what will happen: Move, Equip, Swap, Stack) or
//    red (with the reason: the host's own rules, e.g. "Rings only", "Needs level 55", "Bound: can't be traded");
//  - dropping merges into a matching stack up to its stack size, moves into an empty cell, or swaps when the source can
//    take the other item back; Shift-drag of a stack asks how many to split off; Esc cancels;
//  - dropping on the world (not on a window or the HUD) asks "Drop it on the ground?" (DontDrop items can't be dropped);
//  - right-click a cell for a quick move (hosts decide where: the bag equips or belts an item, a worn item goes back to
//    the bag, storage <-> bag, bag -> the open trade).
// The item rules use play/lab-data/items.json (hudItems.ts). Nothing is sent anywhere.

import { h, injectCss, rafCoalesce } from "./hudWindow.ts";
import { cap, hasBind, info, stackMax, type Stack } from "./hudItems.ts";

export interface DragHost {
  readonly id: string;
  /** Number of cells. */
  readonly size: number;
  /** Whether its window is showing (quick moves only go to a showing host). */
  live(): boolean;
  get(i: number): Stack | null;
  /** Stores a stack (or empties the cell) and redraws. */
  set(i: number, s: Stack | null): void;
  /** "" when cell i can take `s` (coming from `from`), else the reason it can't. */
  accepts(i: number, s: Stack, from: DragHost): string;
  /** What a drop here is called ("Equip", "Belt", "Store"…), coming from `from`; default "Move". */
  verb?(i: number, from: DragHost): string;
  /** Right-click: where the item in cell i goes, or null. */
  quick?(i: number): [DragHost, number] | null;
  /** Called after a move finished (on both hosts; once when it stayed in one), for logs / chat. */
  after?(s: Stack, from: DragHost, to: DragHost): void;
}

const hosts = new Map<string, DragHost>();
export function registerHost<T extends DragHost>(host: T): T { hosts.set(host.id, host); return host; }
export function host(id: string): DragHost | undefined { return hosts.get(id); }
/** Makes a cell draggable / a drop target for (host, index). */
export function bindCell(el: HTMLElement, hostId: string, index: number): void { el.dataset.dz = hostId; el.dataset.di = String(index); }
/** Where `s` goes in `target` on a quick move: a stack of it with room, else the first empty cell that takes it, else (when
 *  `swap`) the first cell that takes it (a worn ring swaps out), as [host, cell]; null when nothing fits or the host isn't
 *  showing. */
export function bestCell(target: DragHost | undefined, s: Stack, from: DragHost, swap = false): [DragHost, number] | null {
  return target?.live() ? cellFor(target, s, from, swap) : null;
}
/** bestCell without the "is it showing" check (the belt equips gear with the Character window shut). */
export function cellFor(target: DragHost, s: Stack, from: DragHost, swap = false): [DragHost, number] | null {
  const max = stackMax(s.name);
  if (max > 1) for (let i = 0; i < target.size; i++) { const t = target.get(i); if (t && t.name === s.name && t.count < max && !target.accepts(i, s, from)) return [target, i]; }
  for (let i = 0; i < target.size; i++) if (!target.get(i) && !target.accepts(i, s, from)) return [target, i];
  if (swap) for (let i = 0; i < target.size; i++) if (!target.accepts(i, s, from)) return [target, i];
  return null;
}

const CSS = `
body.hudm-dragging, body.hudm-dragging * { cursor: grabbing !important; }
body.hudm-dragging .hudm .itip { display: none !important; }
.hudm .dz-src { opacity: .35; }
.hudm .dz-ok { outline: 2px solid #6fe08a; outline-offset: -2px; box-shadow: 0 0 12px rgba(111,224,138,.6) !important; }
.hudm .dz-no { outline: 2px solid #ff6a5a; outline-offset: -2px; }
.hudm .dz-hint { outline: 1px dashed color-mix(in srgb, #6fe08a 70%, transparent); outline-offset: -2px; }
/* The ghost and label move by transform on their own layers (will-change), so a move repaints nothing, not even the
   ghost's drop-shadow filters. */
.hudm .dghost { position: fixed; left: 0; top: 0; will-change: transform; z-index: 2000000; pointer-events: none; width: 44px; height: 44px; margin: -22px 0 0 -22px; border-radius: 6px; background: center / auto no-repeat;
  image-rendering: pixelated; filter: drop-shadow(0 6px 10px rgba(0,0,0,.8)) drop-shadow(0 0 6px var(--glow)); transform: scale(1.15) rotate(-4deg); }
.hudm .dghost b { position: absolute; right: 2px; bottom: 0; font-size: 11px; color: #fff; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .dlabel { position: fixed; left: 0; top: 0; will-change: transform; z-index: 2000001; pointer-events: none; padding: 3px 9px; border-radius: 10px; font-size: 11px; font-weight: 700; white-space: nowrap; color: #fff;
  background: rgba(10,14,22,.92); border: 1px solid var(--fr); box-shadow: 0 4px 12px rgba(0,0,0,.6); }
.hudm .dlabel.ok { border-color: #4fbf6a; color: #c8ffd4; } .hudm .dlabel.no { border-color: #c44; color: #ffc8c0; }
.hudm .dpop { position: fixed; z-index: 2000002; width: 230px; padding: 12px; border-radius: 10px; font-size: 12px; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 96%, #000); border: 1px solid var(--ac); box-shadow: 0 10px 30px rgba(0,0,0,.75), 0 0 14px var(--glow); }
.hudm .dpop b { color: #fff; } .hudm .dpop .r { display: flex; gap: 6px; margin-top: 10px; } .hudm .dpop .r div { flex: 1; text-align: center; padding: 5px; border-radius: 6px; cursor: pointer; border: 1px solid var(--fr); }
.hudm .dpop .r div:hover { border-color: var(--ac); color: #fff; } .hudm .dpop .r div.go { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); }
.hudm .dpop .r div.red { border-color: #c44; color: #ffb3a6; } .hudm .dpop input[type=range] { width: 100%; accent-color: var(--ac); margin-top: 8px; }
.hudm .dpop .n { font-size: 20px; font-weight: 800; color: var(--ac); text-align: center; margin-top: 4px; }
.hudm .dtoast { position: fixed; z-index: 2000003; pointer-events: none; padding: 5px 12px; border-radius: 12px; font-size: 12px; font-weight: 700; color: #ffd0c8; background: rgba(60,10,10,.92);
  border: 1px solid #c44; animation: hudm-dtoast 1.6s ease-out forwards; } @keyframes hudm-dtoast { 0% { opacity: 0; transform: translateY(6px); } 15%, 75% { opacity: 1; transform: none; } 100% { opacity: 0; } }
`;

interface Drag { from: DragHost; i: number; stack: Stack; el: HTMLElement; ghost: HTMLElement; label: HTMLElement; split: boolean; over: HTMLElement | null }
let root: HTMLElement | null = null;
let pending: { from: DragHost; i: number; el: HTMLElement; x: number; y: number; shift: boolean } | null = null;
let drag: Drag | null = null;
/** Drop on the world: the HUD's own say (chat). */
let say: (text: string) => void = () => {};

/** Installs the manager on the HUD root (hudMock.ts), once. */
export function installDrag(hudRoot: HTMLElement, chat: (text: string) => void): void {
  if (root) return;
  root = hudRoot; say = chat;
  injectCss("hudm-drag-css", CSS);
  hudRoot.addEventListener("mousedown", (e) => {
    if (e.button !== 0 || drag) return;
    const el = (e.target as HTMLElement).closest<HTMLElement>("[data-dz]");
    const hst = el && hosts.get(el.dataset.dz!);
    if (!el || !hst || !hst.get(+el.dataset.di!)) return;
    pending = { from: hst, i: +el.dataset.di!, el, x: e.clientX, y: e.clientY, shift: e.shiftKey };
  }, true);
  hudRoot.addEventListener("contextmenu", (e) => {
    const el = (e.target as HTMLElement).closest<HTMLElement>("[data-dz]");
    const hst = el && hosts.get(el.dataset.dz!);
    if (!el || !hst) return;
    const i = +el.dataset.di!, s = hst.get(i);
    if (!s || !hst.quick) return;
    e.preventDefault(); e.stopPropagation();
    const q = hst.quick(i);
    if (!q) { toast(e.clientX, e.clientY, "Nowhere to put it"); return; }
    const why = q[0].accepts(q[1], s, hst);
    if (why) { toast(e.clientX, e.clientY, why); return; }
    place(hst, i, q[0], q[1], s, s.count, e.clientX, e.clientY);
  });
  const follow = rafCoalesce((x: number, y: number) => { if (drag) move(x, y); }); // at most once a frame
  window.addEventListener("mousemove", (e) => {
    if (pending && !drag && Math.hypot(e.clientX - pending.x, e.clientY - pending.y) > 5) start(e);
    else if (drag) follow(e.clientX, e.clientY);
  });
  window.addEventListener("mouseup", (e) => {
    if (drag) { e.preventDefault(); drop(e.clientX, e.clientY); }
    pending = null;
  });
  // Swallow the click that ends a drag (it would select the cell under the pointer).
  window.addEventListener("click", (e) => { if (justDropped) { e.stopPropagation(); e.preventDefault(); justDropped = false; } }, true);
  window.addEventListener("keydown", (e) => { if (drag && e.key === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); cancel(); } }, true);
}
let justDropped = false;

function iconOf(el: HTMLElement): string {
  for (const n of [el, ...el.querySelectorAll<HTMLElement>("*")]) { const b = n.style.backgroundImage; if (b && b !== "none") return b; }
  return "";
}

function start(e: MouseEvent): void {
  const p = pending!; pending = null;
  const stack = p.from.get(p.i);
  if (!stack || !root) return;
  const ghost = h("div", "dghost");
  ghost.style.backgroundImage = iconOf(p.el);
  if (stack.count > 1) ghost.append(h("b", "", String(stack.count)));
  const label = h("div", "dlabel"); label.hidden = true;
  root.append(ghost, label);
  p.el.classList.add("dz-src");
  document.body.classList.add("hudm-dragging");
  drag = { from: p.from, i: p.i, stack, el: p.el, ghost, label, split: p.shift && stack.count > 1, over: null };
  hint(true);
  move(e.clientX, e.clientY);
}

/** Outlines where the dragged item can go outside its own container and the bag (the ring slots for a ring, the belt
 *  for a potion, the open trade or storage), so the eye finds them; every bag cell would light otherwise. */
function hint(on: boolean): void {
  for (const c of document.querySelectorAll<HTMLElement>(".hudm .dz-hint")) c.classList.remove("dz-hint");
  if (!on || !drag) return;
  for (const c of document.querySelectorAll<HTMLElement>(".hudm [data-dz]")) {
    const t = hosts.get(c.dataset.dz!);
    if (!t || t === drag.from || t.id === "bag" || !c.offsetParent) continue;
    if (verdict(t, +c.dataset.di!)[0]) c.classList.add("dz-hint");
  }
}

/** What dropping on (t, i) would do, or the reason it can't. */
function verdict(t: DragHost, i: number): [boolean, string] {
  const d = drag!;
  if (t === d.from && i === d.i) return [false, ""];
  const why = t.accepts(i, d.stack, d.from);
  if (why) return [false, why];
  const there = t.get(i);
  if (there && there.name === d.stack.name && stackMax(there.name) > 1) return there.count >= stackMax(there.name) ? [false, "That stack is full"] : [true, "Stack"];
  if (there) { const back = d.from.accepts(d.i, there, t); return back ? [false, `Can't swap: ${back}`] : [true, `Swap with ${there.name}`]; }
  return [true, d.split ? "Split here" : (t.verb?.(i, d.from) ?? "Move")];
}

function overWorld(x: number, y: number, hit?: HTMLElement | null): boolean {
  const el = hit !== undefined ? hit : document.elementFromPoint(x, y) as HTMLElement | null;
  if (!el || !root?.contains(el) && !el.closest("#view")) return false;
  return !el.closest(".hwin, [class^='hm-'], [class*=' hm-'], .hchat, .mmap, .party, .qtrack, .hframe, .itip, .dpop, .treq, .lgscr, .slscr, .ldscr");
}

function move(x: number, y: number): void {
  const d = drag!;
  // Hit-test before any write (one hit test, no forced layout); the ghost and label are pointer-events: none.
  const hit = document.elementFromPoint(x, y) as HTMLElement | null;
  const el = hit?.closest<HTMLElement>("[data-dz]") ?? null;
  if (d.over && d.over !== el) d.over.classList.remove("dz-ok", "dz-no");
  d.over = el;
  let text = "", ok = false;
  if (el) {
    const t = hosts.get(el.dataset.dz!);
    if (t) { [ok, text] = verdict(t, +el.dataset.di!); el.classList.toggle("dz-ok", ok); el.classList.toggle("dz-no", !ok && !!text); }
  } else if (overWorld(x, y, hit)) { ok = !hasBind(d.stack.name, "DontDrop"); text = ok ? "Drop on the ground" : "Can't be dropped"; }
  d.ghost.style.transform = `translate(${x}px, ${y}px) scale(1.15) rotate(-4deg)`;
  d.label.hidden = !text;
  if (d.label.textContent !== text) d.label.textContent = text;
  const cls = "dlabel " + (ok ? "ok" : "no");
  if (d.label.className !== cls) d.label.className = cls;
  if (text) d.label.style.transform = `translate(${x + 20}px, ${y + 16}px)`;
}

function cleanup(): void {
  if (!drag) return;
  drag.ghost.remove(); drag.label.remove();
  drag.el.classList.remove("dz-src"); drag.over?.classList.remove("dz-ok", "dz-no");
  hint(false);
  document.body.classList.remove("hudm-dragging");
  drag = null;
}
function cancel(): void { cleanup(); }

function drop(x: number, y: number): void {
  const d = drag!;
  justDropped = true; window.setTimeout(() => { justDropped = false; }, 0);
  const el = (document.elementFromPoint(x, y) as HTMLElement | null)?.closest<HTMLElement>("[data-dz]") ?? null;
  const from = d.from, i = d.i, s = d.stack, split = d.split;
  cleanup();
  if (el) {
    const t = hosts.get(el.dataset.dz!); const ti = +el.dataset.di!;
    if (!t) return;
    drag = { ...d }; const [ok, text] = verdict(t, ti); drag = null;
    if (!ok) { if (text) toast(x, y, text); return; }
    if (split && !t.get(ti)) { askAmount(x, y, s, (n) => place(from, i, t, ti, s, n, x, y)); return; }
    place(from, i, t, ti, s, s.count, x, y);
    return;
  }
  if (overWorld(x, y)) {
    if (hasBind(s.name, "DontDrop")) { toast(x, y, `${s.name} can't be dropped`); return; }
    confirmDrop(x, y, s, () => {
      // The cell may have changed while the box was open (a potion drunk, the item moved): drop what is there now.
      const now = from.get(i);
      if (!now || now.name !== s.name) { toast(x, y, "It's no longer there"); return; }
      from.set(i, null); say(`You dropped ${now.name}${now.count > 1 ? ` x${now.count}` : ""} on the ground.`);
    });
  }
}

/** Moves `n` of `s` from (from, i) to (t, ti): merge, move or swap. */
/** Moves the whole stack in (from, i) to (t, ti) by the drop rules (merge / move / swap), for code paths (the belt's gear
 *  swap); "" when done, else the reason it can't. */
export function transfer(from: DragHost, i: number, t: DragHost, ti: number): string {
  const s = from.get(i);
  if (!s) return "Nothing there";
  const why = t.accepts(ti, s, from);
  if (why) return why;
  const there = t.get(ti);
  if (there && there.name !== s.name) { const back = from.accepts(i, there, t); if (back) return back; }
  place(from, i, t, ti, s, s.count, 0, 0);
  return "";
}

function place(from: DragHost, i: number, t: DragHost, ti: number, s: Stack, n: number, x: number, y: number): void {
  // `s` was read when the drag (or the split box) started; the cell may have changed since (Demo events drink a belt
  // potion mid-drag): move what is there now, never more.
  const live = from.get(i);
  if (!live || live.name !== s.name) { toast(x, y, "It's no longer there"); return; }
  n = Math.min(n, live.count); s = live;
  const there = t.get(ti);
  if (there && there.name === s.name && stackMax(s.name) > 1) {
    const room = stackMax(s.name) - there.count, k = Math.min(room, n);
    if (k <= 0) { toast(x, y, "That stack is full"); return; }
    t.set(ti, { ...there, count: there.count + k });
    from.set(i, s.count - k > 0 ? { ...s, count: s.count - k } : null);
  } else if (there) {
    // swap (the source takes the other item back)
    t.set(ti, { ...s }); from.set(i, { ...there });
  } else {
    const k = Math.min(n, cap(s.name)); // a cell never holds more than the item's StackSize
    t.set(ti, { ...s, count: k });
    from.set(i, s.count - k > 0 ? { ...s, count: s.count - k } : null);
  }
  const moved = { ...s, count: there && there.name !== s.name ? s.count : Math.min(n, s.count) };
  from.after?.(moved, from, t); if (t !== from) t.after?.(moved, from, t);
}

/** Closes the open split / drop box and drops its outside-click listener. */
let popClose: (() => void) | null = null;
function popAt(x: number, y: number, ...kids: HTMLElement[]): { p: HTMLElement; close: () => void } {
  popClose?.();
  const p = h("div", "dpop", ...kids);
  p.style.left = Math.min(window.innerWidth - 250, x + 12) + "px"; p.style.top = Math.min(window.innerHeight - 160, y + 12) + "px";
  root!.append(p);
  const outside = (e: MouseEvent) => { if (!p.contains(e.target as Node)) close(); };
  const timer = window.setTimeout(() => document.addEventListener("mousedown", outside, true));
  const close = () => { window.clearTimeout(timer); p.remove(); document.removeEventListener("mousedown", outside, true); if (popClose === close) popClose = null; };
  popClose = close;
  return { p, close };
}
function askAmount(x: number, y: number, s: Stack, done: (n: number) => void): void {
  let n = Math.floor(s.count / 2) || 1;
  const num = h("div", "n", String(n));
  const r = h("input"); r.type = "range"; r.min = "1"; r.max = String(s.count - 1 || 1); r.value = String(n);
  r.addEventListener("input", () => { n = +r.value; num.textContent = String(n); });
  const ok = h("div", "go", "Split"), no = h("div", "", "Cancel");
  const { close } = popAt(x, y, h("div", "", "Split ", h("b", "", s.name), ` (${s.count})`), num, r, h("div", "r", no, ok));
  ok.addEventListener("click", () => { close(); done(n); });
  no.addEventListener("click", close);
}
function confirmDrop(x: number, y: number, s: Stack, done: () => void): void {
  const ok = h("div", "red", "Drop it"), no = h("div", "go", "Keep it");
  const it = info(s.name);
  const { close } = popAt(x, y, h("div", "", "Drop ", h("b", "", `${s.name}${s.count > 1 ? ` x${s.count}` : ""}`), " on the ground?", it?.price ? ` It's worth ${Math.floor(it.price / 2).toLocaleString("en-GB")} gold to a merchant.` : ""), h("div", "r", no, ok));
  ok.addEventListener("click", () => { close(); done(); });
  no.addEventListener("click", close);
}
export function toast(x: number, y: number, text: string): void {
  if (!root) return;
  const t = h("div", "dtoast", text);
  t.style.left = x + 14 + "px"; t.style.top = y - 30 + "px";
  root.append(t);
  window.setTimeout(() => t.remove(), 1700);
}
