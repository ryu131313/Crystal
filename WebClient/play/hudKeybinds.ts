// Key bindings window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// KeyboardLayoutDialog: a full keyboard (main block, navigation cluster, numpad) that lights every key bound on the shown
// modifier layer (none / Shift / Ctrl / Alt / ~ held, combinable: click the chips or the keyboard's own modifier keys)
// with a short action name on the key, conflicts in red; hovering a key lists everything on it across all layers. Below
// it: groups (Dialogs, Skillbar, Belt, General, Toggle, Combat) with counts, filters (All / Changed / Conflicts /
// Unbound), search, and the bind list with keycap chips: click a chip, then press a key (with its modifiers) or click a
// key on the keyboard; Esc cancels, Backspace unbinds; a reset arrow per bind; a conflict note names the other bind and
// can clear it. Restore defaults / Cancel / Apply with an unsaved count. The binds and defaults are the client's
// (src/game/keybinds.ts = the desktop's KeyBindSettings). A bind fires when each modifier's Require matches (0 = not
// held, 1 = held, 2 = either), so two binds on one key clash only if no modifier is 1 on one and 0 on the other.
// Nothing is saved: the lab shares the web client's origin, so this must never write "crystal.keybinds".

import { defaultKeyBind, isModifierKeyName, keyBinds, keyName, tildeHeld, type KeyBind } from "../src/game/keybinds.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

type Mod = "shift" | "ctrl" | "alt" | "tilde";
const MODS: Mod[] = ["tilde", "ctrl", "alt", "shift"];
const MOD_LABEL: Record<Mod, string> = { shift: "Shift", ctrl: "Ctrl", alt: "Alt", tilde: "~" };
type Bind = { key: string } & Record<Mod, number>;
interface Def { fn: number; group: string; desc: string; def: Bind }
const fromKb = (b: KeyBind): Bind => ({ key: b.key, ctrl: b.requireCtrl, alt: b.requireAlt, shift: b.requireShift, tilde: b.requireTilde });
const UNBOUND: Bind = { key: "", ctrl: 2, alt: 2, shift: 2, tilde: 2 };
const same = (a: Bind, b: Bind) => a.key === b.key && MODS.every((m) => a[m] === b[m]);
/** Two binds can fire on the same press: same key and no modifier that one requires held and the other requires not. */
const clashes = (a: Bind, b: Bind) => !!a.key && a.key === b.key && MODS.every((m) => !((a[m] === 1 && b[m] === 0) || (a[m] === 0 && b[m] === 1)));
/** Whether a bind fires with exactly these modifiers held. */
const firesOn = (b: Bind, held: Record<Mod, boolean>) => MODS.every((m) => b[m] === 2 || (b[m] === 1) === held[m]);

