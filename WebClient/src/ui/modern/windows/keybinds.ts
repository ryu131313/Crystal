// Ported from play/hudKeybinds.ts (KeybindsMock): the new UI's key binding editor, the one editor over src/game/keybinds.ts
// (the desktop's KeyBindSettings), playing the role of the classic src/ui/keyboardLayoutWindow.ts (KeyboardLayoutDialog).
// Kept from the mock: the full keyboard (main block, navigation cluster, numpad) lighting every bind on the shown
// modifier layer (none / ~ / Ctrl / Alt / Shift, combinable: the chips or the board's own modifier keys) with a short
// action name per key and conflicts in red; hovering a key lists everything on it; groups with counts, filters (All /
// Not default / Conflicts / Unbound), a debounced search, the bind rows with keycap chips, a reset arrow per row and a
// conflict note that can unbind the other bind. Changes from the mock:
//  - no draft / Apply / Cancel: like the desktop dialog every change is applied and saved at once (setKeyBind /
//    resetKeyBinds write "crystal.keybinds"); the window re-renders on onKeyBindsChange (other editors, e.g. Options),
//    only marked dirty while closed;
//  - rows grouped and ordered like the classic window (group, then description), incl. the web-only hero belt binds
//    HeroBelt1 / HeroBelt2 (+ Alt) of phase 2.9 in the Belt group;
//  - the Assign Rule toggle of the classic window: held modifiers become "must be held"; the others "must not be held"
//    (Strict, default) or "don't care" (Relaxed), for key presses and board clicks alike;
//  - waiting for a key: setCapturing(true) (every `matches` is false, so no game bind fires) and the keys come through
//    the input router's "window" layer (WindowManager.handleKey -> key() of the front window; no window keydown
//    listener). Esc cancels, Delete clears the bind (CheckNewInput), modifier keys alone are ignored, any other key is
//    taken with the held modifiers; a click on the board takes that key (so Esc / Delete themselves can be bound there).
//    The wait ends on every close path (closed()), on a click outside the window, when another window comes in front,
//    and on dispose;
//  - "Reset all" asks first (popup), then resetKeyBinds();
//  - no infinite border pulse on ~100 keys while waiting (Performance.md T3-9): a static accent border, and one
//    opacity '.halo' on the waiting chip; the board is built once and updated in place (classes / setText); clicks and
//    hovers are delegated; the window fits a 1024 px layer (narrow: smaller keys, narrower side bar).

import {
  KeybindOptions, defaultKeyBind, isModifierKeyName, keyBinds, keyLabel, keyName, onKeyBindsChange, overlapping,
  resetKeyBinds, setCapturing, setKeyBind, tildeHeld, type KeyBind, type KeybindOption, type Require,
} from "../../../game/keybinds.ts";
import { h, injectCss, setText } from "../core/dom.ts";
import { plural } from "../core/format.ts";
import { svg, type IconName } from "../core/icons.ts";
import { button, searchBox, segmented } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

type Mod = "tilde" | "ctrl" | "alt" | "shift";
/** Layer chip order (the mock's). */
const MODS: readonly Mod[] = ["tilde", "ctrl", "alt", "shift"];
/** Keycap order of a bind (KeyBindSettings.GetKey: Alt + Ctrl + Shift + ~ + Key). */
const CAP_MODS: readonly Mod[] = ["alt", "ctrl", "shift", "tilde"];
const MOD_LABEL: Record<Mod, string> = { shift: "Shift", ctrl: "Ctrl", alt: "Alt", tilde: "~" };
type Held = Record<Mod, boolean>;

const reqOf = (b: KeyBind, m: Mod): Require =>
  m === "alt" ? b.requireAlt : m === "shift" ? b.requireShift : m === "ctrl" ? b.requireCtrl : b.requireTilde;
/** Whether a bind fires with exactly these modifiers held. */
const firesOn = (b: KeyBind, held: Held): boolean => !!b.key && MODS.every((m) => { const r = reqOf(b, m); return r === 2 || (r === 1) === held[m]; });
const sameBind = (a: KeyBind, b: KeyBind): boolean => a.key === b.key && MODS.every((m) => reqOf(a, m) === reqOf(b, m));

