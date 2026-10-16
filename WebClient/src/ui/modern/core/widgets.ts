// The new UI's shared controls, deduplicated from the scene-lab mocks (Review-Feature-Windows "Improvements": each
// window re-declared its own): the confirm-twice button ("Click again to ..." in hudFriends, hudGroup, hudGuild,
// hudMail, hudQuests, hudCreature), hold-to-confirm (hudAwaken, hudNpcBox), segmented control (hudBigMap, hudCreature,
// hudChat), chips (hudBigMap), toggle switch (hudOptions, hudGuild), tabs, the progress ring (hudCraft, hudCreature,
// hudMentor, hudRefine, hudExtras) and a debounced search box (Performance.md T3-13).
// Controls are real <button>s or carry role / tabindex / keyboard handling (Review-Core "UX"); disabled ones keep
// pointer events so their title (the reason) shows. Listeners live on the control itself; pass the UI scope `signal`
// where a timer is involved. Every factory returns { el, ... } with setters that only touch the DOM on change.

import { asButton, debounce, h, injectCss, setText, typingField } from "./dom.ts";
import type { Kid } from "./dom.ts";
import { svg, svgNode } from "./icons.ts";
import type { IconName } from "./icons.ts";

const CSS = `
/* segmented control */
.hudm .ui-seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; pointer-events: auto; }
.hudm .ui-seg > button { flex: 1; min-width: 0; height: 24px; padding: 0 8px; display: flex; align-items: center; justify-content: center; gap: 4px; cursor: pointer; user-select: none;
  font: inherit; font-size: 11px; font-weight: 600; color: var(--dim); border: 0; background: color-mix(in srgb, var(--pb) 60%, #000); white-space: nowrap; }
.hudm .ui-seg > button + button { border-left: 1px solid var(--fr); }
.hudm .ui-seg > button:hover { color: #fff; }
.hudm .ui-seg > button.on { color: #fff; background: color-mix(in srgb, var(--ac) 24%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .ui-seg > button i { font-style: normal; font-size: 10px; opacity: .7; }
/* chips */
.hudm .ui-chips { display: flex; flex-wrap: wrap; gap: 4px; pointer-events: auto; }
.hudm .ui-chips > button { height: 20px; padding: 0 9px; border-radius: 10px; font: inherit; font-size: 11px; cursor: pointer; color: var(--dim);
  border: 1px solid var(--fr); background: color-mix(in srgb, var(--pb) 70%, #000); }
.hudm .ui-chips > button:hover { border-color: var(--ac); color: #fff; }
.hudm .ui-chips > button.on { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 28%, var(--pb)); }
/* toggle switch */
.hudm .ui-tog { width: 38px; height: 20px; flex: none; padding: 0; border-radius: 10px; position: relative; cursor: pointer; pointer-events: auto;
  background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); }
.hudm .ui-tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 14px; height: 14px; border-radius: 50%; background: var(--dim); transition: transform .15s, background .15s; }
.hudm .ui-tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); }
.hudm .ui-tog.on::after { transform: translateX(18px); background: #fff; box-shadow: 0 0 6px var(--ac); }
.hudm .ui-tog.sm { width: 30px; height: 16px; } .hudm .ui-tog.sm::after { width: 10px; height: 10px; } .hudm .ui-tog.sm.on::after { transform: translateX(14px); }
.hudm .ui-tog.lock { cursor: not-allowed; opacity: .6; }
/* tabs */
.hudm .ui-tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--fr); pointer-events: auto; }
.hudm .ui-tabs > button { position: relative; height: 26px; padding: 0 12px; font: inherit; font-size: 11px; font-weight: 700; letter-spacing: .08em; cursor: pointer;
  color: var(--dim); border: 0; background: none; white-space: nowrap; }
.hudm .ui-tabs > button:hover { color: #fff; }
.hudm .ui-tabs > button.on { color: var(--ac); }
.hudm .ui-tabs > button.on::after { content: ""; position: absolute; left: 6px; right: 6px; bottom: -1px; height: 2px; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .ui-tabs > button b { margin-left: 5px; padding: 0 5px; border-radius: 8px; font-size: 9px; color: #fff; background: var(--bad); }
/* confirm-twice / hold */
.hudm .btn.armed { color: #fff; border-color: var(--bad); background: linear-gradient(180deg, color-mix(in srgb, var(--bad) 45%, var(--pc)), color-mix(in srgb, var(--bad) 22%, var(--pb))); }
.hudm .ui-hold { position: relative; overflow: hidden; }
.hudm .ui-hold > i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); pointer-events: none;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), var(--gold)); opacity: .55; }
.hudm .ui-hold.danger > i { background: linear-gradient(90deg, color-mix(in srgb, var(--bad) 40%, transparent), var(--bad)); }
.hudm .ui-hold > span { position: relative; }
/* progress ring */
.hudm .ui-ring { position: relative; flex: none; }
.hudm .ui-ring svg { display: block; transform: rotate(-90deg); }
.hudm .ui-ring circle { fill: none; }
.hudm .ui-ring .bg { stroke: #05070c; }
.hudm .ui-ring .fg { stroke: var(--ring, var(--ac)); stroke-linecap: round; transition: stroke-dashoffset .3s; }
.hudm .ui-ring > div { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; line-height: 1.1; }
.hudm .ui-ring b { font-size: 13px; color: #fff; } .hudm .ui-ring small { font-size: 8px; color: var(--dim); }
/* labelled row */
.hudm .ui-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; min-height: 26px; }
.hudm .ui-row > .lbl { flex: 1; min-width: 0; }
/* search */
.hudm .ui-search { position: relative; display: flex; align-items: center; pointer-events: auto; }
.hudm .ui-search svg { position: absolute; left: 7px; width: 12px; height: 12px; color: var(--dim); pointer-events: none; }
.hudm .ui-search input { width: 100%; padding-left: 24px; }
.hudm .ui-search .clr { position: absolute; right: 4px; width: 16px; height: 16px; padding: 0; border: 0; background: none; color: var(--dim); cursor: pointer; font: inherit; font-size: 13px; line-height: 1; }
.hudm .ui-search .clr:hover { color: #fff; }
`;

