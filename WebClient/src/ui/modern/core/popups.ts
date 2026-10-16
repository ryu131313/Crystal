// Ported from play/hudPopup.ts: the new UI's stock boxes (the desktop's MirMessageBox, MirInputBox, MirAmountBox) and
// the invite toasts, each a promise the caller awaits (or closes).
//  - confirm (MirMessageBox: OK, OKCancel, YesNo, YesNoCancel, Cancel): a kind (info / question / warning / danger) sets
//    the badge and the primary button; danger buttons can need holding (pointer, or Enter / Space held on the focused
//    button); an optional countdown answers for you when it runs out; an optional "Don't ask again" box; an optional
//    item or gold line under the text.
//  - input (MirInputBox): a text field with a live check, a character counter, a password mode and name suggestions.
//  - amount (MirAmountBox: gold or an item stack): a big number you can type (1.5k / 2m work), a slider, - / + that
//    speed up while held, quick chips, the min / max, the item's icon or the gold coin, and what's left after.
//  - request (group / guild / marriage / mentor / friend invites): a toast with Accept / Decline and a bar that runs
//    out (declines on its own), stacked under the notice line, not modal.
// Changes from the mock: no window key listener (the integrator registers handleKey() on the input router's "modal"
// layer); no backdrop-filter over the live canvas (a darker veil, Performance.md T3-2); no offsetWidth reflow for the
// shake (restartAnim); every box returns a promise with close() (GameUi.confirm / amount hand that back); text is
// textContent, `html` only opt-in for constant markup written in code; one Popups per UiRuntime (closeAll on dispose).

import { Lib, libPicture } from "../../mir/controls.ts";
import { asButton, h, injectCss, later, restartAnim } from "./dom.ts";
import { fmt, parseAmount } from "./format.ts";
import { svg } from "./icons.ts";

export type Answer = "ok" | "cancel" | "yes" | "no";
export type Buttons = "ok" | "okcancel" | "yesno" | "yesnocancel" | "cancel";
export type Kind = "info" | "question" | "warning" | "danger";
/** An item line / badge: ItemInfo.Image (Items.Lib) and the item's name (from ItemInfo by ItemIndex). */
export interface PopupItem { name: string; img: number; count?: number }
/** `text` is plain text (line breaks kept). Set `html` only for trusted markup written in code (<b>, <span class=gold>),
 *  never with names, chat or item data in it. */
export interface ConfirmOpts {
  title?: string; text: string; html?: boolean; kind?: Kind; buttons?: Buttons;
  /** An item (Items.Lib Image) or gold shown under the text. */
  item?: PopupItem; gold?: number;
  /** Seconds until `timeoutAnswer` is chosen for you (shown on that button). */
  countdown?: number; timeoutAnswer?: Answer;
  /** The primary button must be held for this many ms (danger). */
  hold?: number;
  /** Shows a "Don't ask again" box; the result says whether it was ticked. */
  dontAsk?: boolean;
  /** Custom button labels. */
  labels?: Partial<Record<Answer, string>>;
}
export interface InputOpts {
  title?: string; text: string; html?: boolean; placeholder?: string; value?: string; min?: number; max?: number;
  /** Allowed characters as a regex class body, e.g. "A-Za-z0-9". */
  chars?: string; password?: boolean; suggestions?: string[];
  /** "" when fine, else why not (shown under the field). */
  check?: (v: string) => string;
  /** Allow an empty answer (default: OK is off while empty). */
  allowEmpty?: boolean;
}
export interface AmountOpts {
  title?: string; text?: string; html?: boolean; max: number; min?: number; value?: number;
  item?: PopupItem; gold?: boolean;
  /** What the "left" line counts ("in your bag", "gold left"); default by kind. */
  leftLabel?: string;
}
export interface RequestOpts { from: string; text: string; tag?: string; seconds?: number; accept?: string; decline?: string }

/** A popup's answer; close() dismisses it (resolving with the cancelled value: null, or false for a request). */
export type PopupPromise<T> = Promise<T> & { close(): void };

/** MirAmountBox's gold icon (Items.Lib). */
export const GOLD_IMG = 116;
/** Bare marks: the round badge is their only ring. */
const KIND_ICON: Record<Kind, string> = {
  info: "M12 10.5v8M12 5.5v.01",
  question: "M8.5 8.5a3.5 3.5 0 117 0c0 2.4-3.5 2.8-3.5 6M12 19v.01",
  warning: "M12 4.5v9.5M12 19v.01",
  danger: "M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3",
};