/** Keys name -> keycap text. */
const CAP: Record<string, string> = {
  Escape: "Esc", PrintScreen: "PrtSc", Scroll: "ScrLk", Pause: "Pause", Oem8: "`", OemMinus: "-", Oemplus: "=", Back: "Backspace",
  Tab: "Tab", OemOpenBrackets: "[", Oem6: "]", Oem5: "\\", Capital: "Caps", Oem1: ";", Oem7: "'", Return: "Enter", ShiftKey: "Shift",
  Oemcomma: ",", OemPeriod: ".", OemQuestion: "/", ControlKey: "Ctrl", LWin: "Win", Menu: "Alt", Space: "Space", RWin: "Win", Apps: "Menu",
  Insert: "Ins", Home: "Home", PageUp: "PgUp", Delete: "Del", End: "End", Next: "PgDn", Up: "↑", Down: "↓", Left: "←", Right: "→",
  NumLock: "Num", Divide: "/", Multiply: "*", Subtract: "-", Add: "+", Decimal: ".", OemBackslash: "\\",
};
const cap = (k: string): string => CAP[k] ?? k.replace(/^D(\d)$/, "$1").replace(/^NumPad(\d)$/, "Num $1");
/** A key on the board: Keys name, width in units (default 1), modifier. "" = a gap of that width. */
type K = [string, number?, Mod?];
const F = (a: number, b: number): K[] => Array.from({ length: b - a + 1 }, (_, i) => [`F${a + i}`] as K);
const L = (s: string): K[] => [...s].map((c) => [/\d/.test(c) ? `D${c}` : c] as K);
const MAIN: K[][] = [
  [["Escape"], ["", 1], ...F(1, 4), ["", .5], ...F(5, 8), ["", .5], ...F(9, 12)],
  [["Oem8", 1, "tilde"], ...L("1234567890"), ["OemMinus"], ["Oemplus"], ["Back", 2]],
  [["Tab", 1.5], ...L("QWERTYUIOP"), ["OemOpenBrackets"], ["Oem6"], ["Oem5", 1.5]],
  [["Capital", 1.75], ...L("ASDFGHJKL"), ["Oem1"], ["Oem7"], ["Return", 2.25]],
  [["ShiftKey", 2.25, "shift"], ...L("ZXCVBNM"), ["Oemcomma"], ["OemPeriod"], ["OemQuestion"], ["ShiftKey", 2.75, "shift"]],
  [["ControlKey", 1.25, "ctrl"], ["LWin", 1.25], ["Menu", 1.25, "alt"], ["Space", 6.25], ["Menu", 1.25, "alt"], ["RWin", 1.25], ["Apps", 1.25], ["ControlKey", 1.25, "ctrl"]],
];
const NAV: K[][] = [
  [["PrintScreen"], ["Scroll"], ["Pause"]], [["Insert"], ["Home"], ["PageUp"]], [["Delete"], ["End"], ["Next"]], [], [["", 1], ["Up"], ["", 1]], [["Left"], ["Down"], ["Right"]],
];
const PAD: K[][] = [
  [], [["NumLock"], ["Divide"], ["Multiply"], ["Subtract"]], [["NumPad7"], ["NumPad8"], ["NumPad9"], ["Add"]], [["NumPad4"], ["NumPad5"], ["NumPad6"], ["", 1]],
  [["NumPad1"], ["NumPad2"], ["NumPad3"], ["Return"]], [["NumPad0", 2], ["Decimal"], ["", 1]],
];
/** Keycap text on the board (number keys show the bare digit there). */
const boardCap = (k: string): string => k.startsWith("NumPad") ? k.slice(6) : cap(k);

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
/** Shared icons per bind group (core/icons.ts names). */
const GROUP_ICON: Record<string, IconName> = {
  All: "keyboard", Belt: "pouch", Combat: "sword", Dialogs: "square", General: "gear", Skillbar: "spark", Toggle: "repeat",
};

type Filter = "All" | "Changed" | "Conflicts" | "Unbound";
const FILTERS: readonly [Filter, string, string][] = [
  ["All", "All binds", "Every bind"],
  ["Changed", "Not default", "Binds that differ from the defaults"],
  ["Conflicts", "Conflicts", "Binds that fire together with another bind on the same key press"],
  ["Unbound", "Unbound", "Actions without a key"],
];