const css = () => injectCss("core-widgets", CSS);

export interface ButtonOpts {
  /** Extra classes: "main", "warn", "big". */
  cls?: string;
  icon?: IconName;
  title?: string;
  disabled?: boolean;
}
export interface Btn {
  readonly el: HTMLButtonElement;
  setLabel(text: string): void;
  /** Disabled stays hoverable: its title explains why (pass `why`). */
  setDisabled(disabled: boolean, why?: string): void;
}

/** A themed <button class="btn">. Clicks while disabled are ignored. */
export function button(label: string, onClick: (e: MouseEvent) => void, o: ButtonOpts = {}): Btn {
  css();
  const span = h("span", "", label);
  const el = h("button", "btn" + (o.cls ? " " + o.cls : ""), o.icon ? svg(o.icon, "") : null, span);
  el.type = "button";
  const baseTitle = o.title ?? "";
  if (baseTitle) el.title = baseTitle;
  let disabled = false;
  el.addEventListener("click", (e) => { if (!disabled) onClick(e); });
  const btn: Btn = {
    el,
    setLabel: (t) => setText(span, t),
    setDisabled: (d, why) => {
      disabled = d;
      el.classList.toggle("dis", d);
      el.setAttribute("aria-disabled", String(d));
      el.title = d && why ? why : baseTitle;
    },
  };
  if (o.disabled) btn.setDisabled(true);
  return btn;
}

export interface ConfirmTwiceOpts extends ButtonOpts {
  /** The armed label ("Click again to remove"). */
  confirmLabel?: string;
  /** How long the armed state lasts (ms, default 3000). */
  timeout?: number;
  signal?: AbortSignal;
}
/** A destructive button that needs a second click within `timeout` (the mocks' "Click again to ..."). Blur or the
 *  timeout disarms it. `reset()` disarms from code (the row it belongs to changed). */
export function confirmTwice(label: string, onConfirm: () => void, o: ConfirmTwiceOpts = {}): Btn & { reset(): void; readonly armed: boolean } {
  let armed = false, t = 0;
  const confirmLabel = o.confirmLabel ?? "Click again";
  const disarm = () => { window.clearTimeout(t); t = 0; if (!armed) return; armed = false; b.el.classList.remove("armed"); b.setLabel(label); };
  const b = button(label, () => {
    if (armed) { disarm(); onConfirm(); return; }
    armed = true;
    b.el.classList.add("armed");
    b.setLabel(confirmLabel);
    t = window.setTimeout(disarm, o.timeout ?? 3000);
  }, o);
  b.el.addEventListener("blur", disarm);
  o.signal?.addEventListener("abort", disarm, { once: true });
  // defineProperty, not Object.assign: assign would copy the getter's value once.
  return Object.defineProperties(b, { reset: { value: disarm }, armed: { get: () => armed } }) as Btn & { reset(): void; readonly armed: boolean };
}

export interface HoldOpts extends ButtonOpts {
  /** Hold time in ms (default 1200, hudAwaken). */
  ms?: number;
  /** Red fill (destructive). */
  danger?: boolean;
}
/** Hold-to-confirm: the pointer (or Enter / Space on the focused button) must be held `ms`; release early cancels.
 *  The fill animates transform (compositor only). */