const CSS = `
.hudm .hpop-veil { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; pointer-events: auto;
  background: radial-gradient(ellipse at 50% 45%, rgba(4,8,16,.5), rgba(2,4,8,.82)); animation: hudm-pveil .18s ease-out; }
@keyframes hudm-pveil { from { opacity: 0; } }
.hudm .hpop { position: relative; width: 380px; max-width: calc(100% - 24px); color: var(--tx); border-radius: calc(var(--rad) + 2px); overflow: hidden; outline: none;
  background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 96%, var(--pb)), color-mix(in srgb, var(--pb) 80%, #000));
  border: 1px solid color-mix(in srgb, var(--kc) 55%, var(--fr)); box-shadow: 0 0 0 1px #000, 0 20px 50px rgba(0,0,0,.8), 0 0 30px color-mix(in srgb, var(--kc) 25%, transparent);
  animation: hudm-pin .22s cubic-bezier(.2,.9,.3,1.2); --kc: var(--ac); }
.hudm .hpop.danger { --kc: var(--bad); } .hudm .hpop.warning { --kc: var(--warn); }
@keyframes hudm-pin { from { opacity: 0; transform: translateY(10px) scale(.96); } }
.hudm .hpop.out { animation: hudm-pout .14s ease-in forwards; } @keyframes hudm-pout { to { opacity: 0; transform: scale(.97); } }
.hudm .hpop.shake { animation: hudm-pshake .3s linear; } @keyframes hudm-pshake { 25% { transform: translateX(-6px); } 75% { transform: translateX(6px); } }
.hudm .hpop::before { content: ""; position: absolute; left: 0; right: 0; top: 0; height: 2px; background: linear-gradient(90deg, transparent, var(--kc), transparent); }
.hudm .hpop .hd { display: flex; align-items: center; gap: 12px; padding: 16px 18px 6px; }
.hudm .hpop .badge { width: 38px; height: 38px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(circle at 50% 35%, color-mix(in srgb, var(--kc) 35%, var(--pc)), var(--pb)); border: 1px solid var(--kc); box-shadow: 0 0 14px color-mix(in srgb, var(--kc) 45%, transparent); }
.hudm .hpop .badge svg { width: 22px; height: 22px; fill: none; stroke: var(--kc); stroke-width: 2.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hpop .badge.ico { background: center / auto no-repeat, radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); image-rendering: pixelated; border-radius: 10px; }
.hudm .hpop h3 { margin: 0; font-size: 14px; letter-spacing: .08em; color: #fff; }
.hudm .hpop .tx { padding: 4px 18px 0 68px; font-size: 12.5px; line-height: 1.5; color: color-mix(in srgb, var(--tx) 92%, #fff); white-space: pre-line; overflow-wrap: anywhere; }
.hudm .hpop .tx b { color: #fff; } .hudm .hpop .tx .gold { color: var(--gold); font-weight: 700; }
.hudm .hpop .it { display: flex; align-items: center; gap: 10px; margin: 10px 18px 0 68px; padding: 6px 10px; border-radius: var(--rad); background: rgba(0,0,0,.25); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); font-size: 12px; }
.hudm .hpop .it i { width: 32px; height: 32px; flex: none; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .hpop .it b { color: #fff; } .hudm .hpop .it span { margin-left: auto; color: var(--dim); }
.hudm .hpop .body { padding: 12px 18px 0; }
.hudm .hpop .chk { display: flex; align-items: center; gap: 7px; margin: 12px 18px 0 68px; font-size: 11px; color: var(--dim); cursor: pointer; user-select: none; }
.hudm .hpop .chk i { width: 14px; height: 14px; border-radius: 4px; border: 1px solid var(--fr); background: var(--field); display: flex; align-items: center; justify-content: center; font-style: normal; font-size: 10px; color: #fff; }
.hudm .hpop .chk.on i { background: var(--ac); border-color: var(--ac); } .hudm .hpop .chk.on i::before { content: "✓"; }
.hudm .hpop .bt { display: flex; gap: 8px; justify-content: flex-end; padding: 16px 18px 16px; }
.hudm .hpop .b { position: relative; overflow: hidden; min-width: 84px; height: 30px; padding: 0 14px; display: inline-flex; align-items: center; justify-content: center; gap: 6px; cursor: pointer; user-select: none;
  border-radius: calc(var(--rad) * .9); font-size: 12px; font-weight: 600; letter-spacing: .04em; color: var(--tx); border: 1px solid var(--fr); background: linear-gradient(180deg, var(--pc), var(--pb)); box-shadow: 0 0 0 1px #000; }
.hudm .hpop .b:hover { border-color: var(--ac); color: #fff; }
.hudm .hpop .b.main { color: #fff; border-color: var(--kc); background: linear-gradient(180deg, color-mix(in srgb, var(--kc) 55%, var(--pc)), color-mix(in srgb, var(--kc) 28%, var(--pb))); box-shadow: 0 0 0 1px #000, 0 0 12px color-mix(in srgb, var(--kc) 40%, transparent); }
.hudm .hpop .b.dis { opacity: .4; cursor: not-allowed; }
.hudm .hpop .b kbd { font: inherit; font-size: 9px; padding: 0 4px; border-radius: 3px; border: 1px solid color-mix(in srgb, currentColor 40%, transparent); opacity: .7; }
.hudm .hpop .b .fillh { position: absolute; left: 0; top: 0; bottom: 0; width: 100%; transform-origin: 0 50%; transform: scaleX(0); background: rgba(255,255,255,.22); pointer-events: none; }
.hudm .hpop .b span { position: relative; }
.hudm .hpop .cd { position: absolute; left: 0; bottom: 0; height: 2px; width: 100%; transform-origin: 0 50%; background: var(--kc); }
.hudm .hpop .more { position: absolute; right: 12px; top: 10px; font-size: 10px; color: var(--dim); }
/* input */
.hudm .hpop .fldw { position: relative; }
.hudm .hpop input.in { width: 100%; height: 34px; padding: 0 44px 0 12px; border-radius: var(--rad); font: inherit; font-size: 13px; color: #fff; outline: none;
  background: var(--field); border: 1px solid var(--fr); box-shadow: inset 0 1px 3px #000; }
.hudm .hpop input.in:focus { border-color: var(--ac); box-shadow: inset 0 1px 3px #000, 0 0 10px var(--glow); }
.hudm .hpop .fldw.bad input.in { border-color: var(--bad); }
.hudm .hpop .fldw .cnt { position: absolute; right: 10px; top: 10px; font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .hpop .err { min-height: 16px; margin-top: 5px; font-size: 11px; color: var(--bad-soft); } .hudm .hpop .err.ok { color: var(--ok); }
.hudm .hpop .sug { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 4px; }
.hudm .hpop .sug div { padding: 2px 9px; border-radius: 10px; font-size: 11px; cursor: pointer; border: 1px solid var(--fr); color: var(--dim); }
.hudm .hpop .sug div:hover, .hudm .hpop .sug div.on { border-color: var(--ac); color: #fff; }
/* amount */
.hudm .hpop .amt { display: flex; align-items: center; gap: 8px; }
.hudm .hpop .amt .pm { width: 34px; height: 40px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 18px; font-weight: 700; cursor: pointer; user-select: none;
  border-radius: var(--rad); border: 1px solid var(--fr); background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .hpop .amt .pm:hover { border-color: var(--ac); color: #fff; }
.hudm .hpop .amt input.big { flex: 1; min-width: 0; height: 40px; text-align: center; font: inherit; font-size: 22px; font-weight: 800; color: #fff; letter-spacing: .02em; outline: none; font-variant-numeric: tabular-nums;
  border-radius: var(--rad); background: var(--field); border: 1px solid var(--fr); box-shadow: inset 0 1px 3px #000; }
.hudm .hpop .amt input.big:focus { border-color: var(--ac); box-shadow: inset 0 1px 3px #000, 0 0 10px var(--glow); }
.hudm .hpop .amt input.big.gold { color: var(--gold); }
.hudm .hpop input[type=range] { width: 100%; margin: 12px 0 2px; accent-color: var(--ac); }
.hudm .hpop .lim { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .hpop .chips { display: flex; gap: 6px; margin-top: 10px; }
.hudm .hpop .chips div { flex: 1; text-align: center; padding: 4px 0; border-radius: 6px; font-size: 11px; font-weight: 600; cursor: pointer; border: 1px solid var(--fr); color: var(--dim); }
.hudm .hpop .chips div:hover { border-color: var(--ac); color: #fff; }
.hudm .hpop .left { margin-top: 10px; font-size: 11px; color: var(--dim); text-align: center; } .hudm .hpop .left b { color: var(--tx); }
/* requests */
.hudm .hreqs { position: absolute; left: 50%; top: 132px; translate: -50% 0; display: flex; flex-direction: column; gap: 8px; pointer-events: none; }
.hudm .hreq { position: relative; overflow: hidden; width: 380px; display: flex; align-items: center; gap: 12px; padding: 10px 12px 12px; pointer-events: auto; border-radius: var(--rad); color: var(--tx); font-size: 12px;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--ac); box-shadow: 0 0 0 1px #000, 0 8px 24px rgba(0,0,0,.6), 0 0 18px var(--glow); animation: hudm-pin .25s ease-out; }
.hudm .hreq.out { animation: hudm-pout .2s ease-in forwards; }
.hudm .hreq .av { width: 36px; height: 36px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); }
.hudm .hreq .tg { display: block; font-size: 9px; font-weight: 700; letter-spacing: .14em; color: var(--ac); text-transform: uppercase; }
.hudm .hreq b { color: #fff; }
.hudm .hreq .msg { overflow-wrap: anywhere; }
.hudm .hreq .sp { flex: 1; }
.hudm .hreq .b { padding: 5px 12px; border-radius: 6px; cursor: pointer; font-weight: 600; font-size: 11px; border: 1px solid var(--fr); background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .hreq .b:hover { border-color: var(--ac); color: #fff; } .hudm .hreq .b.main { border-color: var(--ac); color: #fff; background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 50%, var(--pc)), color-mix(in srgb, var(--ac) 25%, var(--pb))); }
.hudm .hreq .bar { position: absolute; left: 0; bottom: 0; height: 2px; width: 100%; transform-origin: 0 50%; background: var(--ac); }
`;