const CSS = `
.hudm .kbwin { width: 1000px; }
.hudm .kbwin .panel { height: 640px; display: flex; flex-direction: column; }
/* Layer bar */
.hudm .kbwin .layer { display: flex; align-items: center; gap: 6px; padding: 8px 14px 0; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .kbwin .chip2 { height: 22px; padding: 0 10px; display: inline-flex; align-items: center; cursor: pointer; border-radius: 11px; font: inherit; font-size: 11px; font-weight: 700; letter-spacing: .04em; text-transform: none;
  color: var(--tx); border: 1px solid var(--fr); background: var(--pb); white-space: nowrap; }
.hudm .kbwin .chip2:hover { border-color: var(--ac); }
.hudm .kbwin .chip2.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .kbwin .layer .sp { flex: 1; }
.hudm .kbwin .layer .stat b { color: var(--ac); font-size: 12px; letter-spacing: 0; } .hudm .kbwin .layer .stat b.r { color: var(--bad-soft); }
/* Keyboard */
.hudm .kbwin .board { --u: 36px; --g: 3px; display: flex; gap: 14px; justify-content: center; padding: 10px 14px 8px; position: relative; flex: none; }
.hudm .kbwin .blk { display: flex; flex-direction: column; gap: var(--g); }
.hudm .kbwin .krow { display: flex; gap: var(--g); height: var(--u); }
.hudm .kbwin .krow:first-child { margin-bottom: 6px; }
.hudm .kbwin .k { position: relative; width: calc(var(--u) * var(--w, 1) + var(--g) * (var(--w, 1) - 1)); height: var(--u); flex: none; cursor: pointer; user-select: none;
  border-radius: calc(var(--rad) * .6); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 80%, #000), color-mix(in srgb, var(--pb) 80%, #000));
  box-shadow: 0 2px 0 rgba(0,0,0,.7), inset 0 1px 0 color-mix(in srgb, var(--frl) 15%, transparent); transition: border-color .15s, background .15s; }
.hudm .kbwin .k.gap { visibility: hidden; pointer-events: none; }
.hudm .kbwin .k > b { position: absolute; left: 4px; top: 2px; font: 600 10px ui-monospace, Consolas, monospace; color: var(--dim); }
.hudm .kbwin .k > span { position: absolute; left: 2px; right: 2px; bottom: 2px; font-size: 8px; line-height: 1.05; font-weight: 700; color: #fff; text-align: center; overflow: hidden; overflow-wrap: anywhere;
  display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; text-shadow: 0 1px 2px #000; }
.hudm .kbwin .k > i { position: absolute; right: 3px; top: 2px; font-style: normal; font-size: 8px; font-weight: 800; color: var(--ac); }
.hudm .kbwin .k.on { border-color: color-mix(in srgb, var(--ac) 70%, transparent); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 30%, var(--pb)), color-mix(in srgb, var(--ac) 12%, var(--pb)));
  box-shadow: 0 2px 0 rgba(0,0,0,.7), 0 0 calc(7px * var(--gl)) color-mix(in srgb, var(--ac) 45%, transparent); }
.hudm .kbwin .k.on > b { color: #fff; }
.hudm .kbwin .k.clash { border-color: var(--bad); background: linear-gradient(180deg, #5a1a18, #2a0c0c); box-shadow: 0 2px 0 rgba(0,0,0,.7), 0 0 8px rgba(255,90,74,.55); }
.hudm .kbwin .k.modk { border-color: color-mix(in srgb, var(--fr) 90%, transparent); }
.hudm .kbwin .k.modk > b { color: var(--tx); }
.hudm .kbwin .k.held { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 45%, var(--pb)); box-shadow: 0 0 0 1px var(--ac), 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .kbwin .k.held > b { color: #fff; }
.hudm .kbwin .k.focus { outline: 2px solid #fff; outline-offset: 1px; }
.hudm .kbwin .k:hover { border-color: #fff; }
.hudm .kbwin.waiting .k:not(.modk):not(.on):not(.clash) { border-color: color-mix(in srgb, var(--ac) 45%, var(--fr)); }
/* Info strip */
.hudm .kbwin .info { margin: 0 14px; min-height: 30px; flex: none; display: flex; align-items: center; flex-wrap: wrap; gap: 6px; padding: 4px 10px; border-radius: var(--rad); font-size: 11px; color: var(--dim);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .kbwin .info .kc { margin-right: 2px; }
.hudm .kbwin .info em { font-style: normal; color: var(--tx); } .hudm .kbwin .info .dot { color: var(--fr); }
.hudm .kbwin .info.wait { color: #fff; border-color: var(--ac); box-shadow: 0 0 10px var(--glow); }
/* Lower */
.hudm .kbwin .low { flex: 1; min-height: 0; display: flex; margin-top: 8px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .kbwin .side { width: 190px; flex: none; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; overflow-y: auto; scrollbar-width: thin; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .kbwin .side .ui-search { margin-bottom: 4px; }
.hudm .kbwin .kgi { display: flex; align-items: center; gap: 8px; padding: 3px 8px; flex: none; border-radius: var(--rad); cursor: pointer; font: inherit; font-size: 12px; text-align: left; color: var(--tx); border: 1px solid transparent; background: none; }
.hudm .kbwin .kgi:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .kbwin .kgi.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .kbwin .kgi svg { width: 15px; height: 15px; opacity: .8; }
.hudm .kbwin .kgi span { flex: 1; } .hudm .kbwin .kgi small { font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; } .hudm .kbwin .kgi small.r { color: var(--bad-soft); }
.hudm .kbwin .lcol { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .kbwin .filt { display: flex; gap: 6px; padding: 8px 10px 2px; flex-wrap: wrap; }
.hudm .kbwin .filt small { margin-left: 6px; font-size: 10px; color: var(--dim); } .hudm .kbwin .filt .on small { color: #fff; }
.hudm .kbwin .chip2.warn { border-color: var(--bad); } .hudm .kbwin .chip2.warn small { color: var(--bad-soft); }
.hudm .kbwin .list { flex: 1; min-height: 0; overflow-y: auto; padding: 6px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .kbwin .gh { position: sticky; top: -6px; z-index: 2; padding: 7px 4px 4px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac);
  background: color-mix(in srgb, var(--pb) 55%, #000); }
.hudm .kbwin .gh a { color: var(--ac); cursor: pointer; text-decoration: underline; letter-spacing: .04em; text-transform: none; }
.hudm .kbwin .br { display: flex; align-items: center; gap: 10px; padding: 4px 8px; min-height: 30px; border-radius: var(--rad); font-size: 12px; border: 1px solid transparent; cursor: pointer; }
.hudm .kbwin .br:nth-of-type(even) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .kbwin .br:hover { border-color: color-mix(in srgb, var(--fr) 80%, transparent); }
.hudm .kbwin .br.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .kbwin .br .d { flex: 1; min-width: 0; } .hudm .kbwin .br .d small { display: block; font-size: 10px; color: var(--bad-soft); }
.hudm .kbwin .br .d small a { color: var(--ac); cursor: pointer; text-decoration: underline; margin-left: 4px; }
.hudm .kbwin .br.mod .d::before { content: "●"; color: var(--ac); font-size: 9px; margin-right: 6px; vertical-align: 1px; }
.hudm .kbwin .rst { width: 20px; text-align: center; cursor: pointer; color: var(--dim); visibility: hidden; }
.hudm .kbwin .br.mod .rst { visibility: visible; } .hudm .kbwin .rst:hover { color: var(--ac); }
.hudm .kbwin .bind { position: relative; min-width: 150px; display: flex; justify-content: flex-end; align-items: center; gap: 3px; cursor: pointer; padding: 2px 4px; border-radius: calc(var(--rad) * .7); border: 1px dashed transparent; }
.hudm .kbwin .bind:hover { border-color: var(--fr); }
.hudm .kbwin .bind.wait { border: 1px solid var(--ac); color: #fff; font-size: 11px; }
.hudm .kbwin .bind .un { font-size: 11px; font-style: italic; color: var(--dim); }
.hudm .kbwin .plus { color: var(--dim); font-size: 10px; }
.hudm .kbwin .kc { display: inline-flex; align-items: center; justify-content: center; min-width: 22px; height: 20px; padding: 0 6px; font: 600 10px ui-monospace, Consolas, monospace;
  color: #fff; border-radius: 4px; border: 1px solid var(--fr); border-bottom-width: 2px; background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .kbwin .kc.m { color: var(--ac); }
.hudm .kbwin .br.conf .kc:not(.m) { border-color: var(--bad); color: #ffb0a6; }
.hudm .kbwin .none { padding: 30px; text-align: center; color: var(--dim); font-size: 12px; }
/* Footer */
.hudm .kbwin .foot { display: flex; align-items: center; gap: 8px; padding: 8px 14px; flex: none; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: color-mix(in srgb, var(--pb) 50%, #000);
  border-radius: 0 0 calc(var(--rad) - 1px) calc(var(--rad) - 1px); }
.hudm .kbwin .foot .st { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .kbwin .foot .st b { color: var(--ac); } .hudm .kbwin .foot .st.ok { color: var(--ok); }
/* 1024 px layer */
.hudm.narrow .kbwin { width: 820px; }
.hudm.narrow .kbwin .panel { height: 600px; }
.hudm.narrow .kbwin .board { --u: 30px; gap: 10px; padding: 8px 10px 6px; }
.hudm.narrow .kbwin .k > b { font-size: 9px; left: 3px; }
.hudm.narrow .kbwin .k > span { font-size: 7px; }
.hudm.narrow .kbwin .side { width: 150px; padding: 8px 6px; }
.hudm.narrow .kbwin .bind { min-width: 120px; }
.hudm.narrow .kbwin .layer { gap: 4px; flex-wrap: wrap; }
`;