/** Keys name → keycap text. */
const CAP: Record<string, string> = {
  Escape: "Esc", PrintScreen: "PrtSc", Scroll: "ScrLk", Pause: "Pause", Oem8: "`", OemMinus: "-", Oemplus: "=", Back: "Backspace",
  Tab: "Tab", OemOpenBrackets: "[", Oem6: "]", Oem5: "\\", Capital: "Caps", Oem1: ";", Oem7: "'", Return: "Enter", ShiftKey: "Shift",
  Oemcomma: ",", OemPeriod: ".", OemQuestion: "/", ControlKey: "Ctrl", LWin: "Win", Menu: "Alt", Space: "Space", RWin: "Win", Apps: "Menu",
  Insert: "Ins", Home: "Home", PageUp: "PgUp", Delete: "Del", End: "End", Next: "PgDn", Up: "↑", Down: "↓", Left: "←", Right: "→",
  NumLock: "Num", Divide: "/", Multiply: "*", Subtract: "-", Add: "+", Decimal: ".",
};
const cap = (k: string) => CAP[k] ?? k.replace(/^D(\d)$/, "$1").replace(/^NumPad(\d)$/, "$1");
/** A key on the board: Keys name, width in units (default 1), extra class. "" = a gap of that width. */
type K = [string, number?, string?];
const F = (a: number, b: number): K[] => Array.from({ length: b - a + 1 }, (_, i) => [`F${a + i}`] as K);
const L = (s: string): K[] => [...s].map((c) => [/\d/.test(c) ? `D${c}` : c] as K);
const MAIN: K[][] = [
  [["Escape"], ["", 1], ...F(1, 4), ["", .5], ...F(5, 8), ["", .5], ...F(9, 12)],
  [["Oem8", 1, "mod-tilde"], ...L("1234567890"), ["OemMinus"], ["Oemplus"], ["Back", 2]],
  [["Tab", 1.5], ...L("QWERTYUIOP"), ["OemOpenBrackets"], ["Oem6"], ["Oem5", 1.5]],
  [["Capital", 1.75], ...L("ASDFGHJKL"), ["Oem1"], ["Oem7"], ["Return", 2.25]],
  [["ShiftKey", 2.25, "mod-shift"], ...L("ZXCVBNM"), ["Oemcomma"], ["OemPeriod"], ["OemQuestion"], ["ShiftKey", 2.75, "mod-shift"]],
  [["ControlKey", 1.25, "mod-ctrl"], ["LWin", 1.25], ["Menu", 1.25, "mod-alt"], ["Space", 6.25], ["Menu", 1.25, "mod-alt"], ["RWin", 1.25], ["Apps", 1.25], ["ControlKey", 1.25, "mod-ctrl"]],
];
const NAV: K[][] = [
  [["PrintScreen"], ["Scroll"], ["Pause"]], [["Insert"], ["Home"], ["PageUp"]], [["Delete"], ["End"], ["Next"]], [], [["", 1], ["Up"], ["", 1]], [["Left"], ["Down"], ["Right"]],
];
const PAD: K[][] = [
  [], [["NumLock"], ["Divide"], ["Multiply"], ["Subtract"]], [["NumPad7"], ["NumPad8"], ["NumPad9"], ["Add"]], [["NumPad4"], ["NumPad5"], ["NumPad6"], ["", 1]],
  [["NumPad1"], ["NumPad2"], ["NumPad3"], ["Return"]], [["NumPad0", 2], ["Decimal"], ["", 1]],
];
/** A short action name for a keycap. */
function short(desc: string): string {
  return desc.replace(" Open/Close Alt", "").replace(" Open/Close", "").replace("Hero Skillbar Slot ", "Hero ").replace("Skillbar Alt Slot ", "Bar2 ")
    .replace("Skillbar Slot ", "Skill ").replace(/Belt Slot (\d) Alt/, "Belt $1").replace("Belt Slot ", "Belt ").replace("Set Attack Mode : ", "Atk ")
    .replace("Set Pet Mode : ", "Pet ").replace("Toggle ", "").replace("Close All Windows", "Close all").replace("Pickup Floor Item", "Pick up")
    .replace("Creature Item Pickup", "Pet pickup").replace("Creature Auto Pickup", "Auto pickup").replace("Recruit Group Member", "Recruit")
    .replace("Hold to enable target spell lock-on", "Lock-on").replace("Request Trade", "Trade").replace("Quest Diary", "Quests").replace("Take Screenshot", "Screenshot")
    // Keycaps are ~30px wide: one-word names longer than that get a short form (two-word ones wrap onto the second line).
    .replace(/^(Hero )?Inventory$/, "$1Bag").replace(/^(Hero )?Equipment$/, "$1Gear").replace(/^Creatures$/, "Pets").replace(/^Gameshop$/, "Shop").replace(/^Keybinds$/, "Keys")
    .replace(/^Relationship$/, "Spouse").replace(/^Minimap$/, "Mini map").replace("Mount/Dismount", "Ride").replace(/^Screenshot$/, "Screen shot");
}
const GROUP_ICON: Record<string, string> = {
  Dialogs: "M4 5h16v14H4zM4 9h16", Skillbar: "M13 3L5 14h6l-1 7 8-11h-6z", Belt: "M9 3h6v4l2 3v10H7V10l2-3zM9 12h6",
  General: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 8v5l3 2", Toggle: "M7 8h10a4 4 0 010 8H7a4 4 0 010-8zM7 10a2 2 0 100 4 2 2 0 000-4", Combat: "M5 19L19 5M14 5h5v5M5 14l5 5",
};