interface Job {
  build: (done: (v: unknown) => void) => HTMLElement;
  resolve: (v: unknown) => void;
  keys?: (e: KeyboardEvent) => boolean;
  /** Set while the box shows: finishes it (animated) with a value. */
  done?: (v: unknown) => void;
  /** Runs when the box is taken down (timers). */
  cleanup?: () => void;
  /** The value close() resolves with. */
  cancelled: unknown;
}

export class Popups {
  readonly veil = h("div", "hpop-veil");
  readonly requests = h("div", "hreqs");
  private queue: Job[] = [];
  private current: Job | null = null;
  private box: HTMLElement | null = null;
  private readonly toasts = new Set<() => void>();

  /** `layer` = Layers.pops; `signal` = the UI scope (closeAll on abort). */
  constructor(layer: HTMLElement, private readonly signal: AbortSignal) {
    injectCss("core-popups", CSS);
    this.veil.hidden = true;
    layer.append(this.requests, this.veil);
    this.veil.addEventListener("pointerdown", (e) => { if (e.target === this.veil && this.box) restartAnim(this.box, "shake"); }, { signal });
    signal.addEventListener("abort", () => this.closeAll(), { once: true });
  }

  /** Whether a modal box is showing. */
  get open(): boolean { return !!this.current; }