/** One key on the board (built once, updated in place). */
interface KeyEl { readonly el: HTMLElement; readonly name: string; readonly mod: Mod | null; readonly label: HTMLElement; readonly count: HTMLElement }

export class KeybindsWindow extends UiWindow {
  /** Every bind, ordered like KeyboardLayoutDialog.UpdateText (group, then description). The live KeyBind objects. */
  private readonly binds: readonly KeyBind[];
  private readonly groups: readonly string[];
  private held: Held = { tilde: false, ctrl: false, alt: false, shift: false };
  private group = "All";
  private filter: Filter = "All";
  private query = "";
  /** KeyboardLayoutDialog.WaitingForBind. */
  private waiting: KeybindOption | null = null;
  /** KeyboardLayoutDialog.Enforce (Assign Rule: Strict). */
  private enforce = true;
  private sel: KeybindOption | null = null;
  /** A board key clicked: the list shows only its binds. */
  private focusKey: string | null = null;
  /** The key of the selected row (outlined on the board). */
  private mark: string | null = null;
  private hover: string | null = null;
  /** A key just assigned: its auto-repeats are swallowed until it is pressed again, so they do not fire the new bind. */
  private swallow: string | null = null;
  /** A one-line note in the footer (after a reset), cleared by the next change. */
  private note = "";
  /** Per render: binds that fire together with another (keybinds.ts overlapping). */
  private conflicts = new Map<KeybindOption, KeyBind[]>();