const CSS = `
.hudm .kbwin { width: 1000px; }
.hudm .kbwin .panel { height: 640px; display: flex; flex-direction: column; }
/* Layer bar */
.hudm .kbwin .layer { display: flex; align-items: center; gap: 6px; padding: 8px 14px 0; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .kbwin .chip2 { height: 22px; padding: 0 10px; display: inline-flex; align-items: center; cursor: pointer; border-radius: 11px; font-size: 11px; letter-spacing: .04em; text-transform: none;
  color: var(--tx); border: 1px solid var(--fr); background: var(--pb); }
.hudm .kbwin .chip2:hover { border-color: var(--ac); }
.hudm .kbwin .chip2.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .kbwin .layer .sp { flex: 1; }
.hudm .kbwin .layer .stat b { color: var(--ac); font-size: 12px; letter-spacing: 0; } .hudm .kbwin .layer .stat b.r { color: #ff7a6a; }
/* Keyboard */
.hudm .kbwin .board { --u: 36px; --g: 3px; display: flex; gap: 14px; justify-content: center; padding: 10px 14px 8px; position: relative; }
.hudm .kbwin .blk { display: flex; flex-direction: column; gap: var(--g); }
.hudm .kbwin .krow { display: flex; gap: var(--g); height: var(--u); }
.hudm .kbwin .krow:first-child { margin-bottom: 6px; }
.hudm .kbwin .k { position: relative; width: calc(var(--u) * var(--w, 1) + var(--g) * (var(--w, 1) - 1)); height: var(--u); flex: none; box-sizing: border-box; cursor: pointer; user-select: none;
  border-radius: calc(var(--rad) * .6); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 80%, #000), color-mix(in srgb, var(--pb) 80%, #000));
  box-shadow: 0 2px 0 rgba(0,0,0,.7), inset 0 1px 0 color-mix(in srgb, var(--frl) 15%, transparent); transition: border-color .15s, box-shadow .15s, background .15s; }
.hudm .kbwin .k.gap { visibility: hidden; }
.hudm .kbwin .k > b { position: absolute; left: 4px; top: 2px; font: 600 10px ui-monospace, Consolas, monospace; color: var(--dim); }
.hudm .kbwin .k > span { position: absolute; left: 2px; right: 2px; bottom: 2px; font-size: 8px; line-height: 1.05; font-weight: 700; color: #fff; text-align: center; overflow: hidden; overflow-wrap: anywhere;
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; text-shadow: 0 1px 2px #000; }
.hudm .kbwin .k > i { position: absolute; right: 3px; top: 2px; font-style: normal; font-size: 8px; font-weight: 800; color: var(--ac); }
.hudm .kbwin .k.on { border-color: color-mix(in srgb, var(--ac) 70%, transparent); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 30%, var(--pb)), color-mix(in srgb, var(--ac) 12%, var(--pb)));
  box-shadow: 0 2px 0 rgba(0,0,0,.7), 0 0 calc(7px * var(--gl)) color-mix(in srgb, var(--ac) 45%, transparent); }
.hudm .kbwin .k.on > b { color: #fff; }
.hudm .kbwin .k.clash { border-color: #ff5a4a; background: linear-gradient(180deg, #5a1a18, #2a0c0c); box-shadow: 0 2px 0 rgba(0,0,0,.7), 0 0 8px rgba(255,90,74,.55); }
.hudm .kbwin .k.modk { border-color: color-mix(in srgb, var(--fr) 90%, transparent); }
.hudm .kbwin .k.modk > b { color: var(--tx); }
.hudm .kbwin .k.held { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 45%, var(--pb)); box-shadow: 0 0 0 1px var(--ac), 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .kbwin .k.held > b { color: #fff; }
.hudm .kbwin .k.focus { outline: 2px solid #fff; outline-offset: 1px; }
.hudm .kbwin .k:hover { border-color: #fff; }
.hudm .kbwin.waiting .k:not(.modk):not(.gap) { animation: hudm-kbpulse 1.4s ease-in-out infinite; }
@keyframes hudm-kbpulse { 50% { border-color: var(--ac); } }
/* Info strip */
.hudm .kbwin .info { margin: 0 14px; min-height: 30px; display: flex; align-items: center; flex-wrap: wrap; gap: 6px; padding: 4px 10px; border-radius: var(--rad); font-size: 11px; color: var(--dim);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .kbwin .info .kc { margin-right: 2px; }
.hudm .kbwin .info em { font-style: normal; color: var(--tx); } .hudm .kbwin .info .dot { color: var(--fr); }
.hudm .kbwin .info.wait { color: #fff; border-color: var(--ac); box-shadow: 0 0 10px var(--glow); }
/* Lower */
.hudm .kbwin .low { flex: 1; min-height: 0; display: flex; margin-top: 8px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .kbwin .side { width: 190px; flex: none; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; overflow-y: auto; scrollbar-width: thin; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .kbwin .side input { height: 26px; box-sizing: border-box; padding: 0 9px; margin-bottom: 4px; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #05070c; color: var(--tx); font-size: 12px; outline: none; }
.hudm .kbwin .side input:focus { border-color: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .kbwin .kgi { display: flex; align-items: center; gap: 8px; padding: 3px 8px; flex: none; border-radius: var(--rad); cursor: pointer; font-size: 12px; color: var(--tx); border: 1px solid transparent; }
.hudm .kbwin .kgi:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .kbwin .kgi.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .kbwin .kgi svg { width: 15px; height: 15px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; opacity: .8; }
.hudm .kbwin .kgi span { flex: 1; } .hudm .kbwin .kgi small { font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; } .hudm .kbwin .kgi small.r { color: #ff7a6a; }
.hudm .kbwin .side .sep { height: 1px; margin: 4px 0; background: color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .kbwin .lcol { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .kbwin .filt { display: flex; gap: 6px; padding: 8px 10px 2px; }
.hudm .kbwin .filt small { margin-left: 6px; font-size: 10px; color: var(--dim); } .hudm .kbwin .filt .on small { color: #fff; }
.hudm .kbwin .chip2.warn { border-color: #ff5a4a; } .hudm .kbwin .chip2.warn small { color: #ff8a6a; }
.hudm .kbwin .list { flex: 1; min-height: 0; overflow-y: auto; padding: 6px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .kbwin .gh { position: sticky; top: -6px; z-index: 1; padding: 7px 4px 4px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac);
  background: color-mix(in srgb, var(--pb) 55%, #000); }
.hudm .kbwin .br { display: flex; align-items: center; gap: 10px; padding: 4px 8px; min-height: 30px; border-radius: var(--rad); font-size: 12px; border: 1px solid transparent; }
.hudm .kbwin .br:nth-of-type(even) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .kbwin .br:hover { border-color: color-mix(in srgb, var(--fr) 80%, transparent); }
.hudm .kbwin .br.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .kbwin .br .d { flex: 1; min-width: 0; } .hudm .kbwin .br .d small { display: block; font-size: 10px; color: #ff8a6a; }
.hudm .kbwin .br .d small a { color: var(--ac); cursor: pointer; text-decoration: underline; margin-left: 4px; }
.hudm .kbwin .br.chg .d::before { content: "●"; color: var(--ac); font-size: 9px; margin-right: 6px; vertical-align: 1px; }
.hudm .kbwin .rst { width: 20px; text-align: center; cursor: pointer; color: var(--dim); visibility: hidden; }
.hudm .kbwin .br.mod .rst { visibility: visible; } .hudm .kbwin .rst:hover { color: var(--ac); }
.hudm .kbwin .bind { min-width: 150px; display: flex; justify-content: flex-end; align-items: center; gap: 3px; cursor: pointer; padding: 2px 4px; border-radius: calc(var(--rad) * .7); border: 1px dashed transparent; }
.hudm .kbwin .bind:hover { border-color: var(--fr); }
.hudm .kbwin .bind.wait { border: 1px solid var(--ac); box-shadow: 0 0 10px var(--glow); color: #fff; font-size: 11px; animation: hudm-kbpulse 1s ease-in-out infinite; }
.hudm .kbwin .bind .un { font-size: 11px; font-style: italic; color: var(--dim); }
.hudm .kbwin .bind .plus { color: var(--dim); font-size: 10px; }
.hudm .kbwin .kc { display: inline-flex; align-items: center; justify-content: center; min-width: 22px; height: 20px; padding: 0 6px; box-sizing: border-box; font: 600 10px ui-monospace, Consolas, monospace;
  color: #fff; border-radius: 4px; border: 1px solid var(--fr); border-bottom-width: 2px; background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .kbwin .kc.m { color: var(--ac); }
.hudm .kbwin .br.conf .kc:not(.m) { border-color: #ff5a4a; color: #ffb0a6; }
.hudm .kbwin .none { padding: 30px; text-align: center; color: var(--dim); font-size: 12px; }
/* Footer */
.hudm .kbwin .foot { display: flex; align-items: center; gap: 8px; padding: 8px 14px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: color-mix(in srgb, var(--pb) 50%, #000);
  border-radius: 0 0 calc(var(--rad) - 1px) calc(var(--rad) - 1px); }
.hudm .kbwin .foot .st { flex: 1; font-size: 11px; color: var(--dim); } .hudm .kbwin .foot .st b { color: var(--ac); } .hudm .kbwin .foot .st.ok { color: #7fdc8a; }
.hudm .kbwin .btn.kmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .kbwin .btn.dis { opacity: .4; pointer-events: none; }
`;