  /** The input router's "modal" layer (the integrator registers it): while a box shows, its keys (Enter, Esc, Y / N,
   *  arrows / PageUp / PageDown) run and every key is consumed, so hotkeys and the window manager's Esc stay away.
   *  Keys typed into the box's own field still type (nothing is prevented unless the box used the key). */
  handleKey(e: KeyboardEvent): boolean {
    const job = this.current;
    if (!job) return false;
    if (!e.repeat || e.key.startsWith("Arrow") || e.key.startsWith("Page")) {
      let used = false;
      try { used = job.keys?.(e) === true; } catch (err) { console.error(err); }
      if (used) { e.preventDefault(); return true; }
    }
    // Keys outside the box (focus on the page) must not reach the game; keys inside keep their default (typing).
    return true;
  }

  private push<T>(cancelled: T, build: Job["build"], keys?: (job: Job) => Job["keys"]): PopupPromise<T> {
    let job!: Job;
    const p = new Promise<T>((resolve) => {
      job = { build, resolve: resolve as (v: unknown) => void, cancelled };
      if (keys) job.keys = keys(job);
    }) as PopupPromise<T>;
    p.close = () => this.dismiss(job);
    if (this.signal.aborted) { job.resolve(cancelled); return p; }
    this.queue.push(job);
    if (!this.current) this.next(); else this.badge();
    return p;
  }
  private dismiss(job: Job): void {
    const i = this.queue.indexOf(job);
    if (i >= 0) { this.queue.splice(i, 1); job.resolve(job.cancelled); this.badge(); return; }
    if (this.current === job) job.done?.(job.cancelled);
  }
  private next(): void {
    const job = this.queue.shift();
    this.current = job ?? null;
    if (!job) { this.veil.hidden = true; this.veil.replaceChildren(); this.box = null; return; }
    let finished = false;
    job.done = (v: unknown) => {
      if (finished) return; finished = true;
      job.cleanup?.();
      this.box?.classList.add("out");
      const go = () => { job.resolve(v); if (this.current === job) this.next(); };
      if (this.signal.aborted) go(); else later(go, 140, this.signal);
    };
    this.box = job.build(job.done);
    this.veil.replaceChildren(this.box);
    this.veil.hidden = false;
    this.badge();
    later(() => (this.box?.querySelector<HTMLElement>("input:not([type=range])") ?? this.box)?.focus({ preventScroll: true }), 0, this.signal);
  }
  private badge(): void {
    this.box?.querySelector(".more")?.remove();
    if (this.box && this.queue.length) this.box.append(h("div", "more", `+${this.queue.length} waiting`));
  }