export function holdButton(label: string, onDone: () => void, o: HoldOpts = {}): Btn {
  const fill = h("i");
  const b = button(label, () => {}, { ...o, cls: `${o.cls ?? ""} ui-hold${o.danger ? " danger" : ""}`.trim() });
  b.el.prepend(fill);
  let anim: Animation | null = null;
  const start = () => {
    if (b.el.classList.contains("dis") || (anim && anim.playState === "running")) return;
    anim = fill.animate([{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }], { duration: o.ms ?? 1200, fill: "forwards" });
    anim.onfinish = () => { anim?.cancel(); anim = null; onDone(); };
  };
  const stop = () => { if (anim && anim.playState !== "finished") anim.cancel(); anim = null; };
  b.el.addEventListener("pointerdown", (e) => { if (e.button === 0) start(); });
  for (const ev of ["pointerup", "pointerleave", "pointercancel", "blur"] as const) b.el.addEventListener(ev, stop);
  b.el.addEventListener("keydown", (e) => {
    if (e.key !== "Enter" && e.key !== " ") return;
    e.preventDefault(); e.stopPropagation();
    if (!e.repeat) start();
  });
  b.el.addEventListener("keyup", (e) => { if (e.key === "Enter" || e.key === " ") stop(); });
  return b;
}

export interface Choice<T> { value: T; label: string; title?: string; icon?: IconName; hint?: string }

/** A segmented control (one choice). `set()` changes the selection without calling onChange. */
export function segmented<T>(choices: readonly Choice<T>[], value: T, onChange: (v: T) => void): { el: HTMLElement; get value(): T; set(v: T): void } {
  css();
  let cur = value;
  const el = h("div", "ui-seg");
  el.setAttribute("role", "radiogroup");
  const btns = choices.map((c) => {
    const b = h("button", "", c.icon ? svg(c.icon, "") : null, c.label, c.hint ? h("i", "", c.hint) : null);
    b.type = "button";
    b.setAttribute("role", "radio");
    if (c.title) b.title = c.title;
    b.addEventListener("click", () => { if (c.value === cur) return; set(c.value); onChange(c.value); });
    el.append(b);
    return b;
  });
  const set = (v: T) => {
    cur = v;
    choices.forEach((c, i) => { const on = c.value === v; btns[i]!.classList.toggle("on", on); btns[i]!.setAttribute("aria-checked", String(on)); });
  };
  set(value);
  return { el, get value() { return cur; }, set };
}

/** Chips: toggles in a wrapping row. `multi` (default true) lets several be on; single mode allows none when
 *  `allowNone`. onChange gets the new selection. */
export function chips<T>(choices: readonly Choice<T>[], selected: Iterable<T>, onChange: (sel: Set<T>) => void, o: { multi?: boolean; allowNone?: boolean } = {}): { el: HTMLElement; readonly selected: ReadonlySet<T>; set(sel: Iterable<T>): void } {
  css();
  const multi = o.multi ?? true;
  let sel = new Set(selected);
  const el = h("div", "ui-chips");
  const btns = choices.map((c) => {
    const b = h("button", "", c.icon ? svg(c.icon, "") : null, c.label);
    b.type = "button";
    if (c.title) b.title = c.title;
    b.addEventListener("click", () => {
      const next = new Set(multi ? sel : []);
      if (sel.has(c.value)) { if (multi || o.allowNone) next.delete(c.value); else return; } else next.add(c.value);
      set(next); onChange(new Set(next));
    });
    el.append(b);
    return b;
  });
  const set = (s: Iterable<T>) => {
    sel = new Set(s);
    choices.forEach((c, i) => { const on = sel.has(c.value); btns[i]!.classList.toggle("on", on); btns[i]!.setAttribute("aria-pressed", String(on)); });
  };
  set(sel);
  return { el, get selected() { return sel; }, set };
}

/** A toggle switch (role=switch). `lock` shows it but refuses clicks (title says why). `set()` does not call onChange. */
export function toggleSwitch(on: boolean, onChange: (on: boolean) => void, o: { small?: boolean; title?: string; label?: string } = {}): { el: HTMLButtonElement; get on(): boolean; set(on: boolean): void; lock(locked: boolean, why?: string): void } {
  css();
  let cur = on, locked = false;
  const el = h("button", "ui-tog" + (o.small ? " sm" : ""));
  el.type = "button";
  el.setAttribute("role", "switch");
  if (o.label) el.setAttribute("aria-label", o.label);
  if (o.title) el.title = o.title;
  const set = (v: boolean) => { cur = v; el.classList.toggle("on", v); el.setAttribute("aria-checked", String(v)); };
  el.addEventListener("click", () => { if (locked) return; set(!cur); onChange(cur); });
  set(on);
  return {
    el, get on() { return cur; }, set,
    lock: (l, why) => { locked = l; el.classList.toggle("lock", l); el.setAttribute("aria-disabled", String(l)); el.title = l && why ? why : o.title ?? ""; },
  };
}