type Filter = "All" | "Changed" | "Conflicts" | "Unbound";

export class KeybindsMock extends HudWindow {
  private defs: Def[] = keyBinds().map((b) => ({ fn: b.function, group: b.group, desc: b.description, def: fromKb(defaultKeyBind(b.function) ?? b) }));
  private saved = new Map<number, Bind>(keyBinds().map((b) => [b.function, fromKb(b)]));
  private draft = new Map<number, Bind>([...this.saved].map(([k, v]) => [k, { ...v }]));
  private held: Record<Mod, boolean> = { shift: false, ctrl: false, alt: false, tilde: false };
  private group = "All";
  private filter: Filter = "All";
  private query = "";
  private waiting: number | null = null;
  private sel: number | null = null;
  private hover: string | null = null;
  private focusKey: string | null = null;
  private applied = false;
  private board!: HTMLElement;
  private info!: HTMLElement;
  private side!: HTMLElement;
  private list!: HTMLElement;
  private filters!: HTMLElement;
  private foot!: HTMLElement;
  private layerEl!: HTMLElement;

  constructor() {
    super("kbwin", 12, 14);
    injectCss("hudm-keybinds-css", CSS);
    this.build();
    // Rebinding: the next key press with its modifiers (Require 1 for each held, 0 for each not) becomes the bind.
    window.addEventListener("keydown", (e) => {
      if (this.waiting === null || !this.isFront) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      const name = keyName(e);
      if (isModifierKeyName(name)) return;
      if (name === "Escape") { this.waiting = null; this.renderAll(); return; }
      this.assign(name === "Back" ? { ...UNBOUND } : { key: name, ctrl: e.ctrlKey ? 1 : 0, alt: e.altKey ? 1 : 0, shift: e.shiftKey ? 1 : 0, tilde: tildeHeld() ? 1 : 0 });
    }, { capture: true });
  }
  protected override shown(): void { this.renderAll(); }
  /** A rebind still waiting for its key is dropped on close, so the next key after reopening is not taken as a bind. */
  protected override closed(): void { this.waiting = null; this.el.classList.remove("waiting"); }