  private shell(kind: Kind, title: string, text: string, icon?: { img: number }, html = false): { box: HTMLElement; bt: HTMLElement } {
    const badge = h("div", "badge");
    if (icon) { badge.classList.add("ico"); void libPicture(Lib.Items, icon.img).then((p) => { if (p) badge.style.backgroundImage = `url(${p.url}), radial-gradient(circle at 50% 35%, var(--pc), var(--pb))`; }); }
    else badge.append(svg(KIND_ICON[kind], ""));
    const tx = h("div", "tx");
    if (html) tx.innerHTML = text; // opt-in: trusted markup written in code only (ConfirmOpts.html), never data
    else tx.textContent = text;
    const box = h("div", "hpop " + kind, h("div", "hd", badge, h("h3", "", title)), tx);
    box.tabIndex = -1;
    box.setAttribute("role", "dialog");
    box.setAttribute("aria-modal", "true");
    box.setAttribute("aria-label", title);
    return { box, bt: h("div", "bt") };
  }
  private shake(): void { if (this.box) restartAnim(this.box, "shake"); }

  /** MirMessageBox. Resolves with the button chosen (and whether "Don't ask again" was ticked); null if closed. */
  confirm(o: ConfirmOpts): PopupPromise<{ answer: Answer; dontAsk: boolean } | null> {
    const kind = o.kind ?? (o.buttons?.startsWith("yes") ? "question" : "info");
    const buttons = o.buttons ?? "ok";
    const order = ({ ok: ["ok"], okcancel: ["cancel", "ok"], yesno: ["no", "yes"], yesnocancel: ["cancel", "no", "yes"], cancel: ["cancel"] } as Record<Buttons, Answer[]>)[buttons];
    const primary: Answer = order.includes("yes") ? "yes" : order.includes("ok") ? "ok" : "cancel";
    const escAnswer: Answer = order.includes("cancel") ? "cancel" : order.includes("no") ? "no" : "ok";
    const label = (a: Answer) => o.labels?.[a] ?? { ok: "OK", cancel: "Cancel", yes: "Yes", no: "No" }[a];
    let tick = false;
    let pick: (a: Answer) => void = () => {};
    return this.push<{ answer: Answer; dontAsk: boolean } | null>(null, (done) => {
      const { box, bt } = this.shell(kind, o.title ?? { info: "Notice", question: "Confirm", warning: "Warning", danger: "Are you sure?" }[kind], o.text, undefined, o.html);
      let timer = 0;
      pick = (a) => { window.clearInterval(timer); done({ answer: a, dontAsk: tick }); };
      const job = this.current!;
      job.cleanup = () => window.clearInterval(timer);
      if (o.item || o.gold !== undefined) {
        const i = h("i");
        void libPicture(Lib.Items, o.item ? o.item.img : GOLD_IMG).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
        box.append(h("div", "it", i, h("b", "", o.item ? o.item.name : "Gold"),
          h("span", "", o.item ? (o.item.count && o.item.count > 1 ? `× ${fmt(o.item.count)}` : "") : `${fmt(o.gold!)} gold`)));
      }
      if (o.dontAsk) {
        const c = asButton(h("div", "chk", h("i"), "Don't ask me again"));
        c.setAttribute("role", "checkbox");
        c.setAttribute("aria-checked", "false");
        c.addEventListener("click", () => { tick = !tick; c.classList.toggle("on", tick); c.setAttribute("aria-checked", String(tick)); });
        box.append(c);
      }
      const btns: Partial<Record<Answer, HTMLElement>> = {};
      for (const a of order) {
        const span = h("span", "", label(a));
        const b = asButton(h("div", "b" + ((a === primary && kind !== "info") || (a === primary && order.length === 1) ? " main" : ""), span));
        if (a === primary && !o.hold) b.append(h("kbd", "", "Enter")); else if (a !== primary && a === escAnswer) b.append(h("kbd", "", "Esc"));
        if (a === primary && o.hold) {
          const fill = h("div", "fillh"); b.prepend(fill);
          span.textContent = `Hold to ${label(a).toLowerCase()}`;
          let anim: Animation | null = null;
          const start = () => {
            if (anim && anim.playState === "running") return;
            anim = fill.animate([{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }], { duration: o.hold, fill: "forwards" });
            anim.onfinish = () => pick(a);
          };
          const stop = () => { if (anim && anim.playState !== "finished") anim.cancel(); };
          b.addEventListener("pointerdown", (e) => { if (e.button === 0) start(); });
          b.addEventListener("pointerup", stop); b.addEventListener("pointerleave", stop); b.addEventListener("pointercancel", stop);
          // Keyboard path (Review-Core "UX"): hold Enter or Space on the focused button.
          b.addEventListener("keydown", (e) => { if ((e.key === "Enter" || e.key === " ") && !e.repeat) { e.preventDefault(); e.stopPropagation(); start(); } }, { capture: true });
          b.addEventListener("keyup", (e) => { if (e.key === "Enter" || e.key === " ") stop(); });
          b.addEventListener("blur", stop);
        } else b.addEventListener("click", () => pick(a));
        btns[a] = b; bt.append(b);
      }
      box.append(bt);
      if (o.countdown) {
        const ta = o.timeoutAnswer ?? escAnswer, tb = btns[ta], bar = h("div", "cd"), ends = performance.now() + o.countdown * 1000;
        box.append(bar);
        bar.animate([{ transform: "scaleX(1)" }, { transform: "scaleX(0)" }], { duration: o.countdown * 1000, fill: "forwards" });
        let shownLeft = -1;
        const show = () => {
          const left = Math.max(0, Math.ceil((ends - performance.now()) / 1000));
          if (tb && left !== shownLeft) { shownLeft = left; (tb.querySelector("span") as HTMLElement).textContent = `${label(ta)} (${left})`; }
          if (left <= 0) pick(ta);
        };
        show(); timer = window.setInterval(show, 250);
      }
      return box;
    }, () => (e) => {
      if (e.key === "Enter" && !o.hold) { pick(primary); return true; }
      if (e.key === "Escape") { pick(escAnswer); return true; }
      if (order.includes("yes") && (e.key === "y" || e.key === "Y")) { if (!o.hold) pick("yes"); return true; }
      if (order.includes("no") && (e.key === "n" || e.key === "N")) { pick("no"); return true; }
      return false;
    });
  }