/** Tabs over a row of names. `badge(i, n)` shows a count (0 hides it). `select()` does not call onChange. */
export function tabs(names: readonly string[], active: number, onChange: (i: number) => void): { el: HTMLElement; get active(): number; select(i: number): void; badge(i: number, n: number): void; rename(i: number, name: string): void } {
  css();
  let cur = active;
  const el = h("div", "ui-tabs");
  el.setAttribute("role", "tablist");
  const labels: HTMLElement[] = [], badges: (HTMLElement | null)[] = [];
  const btns = names.map((n, i) => {
    const label = h("span", "", n);
    const b = h("button", "", label);
    b.type = "button";
    b.setAttribute("role", "tab");
    b.addEventListener("click", () => { if (i === cur) return; select(i); onChange(i); });
    labels.push(label); badges.push(null);
    el.append(b);
    return b;
  });
  const select = (i: number) => {
    cur = i;
    btns.forEach((b, j) => { b.classList.toggle("on", j === i); b.setAttribute("aria-selected", String(j === i)); });
  };
  select(active);
  return {
    el, get active() { return cur; }, select,
    badge: (i, n) => {
      const b = btns[i]; if (!b) return;
      let el2 = badges[i];
      if (n <= 0) { el2?.remove(); badges[i] = null; return; }
      if (!el2) { el2 = h("b"); badges[i] = el2; b.append(el2); }
      setText(el2, n > 99 ? "99+" : String(n));
    },
    rename: (i, name) => { const l = labels[i]; if (l) setText(l, name); },
  };
}

/** A progress ring (SVG circle, stroke-dashoffset). `set(frac, big?, small?)` writes only on change. `--ring` on the
 *  element (or the colour argument) overrides the accent. */
export function progressRing(size = 62, stroke = 6, colour?: string): { el: HTMLElement; set(frac: number, big?: string, small?: string): void } {
  css();
  const r = (size - stroke) / 2, c = 2 * Math.PI * r;
  const fg = svgNode("circle", { class: "fg", cx: size / 2, cy: size / 2, r, "stroke-width": stroke, "stroke-dasharray": c.toFixed(2), "stroke-dashoffset": c.toFixed(2) });
  const s = svgNode("svg", { width: size, height: size, viewBox: `0 0 ${size} ${size}`, "aria-hidden": "true" });
  s.append(svgNode("circle", { class: "bg", cx: size / 2, cy: size / 2, r, "stroke-width": stroke }), fg);
  const b = h("b"), sm = h("small");
  const el = h("div", "ui-ring", s, h("div", "", b, sm));
  el.style.width = el.style.height = size + "px";
  if (colour) el.style.setProperty("--ring", colour);
  el.setAttribute("role", "progressbar");
  let last = "";
  return {
    el,
    set: (frac, big = "", small = "") => {
      const f = Math.min(1, Math.max(0, Number.isFinite(frac) ? frac : 0));
      const off = (c * (1 - f)).toFixed(1);
      if (off !== last) { last = off; fg.setAttribute("stroke-dashoffset", off); el.setAttribute("aria-valuenow", String(Math.round(f * 100))); }
      setText(b, big); setText(sm, small);
    },
  };
}

/** A search box that calls onSearch at most every `ms` (default 100) while typing, and at once on Enter / clear.
 *  Hotkeys never see its keys (typingField). */
export function searchBox(placeholder: string, onSearch: (q: string) => void, o: { ms?: number; signal?: AbortSignal; value?: string } = {}): { el: HTMLElement; input: HTMLInputElement; get value(): string; set(v: string): void; focus(): void } {
  css();
  const input = typingField(h("input", "fld"));
  input.type = "search";
  input.placeholder = placeholder;
  input.value = o.value ?? "";
  const clr = h("button", "clr", "×");
  clr.type = "button";
  clr.title = "Clear";
  clr.hidden = !input.value;
  const run = debounce((q: string) => onSearch(q), o.ms ?? 100, o.signal);
  input.addEventListener("input", () => { clr.hidden = !input.value; run(input.value.trim()); });
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") { run.cancel(); onSearch(input.value.trim()); } });
  clr.addEventListener("click", () => { input.value = ""; clr.hidden = true; run.cancel(); onSearch(""); input.focus(); });
  const el = h("div", "ui-search", svg("search", ""), input, clr);
  return {
    el, input, get value() { return input.value.trim(); },
    set: (v) => { input.value = v; clr.hidden = !v; },
    focus: () => input.focus({ preventScroll: true }),
  };
}

/** A labelled row: label on the left, the control(s) on the right (options pages, details). */
export function row(label: string, ...controls: Kid[]): HTMLElement {
  css();
  return h("div", "ui-row", h("span", "lbl", label), ...controls);
}

/** Makes any element a keyboard-reachable button (re-export for windows that build custom tiles). */
export { asButton };