  private b(fn: number): Bind { return this.draft.get(fn)!; }
  private def(fn: number): Def { return this.defs.find((d) => d.fn === fn)!; }
  private clashOf(fn: number): Def | undefined { const me = this.b(fn); return this.defs.find((o) => o.fn !== fn && clashes(me, this.b(o.fn))); }
  private changed(): Def[] { return this.defs.filter((d) => !same(this.b(d.fn), this.saved.get(d.fn)!)); }
  private conflicts(): Def[] { return this.defs.filter((d) => this.clashOf(d.fn)); }

  private assign(bind: Bind): void {
    const fn = this.waiting!;
    this.waiting = null;
    this.draft.set(fn, bind);
    this.sel = fn;
    this.applied = false;
    if (bind.key) { this.focusKey = bind.key; for (const m of MODS) this.held[m] = bind[m] === 1; }
    this.renderAll();
  }

  private build(): void {
    this.layerEl = h("div", "layer");
    this.board = h("div", "board");
    this.info = h("div", "info");
    this.side = h("div", "side");
    this.list = h("div", "list");
    this.filters = h("div", "filt");
    this.foot = h("div", "foot");
    this.el.append(h("div", "panel", this.titleBar("KEY BINDINGS", "Close (U)"), this.layerEl, this.board, this.info, h("div", "low", this.side, h("div", "lcol", this.filters, this.list)), this.foot));
  }