  /** Shorthand: a Yes / No (or OK / Cancel) box that resolves true on the primary answer. */
  ask(text: string, o: Omit<ConfirmOpts, "text" | "buttons"> & { okCancel?: boolean } = {}): PopupPromise<boolean> {
    const p = this.confirm({ ...o, text, buttons: o.okCancel ? "okcancel" : "yesno" });
    const q = p.then((r) => r?.answer === "yes" || r?.answer === "ok") as PopupPromise<boolean>;
    q.close = p.close;
    return q;
  }

  /** MirInputBox. Resolves with the text (trimmed), or null when cancelled. */
  input(o: InputOpts): PopupPromise<string | null> {
    let submit: () => void = () => {}, cancel: () => void = () => {};
    return this.push<string | null>(null, (done) => {
      const { box, bt } = this.shell("question", o.title ?? "Enter a value", o.text, undefined, o.html);
      const inp = h("input", "in");
      inp.placeholder = o.placeholder ?? ""; inp.value = o.value ?? ""; inp.autocomplete = "off"; inp.spellcheck = false;
      if (o.password) inp.type = "password";
      if (o.max) inp.maxLength = o.max;
      const cnt = h("div", "cnt"), err = h("div", "err"), fld = h("div", "fldw", inp, cnt);
      const ok = asButton(h("div", "b main", h("span", "", "OK"), h("kbd", "", "Enter"))), no = asButton(h("div", "b", h("span", "", "Cancel"), h("kbd", "", "Esc")));
      const charsRe = o.chars ? new RegExp(`[^${o.chars}]`) : null;
      const why = (v: string): string => {
        if (o.min && v.length < o.min) return v.length ? `At least ${o.min} characters` : "";
        if (charsRe?.test(v)) return "Letters and numbers only";
        return o.check?.(v) ?? "";
      };
      const sug = h("div", "sug");
      const refresh = () => {
        const v = inp.value, w = why(v);
        cnt.textContent = o.max ? `${v.length}/${o.max}` : "";
        err.textContent = w || (v && (o.min || o.chars || o.check) ? "✓ Looks good" : "");
        err.classList.toggle("ok", !w && !!v);
        fld.classList.toggle("bad", !!w);
        ok.classList.toggle("dis", !!w || (!o.allowEmpty && !v.trim()));
        for (const s of sug.children) s.classList.toggle("on", (s as HTMLElement).textContent === v);
      };
      submit = () => { refresh(); if (!ok.classList.contains("dis")) done(inp.value.trim()); else this.shake(); };
      cancel = () => done(null);
      inp.addEventListener("input", refresh);
      for (const s of o.suggestions ?? []) { const c = h("div", "", s); c.addEventListener("click", () => { inp.value = s; refresh(); inp.focus(); }); sug.append(c); }
      ok.addEventListener("click", submit); no.addEventListener("click", cancel);
      bt.append(no, ok);
      box.append(h("div", "body", fld, err, o.suggestions?.length ? sug : null), bt);
      refresh();
      return box;
    }, () => (e) => {
      if (e.key === "Enter") { submit(); return true; }
      if (e.key === "Escape") { cancel(); return true; }
      return false;
    });
  }