  private readonly closeX: HTMLElement | null;
  private readonly layerChips: { none: HTMLElement; mods: Record<Mod, HTMLElement>; lit: HTMLElement; nc: HTMLElement; ncText: HTMLElement };
  private readonly board = h("div", "board");
  private readonly keys: KeyEl[] = [];
  private readonly info = h("div", "info");
  private readonly side = h("div", "side");
  private readonly groupEls = new Map<string, { el: HTMLElement; count: HTMLElement }>();
  private readonly filters = h("div", "filt");
  private readonly filterEls = new Map<Filter, { el: HTMLElement; count: HTMLElement }>();
  private readonly list = h("div", "list");
  private readonly status = h("div", "st");
  private infoKey = "\u0000";

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "kbwin", Math.max(0, Math.round((ui.rt.layout.width - (ui.rt.layout.narrow ? 820 : 1000)) / 2)), 24);
    injectCss("win-keybinds", CSS);
    this.binds = [...keyBinds()].sort((a, b) => cmp(a.group, b.group) || cmp(a.description, b.description));
    this.groups = [...new Set(this.binds.map((b) => b.group))];

    // Title + layer bar
    const title = this.titleBar("KEY BINDINGS", "Close");
    this.closeX = title.querySelector<HTMLElement>(".x");
    const chip = (text: string, tip: string) => { const c = h("button", "chip2", text); c.type = "button"; c.title = tip; return c; };
    const none = chip("No modifier", "Show the binds that fire with no modifier held");
    const mods = {} as Record<Mod, HTMLElement>;
    for (const m of MODS) mods[m] = chip(m === "tilde" ? "~ held" : MOD_LABEL[m], `Show the binds that fire with ${MOD_LABEL[m]} held (combinable)`);
    const lit = h("b"), nc = h("b"), ncText = h("span");
    this.layerChips = { none, mods, lit, nc, ncText };
    const layer = h("div", "layer", "Layer", none, ...MODS.map((m) => mods[m]), h("span", "sp"), h("span", "stat", lit, " on this layer · ", nc, ncText));
    this.listen(none, "click", () => { this.held = { tilde: false, ctrl: false, alt: false, shift: false }; this.invalidate(); });
    for (const m of MODS) this.listen(mods[m], "click", () => { this.held[m] = !this.held[m]; this.invalidate(); });

    // Keyboard (built once; clicks and hovers delegated)
    const block = (rows: K[][]) => {
      const blk = h("div", "blk");
      for (const row of rows) {
        const r = h("div", "krow");
        for (const [name, w = 1, mod = null] of row) r.append(this.keyEl(name, w, mod));
        blk.append(r);
      }
      return blk;
    };
    this.board.append(block(MAIN), block(NAV), block(PAD));
    this.listen(this.board, "click", (e) => {
      const k = this.keyOf(e.target);
      if (k) this.boardClick(k);
    });
    this.listen(this.board, "pointerover", (e) => {
      const k = this.keyOf(e.target);
      const name = k && !k.mod ? k.name : null;
      if (name !== this.hover) { this.hover = name; this.renderInfo(); }
    });
    this.listen(this.board, "pointerleave", () => { if (this.hover !== null) { this.hover = null; this.renderInfo(); } });

    // Side: search + groups
    const search = searchBox("Search actions or keys", (q) => { this.query = q.toLowerCase(); this.focusKey = null; this.invalidate(); }, { signal: this.signal });
    this.side.append(search.el);
    for (const g of ["All", ...this.groups]) {
      const count = h("small", "", String(g === "All" ? this.binds.length : this.binds.filter((b) => b.group === g).length));
      const el = h("button", "kgi", svg(GROUP_ICON[g] ?? "menu"), h("span", "", g === "All" ? "All groups" : g), count);
      el.type = "button";
      el.dataset.g = g;
      this.groupEls.set(g, { el, count });
      this.side.append(el);
    }
    this.listen(this.side, "click", (e) => {
      const g = (e.target as Element).closest<HTMLElement>(".kgi")?.dataset.g;
      if (g === undefined) return;
      this.group = g; this.focusKey = null; this.invalidate();
    });

    // Filters + list
    for (const [f, label, tip] of FILTERS) {
      const count = h("small");
      const el = chip(label, tip);
      el.append(count);
      this.listen(el, "click", () => { this.filter = f; this.focusKey = null; this.invalidate(); });
      this.filterEls.set(f, { el, count });
      this.filters.append(el);
    }
    this.listen(this.list, "click", (e) => this.listClick(e));

    // Footer: Assign Rule (KeyboardLayoutDialog.EnforceButton), status, Reset all (ResetButton)
    const rule = segmented([
      { value: true, label: "Strict", title: "Modifiers you do not hold must stay up for the bind to fire" },
      { value: false, label: "Relaxed", title: "Modifiers you do not hold are ignored (the bind fires with or without them)" },
    ], this.enforce, (v) => { this.enforce = v; this.infoKey = "\u0000"; this.renderInfo(); });
    const resetAll = button("Reset all", () => void this.resetAll(), { cls: "warn", icon: "refresh", title: "Put every key binding back to its default" });
    const foot = h("div", "foot", h("span", "lbl", "Assign rule"), rule.el, this.status, resetAll.el);

    this.el.append(h("div", "panel", title, layer, this.board, this.info, h("div", "low", this.side, h("div", "lcol", this.filters, this.list)), foot));

    // Another editor (Options, the classic dialog) or this one changed a bind: re-render when shown, else on open.
    const off = onKeyBindsChange(() => this.invalidate());
    this.signal.addEventListener("abort", off, { once: true });
    // The wait ends on a click outside the window or when another window comes in front (its keys would go there).
    this.listen(document, "pointerdown", (e) => {
      if (this.waiting !== null && !this.el.contains(e.target as Node)) this.cancelWait();
    }, { capture: true });
    const offWm = this.wm.changed.on(() => { if (this.waiting !== null && !this.isFront) this.cancelWait(); });
    this.signal.addEventListener("abort", offWm, { once: true });
  }

  // — Lifecycle —

  protected override closed(): void {
    this.cancelWait();
    this.hover = null;
    this.swallow = null;
  }

  /** Keys while this is the front window (the router's "window" layer). While waiting every key is taken here. */
  override key(e: KeyboardEvent): boolean {
    const name = keyName(e);
    if (this.waiting === null) {
      if (this.swallow !== null) {
        if (e.repeat && name === this.swallow) return true;
        this.swallow = null;
      }
      return false;
    }
    // KeyboardLayoutDialog.CheckNewInput
    if (isModifierKeyName(name)) return true;
    const fn = this.waiting;
    if (name === "Escape") { this.cancelWait(); return true; }
    if (name === "Delete") { this.cancelWait(); this.apply(fn, ""); return true; }
    this.cancelWait();
    this.swallow = name;
    this.apply(fn, name, { alt: e.altKey, shift: e.shiftKey, ctrl: e.ctrlKey, tilde: tildeHeld() });
    return true;
  }

  // — Actions —

  private startWait(fn: KeybindOption): void {
    if (this.waiting === fn) { this.cancelWait(); return; }
    this.waiting = fn;
    this.sel = fn;
    this.note = "";
    setCapturing(true);
    // A focused field / button in the window would keep the keys (typingField) or click on Enter / Space.
    const a = document.activeElement;
    if (a instanceof HTMLElement && this.el.contains(a)) a.blur();
    this.invalidate();
  }

  private cancelWait(): void {
    if (this.waiting === null) return;
    this.waiting = null;
    setCapturing(false);
    this.invalidate();
  }

  /** Assigns `key` (with these modifiers held) under the Assign Rule; "" clears the bind (Keys.None). */
  private apply(fn: KeybindOption, key: string, held?: Held): void {
    this.sel = fn;
    this.note = "";
    if (!key || !held) { setKeyBind(fn, "", 2, 2, 2, 2); return; }
    const r = (on: boolean): Require => (on ? 1 : this.enforce ? 0 : 2);
    this.mark = key;
    this.held = { ...held };
    setKeyBind(fn, key, r(held.alt), r(held.shift), r(held.ctrl), r(held.tilde));
  }

  private boardClick(k: KeyEl): void {
    if (k.mod) { this.held[k.mod] = !this.held[k.mod]; this.invalidate(); return; }
    if (this.waiting !== null) {
      const fn = this.waiting;
      this.cancelWait();
      this.apply(fn, k.name, { ...this.held });
      return;
    }
    this.focusKey = this.focusKey === k.name ? null : k.name;
    if (this.focusKey) { this.group = "All"; this.filter = "All"; }
    this.invalidate();
  }

  private listClick(e: MouseEvent): void {
    const t = e.target as Element;
    const actEl = t.closest<HTMLElement>("[data-act]");
    const act = actEl?.dataset.act;
    if (act === "all") { this.focusKey = null; this.invalidate(); return; }
    const row = t.closest<HTMLElement>(".br");
    if (!row) return;
    const fn = Number(row.dataset.fn) as KeybindOption;
    const b = this.binds.find((x) => x.function === fn);
    if (!b) return;
    if (act === "bind") { this.startWait(fn); return; }
    if (act === "rst") {
      const d = defaultKeyBind(fn);
      if (!d) return;
      this.cancelWait();
      this.sel = fn; this.note = "";
      setKeyBind(fn, d.key, d.requireAlt, d.requireShift, d.requireCtrl, d.requireTilde);
      return;
    }
    if (act === "unbind") {
      const other = Number(actEl!.dataset.other) as KeybindOption;
      this.cancelWait();
      this.note = "";
      setKeyBind(other, "", 2, 2, 2, 2);
      return;
    }
    // A row click: select it, show its layer and outline its key.
    this.sel = fn;
    if (b.key) { for (const m of MODS) this.held[m] = reqOf(b, m) === 1; this.mark = b.key; }
    this.invalidate();
  }

  /** ResetButton.Click, after a confirm. */
  private async resetAll(): Promise<void> {
    this.cancelWait();
    const ok = await this.ui.rt.popups.ask("Put every key binding back to its default? Your changes are lost.", { title: "Reset key bindings", kind: "warning" });
    if (!ok || this.signal.aborted) return;
    resetKeyBinds();
    this.note = "Keyboard settings have been reset back to default."; // KeyboardSettingsResetDefault
    this.invalidate();
  }

  // — Render —

  protected override render(): void {
    this.conflicts.clear();
    for (const b of this.binds) {
      if (!b.key) continue;
      const o = overlapping(b.function);
      if (o.length) this.conflicts.set(b.function, o);
    }
    this.el.classList.toggle("waiting", this.waiting !== null);
    if (this.closeX) {
      const k = keyLabel(KeybindOptions.Keybind);
      this.closeX.title = `Close${k ? ` (${k})` : ""} · Esc closes the front window, Shift+Esc all`;
    }
    this.renderLayer();
    this.renderBoard();
    this.infoKey = "\u0000";
    this.renderInfo();
    this.renderSide();
    this.renderFilters();
    this.renderList();
    this.renderFoot();
  }

  private renderLayer(): void {
    const c = this.layerChips;
    c.none.classList.toggle("on", !MODS.some((m) => this.held[m]));
    for (const m of MODS) c.mods[m].classList.toggle("on", this.held[m]);
    let lit = 0;
    for (const b of this.binds) if (firesOn(b, this.held)) lit++;
    const n = this.conflicts.size;
    setText(c.lit, String(lit));
    setText(c.nc, String(n));
    c.nc.classList.toggle("r", n > 0);
    setText(c.ncText, ` ${plural(n, "conflict")}`);
  }

  private keyEl(name: string, w: number, mod: Mod | null): HTMLElement {
    const el = h("div", "k");
    el.style.setProperty("--w", String(w));
    if (!name) { el.classList.add("gap"); return el; }
    const label = h("span"), count = h("i");
    label.hidden = count.hidden = true;
    el.append(h("b", "", boardCap(name)), label, count);
    if (mod) { el.classList.add("modk"); el.title = `Show the ${MOD_LABEL[mod]} layer`; }
    const k: KeyEl = { el, name, mod, label, count };
    this.keys.push(k);
    return el;
  }

  private keyOf(target: EventTarget | null): KeyEl | null {
    const el = target instanceof Element ? target.closest<HTMLElement>(".k") : null;
    if (!el) return null;
    for (const k of this.keys) if (k.el === el) return k;
    return null;
  }

  private renderBoard(): void {
    const onLayer = new Map<string, KeyBind[]>();
    for (const b of this.binds) {
      if (!firesOn(b, this.held)) continue;
      let l = onLayer.get(b.key);
      if (!l) onLayer.set(b.key, l = []);
      l.push(b);
    }
    for (const k of this.keys) {
      if (k.mod) { k.el.classList.toggle("held", this.held[k.mod]); continue; }
      const binds = onLayer.get(k.name) ?? [];
      const on = binds.length > 0;
      k.el.classList.toggle("on", on);
      k.el.classList.toggle("clash", binds.some((b) => this.conflicts.has(b.function)));
      k.el.classList.toggle("focus", k.name === this.focusKey || k.name === this.mark);
      k.label.hidden = !on;
      setText(k.label, on ? binds.map((b) => short(b.description)).join(" · ") : "");
      k.count.hidden = binds.length < 2;
      setText(k.count, binds.length > 1 ? "×" + binds.length : "");
    }
  }

  /** A bind as keycap chips. */
  private caps(b: KeyBind): HTMLElement[] {
    if (!b.key) return [h("span", "un", "Unbound")];
    const out: HTMLElement[] = [];
    for (const m of CAP_MODS) if (reqOf(b, m) === 1) out.push(h("span", "kc m", MOD_LABEL[m]), h("span", "plus", "+"));
    out.push(h("span", "kc", cap(b.key)));
    return out;
  }

  private renderInfo(): void {
    const key = this.waiting !== null ? "\u0001" : this.hover ?? this.focusKey ?? this.mark ?? "";
    if (key === this.infoKey) return;
    this.infoKey = key;
    this.info.classList.toggle("wait", this.waiting !== null);
    if (this.waiting !== null) {
      const b = this.binds.find((x) => x.function === this.waiting);
      this.info.replaceChildren(`Rebinding “${b?.description ?? ""}”: press a key with its modifiers, or click a key on the board (uses the shown layer). `
        + `Esc cancels, Delete clears the bind. Assign rule: ${this.enforce ? "Strict" : "Relaxed"}.`);
      return;
    }
    if (!key) { this.info.replaceChildren("Hover a key to see everything bound to it. Click a key to list its binds, click a bind's keys to change them."); return; }
    const all = this.binds.filter((b) => b.key === key);
    if (!all.length) { this.info.replaceChildren(h("span", "kc", cap(key)), " Nothing is bound to this key."); return; }
    const parts: (Node | string)[] = [];
    all.forEach((b, i) => {
      if (i) parts.push(h("span", "dot", "·"));
      parts.push(...this.caps(b), h("em", "", b.description));
    });
    this.info.replaceChildren(...parts);
  }

  private renderSide(): void {
    for (const [g, x] of this.groupEls) {
      x.el.classList.toggle("on", this.group === g);
      let n = 0;
      for (const b of this.binds) if ((g === "All" || b.group === g) && this.conflicts.has(b.function)) n++;
      x.count.classList.toggle("r", n > 0);
      x.el.title = n ? `${n} ${plural(n, "bind")} in conflict` : "";
    }
  }

  private isChanged(b: KeyBind): boolean {
    const d = defaultKeyBind(b.function);
    return !!d && !sameBind(b, d);
  }

  private renderFilters(): void {
    const counts: Record<Filter, number> = { All: this.binds.length, Changed: 0, Conflicts: this.conflicts.size, Unbound: 0 };
    for (const b of this.binds) { if (this.isChanged(b)) counts.Changed++; if (!b.key) counts.Unbound++; }
    for (const [f, x] of this.filterEls) {
      x.el.classList.toggle("on", this.filter === f);
      x.el.classList.toggle("warn", f === "Conflicts" && counts[f] > 0);
      setText(x.count, String(counts[f]));
    }
  }

  private listed(b: KeyBind): boolean {
    if (this.focusKey) return b.key === this.focusKey;
    if (this.group !== "All" && b.group !== this.group) return false;
    if (this.filter === "Changed" && !this.isChanged(b)) return false;
    if (this.filter === "Conflicts" && !this.conflicts.has(b.function)) return false;
    if (this.filter === "Unbound" && b.key) return false;
    const q = this.query;
    if (q && !b.description.toLowerCase().includes(q) && !(b.key && cap(b.key).toLowerCase().includes(q)) && !b.group.toLowerCase().includes(q)
      && !keyLabel(b.function).toLowerCase().includes(q)) return false;
    return true;
  }

  private renderList(): void {
    const scroll = this.list.scrollTop;
    const nodes: HTMLElement[] = [];
    if (this.focusKey) {
      const all = h("a", "", "show all");
      all.dataset.act = "all";
      nodes.push(h("div", "gh", `Bound to ${cap(this.focusKey)} · `, all));
    }
    let group = "", count = 0;
    for (const b of this.binds) {
      if (!this.listed(b)) continue;
      if (!this.focusKey && b.group !== group) { group = b.group; nodes.push(h("div", "gh", group)); }
      nodes.push(this.row(b));
      count++;
    }
    if (!count) nodes.push(h("div", "none", this.filter === "Conflicts" && !this.focusKey ? "No conflicts: every key does one thing on each layer." : "Nothing matches."));
    this.list.replaceChildren(...nodes);
    this.list.scrollTop = scroll;
  }

  private row(b: KeyBind): HTMLElement {
    const waiting = this.waiting === b.function;
    const clash = this.conflicts.get(b.function);
    const bind = h("div", "bind" + (waiting ? " wait" : ""), ...(waiting ? [h("span", "halo"), "Press a key…"] : this.caps(b)));
    bind.dataset.act = "bind";
    bind.title = waiting ? "Click again to cancel" : "Click, then press the new key (Delete clears it)";
    const d = defaultKeyBind(b.function);
    const rst = h("div", "rst", "↺");
    rst.dataset.act = "rst";
    rst.title = "Back to the default: " + (d?.key ? keyLabel(b.function, true) : "Unbound");
    const desc = h("div", "d", b.description);
    if (clash?.length) {
      const other = clash[0]!;
      const fix = h("a", "", "unbind it");
      fix.dataset.act = "unbind";
      fix.dataset.other = String(other.function);
      desc.append(h("small", "", `Also fires “${other.description}”${clash.length > 1 ? ` (+${clash.length - 1})` : ""}`, fix));
    }
    const r = h("div", "br" + (clash ? " conf" : "") + (this.sel === b.function ? " sel" : "") + (this.isChanged(b) ? " mod" : ""), desc, rst, bind);
    r.dataset.fn = String(b.function);
    return r;
  }

  private renderFoot(): void {
    let changed = 0;
    for (const b of this.binds) if (this.isChanged(b)) changed++;
    this.status.classList.toggle("ok", !!this.note);
    if (this.note) { this.status.replaceChildren(this.note); return; }
    if (this.waiting !== null) { this.status.replaceChildren("Waiting for a key… (Esc cancels)"); return; }
    this.status.replaceChildren(...(changed ? [h("b", "", String(changed)), ` ${plural(changed, "bind")} differ from the defaults · changes are saved at once`]
      : ["Default key bindings · changes are saved at once"]));
  }
}

function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

// Hotkey: KeybindOptions.Keybind (U), the MENU_WINDOWS default for "keybinds".
registerWindow("keybinds", (host) => new KeybindsWindow(host));