  private renderAll(): void {
    this.el.classList.toggle("waiting", this.waiting !== null);
    this.renderLayer(); this.renderBoard(); this.renderInfo(); this.renderSide(); this.renderFilters(); this.renderList(); this.renderFoot();
  }

  private renderLayer(): void {
    const none = h("div", "chip2" + (MODS.some((m) => this.held[m]) ? "" : " on"), "No modifier");
    none.addEventListener("click", () => { for (const m of MODS) this.held[m] = false; this.renderAll(); });
    const chips = MODS.map((m) => {
      const c = h("div", "chip2" + (this.held[m] ? " on" : ""), m === "tilde" ? "~ held" : MOD_LABEL[m]);
      c.addEventListener("click", () => { this.held[m] = !this.held[m]; this.renderAll(); });
      return c;
    });
    const lit = this.defs.filter((d) => this.b(d.fn).key && firesOn(this.b(d.fn), this.held)).length;
    const nc = this.conflicts().length;
    this.layerEl.replaceChildren("Layer", none, ...chips, h("span", "sp"),
      h("span", "stat", h("b", "", String(lit)), " on this layer · ", h("b", nc ? "r" : "", String(nc)), ` conflict${nc === 1 ? "" : "s"}`));
  }

  /** Binds that fire on a key with the shown layer's modifiers. */
  private onKey(key: string): Def[] { return this.defs.filter((d) => this.b(d.fn).key === key && firesOn(this.b(d.fn), this.held)); }

  private renderBoard(): void {
    const block = (rows: K[][]) => {
      const blk = h("div", "blk");
      for (const row of rows) {
        const r = h("div", "krow");
        for (const [key, w = 1, cls = ""] of row) r.append(this.keyEl(key, w, cls));
        blk.append(r);
      }
      return blk;
    };
    this.board.replaceChildren(block(MAIN), block(NAV), block(PAD));
  }

  private keyEl(key: string, w: number, cls: string): HTMLElement {
    const k = h("div", "k");
    k.style.setProperty("--w", String(w));
    if (!key) { k.classList.add("gap"); return k; }
    const mod = cls.startsWith("mod-") ? cls.slice(4) as Mod : null;
    k.append(h("b", "", cap(key)));
    if (mod) {
      k.classList.add("modk");
      if (this.held[mod]) k.classList.add("held");
      k.title = `Show the ${MOD_LABEL[mod]} layer`;
      k.addEventListener("click", () => { this.held[mod] = !this.held[mod]; this.renderAll(); });
      return k;
    }
    const binds = this.onKey(key);
    if (binds.length) {
      k.classList.add("on");
      k.append(h("span", "", binds.map((d) => short(d.desc)).join(" · ")));
      if (binds.length > 1) k.append(h("i", "", "×" + binds.length));
      if (binds.some((d) => this.clashOf(d.fn))) k.classList.add("clash");
    }
    if (this.focusKey === key) k.classList.add("focus");
    k.addEventListener("mouseenter", () => { this.hover = key; this.renderInfo(); });
    k.addEventListener("mouseleave", () => { if (this.hover === key) { this.hover = null; this.renderInfo(); } });
    k.addEventListener("click", () => {
      if (this.waiting !== null) { this.assign({ key, ctrl: this.held.ctrl ? 1 : 0, alt: this.held.alt ? 1 : 0, shift: this.held.shift ? 1 : 0, tilde: this.held.tilde ? 1 : 0 }); return; }
      this.focusKey = this.focusKey === key ? null : key;
      if (this.focusKey) { this.group = "All"; this.filter = "All"; this.query = ""; }
      this.renderAll();
    });
    return k;
  }