  /** MirAmountBox. Resolves with the amount, or null when cancelled. */
  amount(o: AmountOpts): PopupPromise<number | null> {
    const min = o.min ?? (o.gold ? 0 : 1), max = Math.max(min, o.max);
    let val = Math.min(max, Math.max(min, o.value ?? max));
    let submit: () => void = () => {}, cancel: () => void = () => {}, nudge: (d: number) => void = () => {};
    return this.push<number | null>(null, (done) => {
      const { box, bt } = this.shell("question", o.title ?? "How many?", o.text ?? "", { img: o.gold || !o.item ? GOLD_IMG : o.item.img }, o.html);
      if (!o.text) box.querySelector(".tx")!.remove();
      const job = this.current!;
      const big = h("input", "big" + (o.gold ? " gold" : ""));
      big.inputMode = "decimal"; big.autocomplete = "off";
      const range = h("input"); range.type = "range"; range.min = String(min); range.max = String(max); range.step = String(Math.max(1, Math.round((max - min) / 400)));
      const left = h("div", "left");
      const ok = asButton(h("div", "b main", h("span", "", "OK"), h("kbd", "", "Enter"))), no = asButton(h("div", "b", h("span", "", "Cancel"), h("kbd", "", "Esc")));
      const set = (v: number, from?: HTMLElement) => {
        val = Math.min(max, Math.max(min, Math.round(v)));
        if (from !== big) big.value = fmt(val);
        if (from !== range) range.value = String(val);
        left.replaceChildren(o.gold ? "You'll have " : "", h("b", "", fmt(max - val)), ` ${o.leftLabel ?? (o.gold ? "gold left" : "left in the stack")}`);
        ok.classList.toggle("dis", val < min || (val === 0 && !o.gold));
      };
      nudge = (d) => set(val + d);
      big.addEventListener("input", () => { const n = parseAmount(big.value); if (!isNaN(n)) set(n, big); });
      big.addEventListener("blur", () => set(val));
      range.addEventListener("input", () => set(+range.value, range));
      // - / +: one step, then faster while held.
      const holds: number[] = [];
      const pm = (sign: number, text: string) => {
        const b = h("div", "pm", text);
        b.setAttribute("role", "button");
        b.setAttribute("aria-label", sign > 0 ? "More" : "Less");
        let n = 0;
        const step = () => {
          const s = n < 6 ? 1 : n < 16 ? Math.max(1, Math.round(max / 200)) : Math.max(1, Math.round(max / 40));
          set(val + sign * s); n++;
          holds[sign > 0 ? 1 : 0] = window.setTimeout(step, n < 6 ? 220 : 70);
        };
        const stop = () => { window.clearTimeout(holds[sign > 0 ? 1 : 0]); n = 0; };
        b.addEventListener("pointerdown", (e) => { if (e.button === 0) step(); });
        b.addEventListener("pointerup", stop); b.addEventListener("pointerleave", stop); b.addEventListener("pointercancel", stop);
        return b;
      };
      job.cleanup = () => { for (const t of holds) window.clearTimeout(t); };
      const chips = h("div", "chips");
      const quick: [string, number][] = o.gold ? [["10k", 1e4], ["100k", 1e5], ["1M", 1e6], ["Half", Math.floor(max / 2)], ["All", max]] : [["1", 1], ["10", 10], ["Half", Math.floor(max / 2)], ["Max", max]];
      for (const [l, v] of quick) { if (v > max && l !== "Max" && l !== "All") continue; const c = h("div", "", l); c.addEventListener("click", () => set(v)); chips.append(c); }
      submit = () => { set(parseAmount(big.value) || val); if (!ok.classList.contains("dis")) done(val); else this.shake(); };
      cancel = () => done(null);
      ok.addEventListener("click", submit); no.addEventListener("click", cancel);
      bt.append(no, ok);
      const name = o.item ? h("div", "lim", h("span", "", o.item.name), h("span", "", `max ${fmt(max)}`)) : h("div", "lim", h("span", "", `min ${fmt(min)}`), h("span", "", `max ${fmt(max)}`));
      box.append(h("div", "body", h("div", "amt", pm(-1, "−"), big, pm(1, "+")), range, name, chips, left), bt);
      set(val);
      later(() => big.select(), 0, this.signal);
      return box;
    }, () => (e) => {
      if (e.key === "Enter") { submit(); return true; }
      if (e.key === "Escape") { cancel(); return true; }
      if (e.key === "ArrowUp" || e.key === "ArrowDown") { nudge(e.key === "ArrowUp" ? 1 : -1); return true; }
      if (e.key === "PageUp" || e.key === "PageDown") { nudge((e.key === "PageUp" ? 1 : -1) * Math.max(1, Math.round(max / 10))); return true; }
      return false;
    });
  }

