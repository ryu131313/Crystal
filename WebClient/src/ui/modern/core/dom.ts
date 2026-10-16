// Ported from play/hudWindow.ts (h, injectCss, setText, restartAnim, rafCoalesce, windowInterval): the DOM helpers of the
// new UI (webclient.md §3.6). Every modern file builds its DOM with these: no innerHTML with data, text through
// textContent / setText, CSS appended once into one <style id="crystal-ui">, and the performance helpers of
// Knowledgebase/Future Ideas/SceneLab-UI-Review/Performance.md (setText, restartAnim without a reflow, rafCoalesce,
// interval lifecycle, debounce for search boxes).
//
// Page-level state here is only the style sheet (idempotent, harmless across log-outs) and WeakMaps keyed by nodes.

/** A child for h(): nodes and strings are appended; null / undefined / false / "" are skipped. */
export type Kid = Node | string | null | undefined | false;

/** Creates an element with a class list and children (strings become text nodes, never markup). */
export function h<K extends keyof HTMLElementTagNameMap>(tag: K, cls = "", ...kids: Kid[]): HTMLElementTagNameMap[K] {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  for (const k of kids) if (k !== null && k !== undefined && k !== false && k !== "") n.append(k);
  return n;
}

const STYLE_ID = "crystal-ui";
const injected = new Set<string>();

/** Appends a module's CSS once to the one shared <style id="crystal-ui"> (one sheet for the whole new UI instead of a
 *  <style> per module). `id` names the module; a second call with the same id does nothing. */
export function injectCss(id: string, css: string): void {
  let style = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
  if (!style) {
    injected.clear(); // the sheet was removed (a test page): append everything again
    style = document.createElement("style");
    style.id = STYLE_ID;
    document.head.append(style);
  }
  if (injected.has(id)) return;
  injected.add(id);
  style.append(document.createTextNode(`/* ${id} */\n${css}\n`));
}

// — Performance helpers (Performance.md "Shared helpers") —

const lastText = new WeakMap<Node, string>();
/** Sets `textContent` only when the string changes: assigning the same text still replaces the text node and dirties
 *  layout, which adds up for per-frame HUD text. Keeps its own copy, so the DOM is never read back. Use it for every
 *  text that is rewritten from a frame / ticker / model event. */
export function setText(el: Node, s: string): void {
  if (lastText.get(el) === s) return;
  lastText.set(el, s);
  el.textContent = s;
}

const lastStyle = new WeakMap<HTMLElement, Map<string, string>>();
/** Sets one inline style property (or custom property) only when the value changes (bar widths, transforms). */
export function setStyle(el: HTMLElement, prop: string, value: string): void {
  let m = lastStyle.get(el);
  if (!m) lastStyle.set(el, (m = new Map()));
  if (m.get(prop) === value) return;
  m.set(prop, value);
  el.style.setProperty(prop, value);
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

/** Wraps a high-rate handler (pointermove, input) so it runs at most once a frame, with the latest arguments. With a
 *  `signal`, a call after the abort does nothing (a frame already queued is dropped). */
export function rafCoalesce<A extends unknown[]>(fn: (...args: A) => void, signal?: AbortSignal): (...args: A) => void {
  let pending: A | null = null;
  return (...args: A) => {
    if (signal?.aborted) return;
    if (!pending) requestAnimationFrame(() => { const a = pending!; pending = null; if (!signal?.aborted) fn(...a); });
    pending = args;
  };
}

/** A start / stop switch for a timer (windows start it in shown() and stop it in closed()). */
export interface Switch { on(): void; off(): void; readonly running: boolean }

/** An interval that only runs between `on()` and `off()` (start it in a window's shown(), stop it in closed()), so
 *  closed windows cost nothing. `on()` while running does nothing. With a `signal`, the abort stops it for good.
 *  Prefer the frame ticker (ticker.ts `every`) for UI countdowns; this is for work that must run without frames. */
export function windowInterval(fn: () => void, ms: number, signal?: AbortSignal): Switch {
  let id = 0;
  const sw: Switch = {
    on() { if (!id && !signal?.aborted) id = window.setInterval(fn, ms); },
    off() { if (id) { window.clearInterval(id); id = 0; } },
    get running() { return id !== 0; },
  };
  signal?.addEventListener("abort", () => sw.off(), { once: true });
  return sw;
}

/** A debounced call (search boxes: ~100 ms, Performance.md T3-13). `flush()` runs a pending call now, `cancel()` drops it. */
export function debounce<A extends unknown[]>(fn: (...args: A) => void, ms = 100, signal?: AbortSignal): ((...args: A) => void) & { flush(): void; cancel(): void } {
  let id = 0, last: A | null = null;
  const run = () => { id = 0; const a = last; last = null; if (a && !signal?.aborted) fn(...a); };
  const d = ((...args: A) => {
    if (signal?.aborted) return;
    last = args;
    if (id) window.clearTimeout(id);
    id = window.setTimeout(run, ms);
  }) as ((...args: A) => void) & { flush(): void; cancel(): void };
  d.flush = () => { if (id) { window.clearTimeout(id); run(); } };
  d.cancel = () => { if (id) window.clearTimeout(id); id = 0; last = null; };
  signal?.addEventListener("abort", () => d.cancel(), { once: true });
  return d;
}

/** A one-shot timeout tied to a scope: cleared when `signal` aborts. Returns its cancel. */
export function later(fn: () => void, ms: number, signal?: AbortSignal): () => void {
  if (signal?.aborted) return () => {};
  const id = window.setTimeout(() => { signal?.removeEventListener("abort", cancel); fn(); }, ms);
  const cancel = () => window.clearTimeout(id);
  signal?.addEventListener("abort", cancel, { once: true });
  return cancel;
}

/** True when the player asked the system for less motion (CSS also turns animations off, styles.ts). */
export function reducedMotion(): boolean {
  return typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
}

/** Makes a non-button element act as a button for keyboard and assistive tech: role, tab stop, Enter / Space click.
 *  The key listener is on the element itself (not window), so it only sees keys while the element has the focus. */
export function asButton<E extends HTMLElement>(el: E, label?: string): E {
  el.setAttribute("role", "button");
  if (el.tabIndex < 0) el.tabIndex = 0;
  if (label) el.setAttribute("aria-label", label);
  el.addEventListener("keydown", (e) => {
    if ((e.key === "Enter" || e.key === " ") && !e.repeat && e.target === el) { e.preventDefault(); e.stopPropagation(); el.click(); }
  });
  return el;
}

/** Stops game hotkeys while typing in a text field built by a window (the router also skips text targets via
 *  isTextTarget; this keeps a key from reaching a lower layer that does not check). Element-level, never window. */
export function typingField<E extends HTMLInputElement | HTMLTextAreaElement>(el: E): E {
  el.autocomplete = "off";
  el.spellcheck = false;
  (el as HTMLElement).addEventListener("keydown", (e) => { if (e.key !== "Escape") e.stopPropagation(); });
  return el;
}