  /** A bind as keycap chips. */
  private caps(b: Bind): HTMLElement[] {
    if (!b.key) return [h("span", "un", "Unbound")];
    const out: HTMLElement[] = [];
    for (const m of MODS) if (b[m] === 1) out.push(h("span", "kc m", MOD_LABEL[m]), h("span", "plus", "+"));
    out.push(h("span", "kc", cap(b.key)));
    return out;
  }

  private renderInfo(): void {
    this.info.className = "info" + (this.waiting !== null ? " wait" : "");
    if (this.waiting !== null) {
      this.info.replaceChildren(`Rebinding “${this.def(this.waiting).desc}”: press a key with any modifiers, or click a key on the board (uses the shown layer). Esc cancels, Backspace unbinds.`);
      return;
    }
    const key = this.hover ?? this.focusKey;
    if (!key) { this.info.replaceChildren("Hover a key to see everything bound to it. Click a key to list its binds, click a bind's keys to change them."); return; }
    const all = this.defs.filter((d) => this.b(d.fn).key === key);
    if (!all.length) { this.info.replaceChildren(h("span", "kc", cap(key)), " Nothing is bound to this key."); return; }
    const parts: (Node | string)[] = [];
    all.forEach((d, i) => {
      if (i) parts.push(h("span", "dot", "·"));
      parts.push(...this.caps(this.b(d.fn)), h("em", "", d.desc));
    });
    this.info.replaceChildren(...parts);
  }

  private visible(): Def[] {
    const q = this.query.trim().toLowerCase();
    return this.defs.filter((d) => {
      const b = this.b(d.fn);
      if (this.focusKey && b.key !== this.focusKey) return false;
      if (this.group !== "All" && d.group !== this.group) return false;
      if (this.filter === "Changed" && same(b, d.def)) return false;
      if (this.filter === "Conflicts" && !this.clashOf(d.fn)) return false;
      if (this.filter === "Unbound" && b.key) return false;
      if (q && !d.desc.toLowerCase().includes(q) && !cap(b.key).toLowerCase().includes(q) && !d.group.toLowerCase().includes(q)) return false;
      return true;
    });
  }