  /** An invite toast (not modal): Accept / Decline, declines on its own when the bar runs out. close() declines. */
  request(o: RequestOpts): PopupPromise<boolean> {
    let end: (v: boolean) => void = () => {};
    const p = new Promise<boolean>((resolve) => {
      if (this.signal.aborted) { resolve(false); return; }
      const secs = o.seconds ?? 20;
      const yes = asButton(h("div", "b main", o.accept ?? "Accept")), no = asButton(h("div", "b", o.decline ?? "Decline")), bar = h("div", "bar");
      const el = h("div", "hreq", h("div", "av", o.from.slice(0, 3).toUpperCase()),
        h("div", "msg", o.tag ? h("span", "tg", o.tag) : null, h("b", "", o.from), " ", o.text), h("div", "sp"), no, yes, bar);
      el.setAttribute("role", "alertdialog");
      let over = false;
      end = (v: boolean) => {
        if (over) return; over = true;
        this.toasts.delete(drop);
        anim.cancel();
        el.classList.add("out");
        if (this.signal.aborted) el.remove(); else later(() => el.remove(), 200, this.signal);
        resolve(v);
      };
      const drop = () => end(false);
      yes.addEventListener("click", () => end(true)); no.addEventListener("click", () => end(false));
      const anim = bar.animate([{ transform: "scaleX(1)" }, { transform: "scaleX(0)" }], { duration: secs * 1000, fill: "forwards" });
      anim.onfinish = () => end(false);
      this.toasts.add(drop);
      this.requests.append(el);
    }) as PopupPromise<boolean>;
    p.close = () => end(false);
    return p;
  }

  /** Closes everything (log-out, dispose): open and queued boxes resolve as cancelled, requests as declined. */
  closeAll(): void {
    const q = this.queue;
    this.queue = [];
    for (const j of q) j.resolve(j.cancelled);
    if (this.current) {
      const cur = this.current;
      cur.cleanup?.();
      this.current = null;
      this.veil.hidden = true; this.veil.replaceChildren(); this.box = null;
      cur.resolve(cur.cancelled);
    }
    for (const t of [...this.toasts]) t();
  }
}