  private renderSide(): void {
    const search = h("input");
    search.placeholder = "Search actions or keys…";
    search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value; this.focusKey = null; this.renderList(); this.renderBoard(); });
    const groups = ["All", ...new Set(this.defs.map((d) => d.group))];
    const item = (label: string, count: string, on: boolean, icon: string, click: () => void, red = false) => {
      const e = h("div", "kgi" + (on ? " on" : ""));
      e.innerHTML = `<svg viewBox="0 0 24 24"><path d="${icon}"/></svg>`;
      e.append(h("span", "", label), h("small", red ? "r" : "", count));
      e.addEventListener("click", click);
      return e;
    };
    const nodes: HTMLElement[] = [search];
    for (const g of groups) nodes.push(item(g === "All" ? "All groups" : g, String(g === "All" ? this.defs.length : this.defs.filter((d) => d.group === g).length), this.group === g,
      GROUP_ICON[g] ?? "M4 6h16M4 12h16M4 18h16", () => { this.group = g; this.focusKey = null; this.renderAll(); }));
    const had = document.activeElement === this.side.querySelector("input");
    this.side.replaceChildren(...nodes);
    if (had) { search.focus(); search.setSelectionRange(search.value.length, search.value.length); }
  }

  /** The filter chips over the list. */
  private renderFilters(): void {
    const counts: Record<Filter, number> = { All: this.defs.length, Changed: this.defs.filter((d) => !same(this.b(d.fn), d.def)).length, Conflicts: this.conflicts().length, Unbound: this.defs.filter((d) => !this.b(d.fn).key).length };
    const chips = (["All", "Changed", "Conflicts", "Unbound"] as Filter[]).map((f) => {
      const c = h("div", "chip2" + (this.filter === f ? " on" : "") + (f === "Conflicts" && counts[f] ? " warn" : ""), f === "All" ? "All binds" : f === "Changed" ? "Not default" : f, h("small", "", String(counts[f])));
      c.addEventListener("click", () => { this.filter = f; this.focusKey = null; this.renderAll(); });
      return c;
    });
    this.filters.replaceChildren(...chips);
  }

  private renderList(): void {
    const scroll = this.list.scrollTop;
    const rows = this.visible();
    const nodes: HTMLElement[] = [];
    if (this.focusKey) {
      const clear = h("a", "", "show all");
      clear.addEventListener("click", () => { this.focusKey = null; this.renderAll(); });
      nodes.push(h("div", "gh", `Bound to ${cap(this.focusKey)} · `, clear));
    }
    let group = "";
    for (const d of rows) {
      if (!this.focusKey && d.group !== group) { group = d.group; nodes.push(h("div", "gh", group)); }
      nodes.push(this.row(d));
    }
    if (!rows.length) nodes.push(h("div", "none", this.filter === "Conflicts" ? "No conflicts: every key does one thing on each layer." : "Nothing matches."));
    this.list.replaceChildren(...nodes);
    this.list.scrollTop = scroll;
  }

  private row(d: Def): HTMLElement {
    const b = this.b(d.fn);
    const clash = this.clashOf(d.fn);
    const bind = h("div", "bind" + (this.waiting === d.fn ? " wait" : ""), ...(this.waiting === d.fn ? ["Press a key…"] : this.caps(b)));
    bind.title = "Click to rebind";
    bind.addEventListener("click", (e) => { e.stopPropagation(); this.waiting = this.waiting === d.fn ? null : d.fn; this.sel = d.fn; this.renderAll(); });
    const rst = h("div", "rst", "↺");
    rst.title = "Default: " + (d.def.key ? this.caps(d.def).map((n) => n.textContent).join("") : "Unbound");
    rst.addEventListener("click", (e) => { e.stopPropagation(); this.draft.set(d.fn, { ...d.def }); this.applied = false; this.renderAll(); });
    const desc = h("div", "d", d.desc);
    if (clash) {
      const fix = h("a", "", "unbind it");
      fix.addEventListener("click", (e) => { e.stopPropagation(); this.draft.set(clash.fn, { ...UNBOUND }); this.applied = false; this.renderAll(); });
      desc.append(h("small", "", `Also fires “${clash.desc}”`, fix));
    }
    const r = h("div", "br" + (clash ? " conf" : "") + (this.sel === d.fn ? " sel" : "") + (same(b, this.saved.get(d.fn)!) ? "" : " chg") + (same(b, d.def) ? "" : " mod"), desc, rst, bind);
    r.addEventListener("click", () => {
      this.sel = d.fn;
      if (b.key) { this.focusKey = null; for (const m of MODS) this.held[m] = b[m] === 1; this.hover = b.key; }
      this.renderLayer(); this.renderBoard(); this.renderInfo();
      for (const x of this.list.querySelectorAll(".br.sel")) x.classList.remove("sel");
      r.classList.add("sel");
      const k = [...this.board.querySelectorAll<HTMLElement>(".k")].filter((e) => e.firstChild?.textContent === cap(b.key));
      for (const e of k) e.classList.add("focus");
    });
    return r;
  }

  private renderFoot(): void {
    const n = this.changed().length;
    const st = h("div", "st" + (!n && this.applied ? " ok" : ""), ...(n ? [h("b", "", String(n)), ` unsaved change${n === 1 ? "" : "s"}`] : [this.applied ? "✓ Key bindings applied (mock: nothing is saved)" : "No unsaved changes"]));
    const defaults = h("div", "btn", "Restore defaults");
    defaults.addEventListener("click", () => { for (const d of this.defs) this.draft.set(d.fn, { ...d.def }); this.applied = false; this.renderAll(); });
    const cancel = h("div", "btn" + (n ? "" : " dis"), "Cancel");
    cancel.addEventListener("click", () => { for (const [k, v] of this.saved) this.draft.set(k, { ...v }); this.waiting = null; this.renderAll(); });
    const apply = h("div", "btn kmain" + (n ? "" : " dis"), "Apply");
    apply.addEventListener("click", () => { if (!this.changed().length) return; for (const [k, v] of this.draft) this.saved.set(k, { ...v }); this.applied = true; this.renderAll(); });
    this.foot.replaceChildren(st, defaults, cancel, apply);
  }
}
