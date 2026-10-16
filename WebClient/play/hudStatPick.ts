// Stat plates for the scene lab's HUD mock (2026-10-16): the six small plates above the HP / MP bars each track a stat
// the player picks. Click a plate: a list of every stat (the Character window's Stats panel, plus the live bag count,
// bag weight and gold) opens above it, grouped like the Stats panel and showing each stat's value; click one and the
// plate tracks it. A stat already on another plate swaps places with this plate's. Esc or a click elsewhere closes the
// list. The picks are remembered per browser (localStorage; the defaults come back when storage is blocked).

import { h, injectCss } from "./hudWindow.ts";
import { STATS } from "./hudCharacter.ts";

/** A stat a plate can track: `live` ones are filled in by the HUD every frame, the rest show the Stats panel's value. */
export interface PlateStat { key: string; name: string; short: string; group: string; value: string; live?: boolean }

/** Plate labels (the plates are ~70 px wide: at most ~5 characters). */
const SHORT: Record<string, string> = {
  "Accuracy": "Acc", "Agility": "Agil", "Attack Speed": "ASpd", "Critical Rate": "Crit", "Critical Damage": "CDmg",
  "Magic Resist": "MRes", "Poison Resist": "PRes", "Health Recovery": "HPRc", "Mana Recovery": "MPRc", "Poison Recovery": "PsRc",
  "Freeze": "Frz", "Poison Attack": "PAtk", "Wear Weight": "WWt", "Hand Weight": "HWt", "EXP Rate": "EXP", "Drop Rate": "Drop", "Gold Rate": "GRt",
};
const GROUPS = ["Attack & defence", "Combat", "Resistance & recovery", "Weight", "Rates"];

/** Every stat a plate can track, in list order. Bag Weight is the live Weight entry. */
export const PLATE_STATS: PlateStat[] = (() => {
  const out: PlateStat[] = [
    { key: "Bag", name: "Bag space", short: "Bag", group: "Inventory", value: "", live: true },
    { key: "Weight", name: "Bag weight", short: "Wt", group: "Inventory", value: "", live: true },
    { key: "Gold", name: "Gold", short: "Gold", group: "Inventory", value: "", live: true },
  ];
  let g = 0;
  for (const row of STATS) {
    if (!row) { g++; continue; }
    const [name, value] = row;
    if (name === "Bag Weight") continue;
    out.push({ key: name, name, short: SHORT[name] ?? name, group: GROUPS[g] ?? "Other", value });
  }
  return out;
})();
const BY_KEY = new Map(PLATE_STATS.map((s) => [s.key, s]));
export const plateStat = (key: string): PlateStat => BY_KEY.get(key) ?? PLATE_STATS[0]!;

const STORE = "hudm-stat-plates";
const DEFAULTS = ["AC", "MAC", "DC", "Bag", "Weight", "Gold"];
/** The six plates' stat keys, left wing first. */
export function loadPlates(): string[] {
  try {
    const v = JSON.parse(localStorage.getItem(STORE) ?? "null") as unknown;
    if (Array.isArray(v) && v.length === DEFAULTS.length && v.every((k) => typeof k === "string" && BY_KEY.has(k))) return v as string[];
  } catch { /* storage blocked or bad data: defaults */ }
  return [...DEFAULTS];
}
export function savePlates(keys: string[]): void {
  try { localStorage.setItem(STORE, JSON.stringify(keys)); } catch { /* storage blocked: the picks just aren't kept */ }
}

const CSS = `
.hudm .hm-stat { cursor: pointer; }
.hudm .hm-stat:hover { filter: brightness(1.25); }
.hudm .hm-stat.picking { filter: brightness(1.35); }
/* The bars' layer (.rig) sits under the windows' layer (chat, quest tracker): lift it while the list is open. */
.hudm .rig.spicking { z-index: 2; }
.hudm .hm-spick { position: absolute; bottom: calc(100% + 6px); z-index: 12; width: 300px; padding: 6px; border-radius: var(--rad); pointer-events: auto; font-size: 11px;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 8px 18px #000; animation: hudm-spin .12s ease-out; }
@keyframes hudm-spin { from { opacity: 0; transform: translateY(4px); } }
.hudm .hm-spick.l { left: 0; } .hudm .hm-spick.r { right: 0; }
.hudm .hm-spick .hd { display: flex; align-items: baseline; padding: 2px 4px 5px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac); }
.hudm .hm-spick .hd span { margin-left: auto; letter-spacing: 0; text-transform: none; font-weight: 400; color: var(--dim); }
.hudm .hm-spick .grp { padding: 5px 4px 2px; font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .hm-spick .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1px 4px; }
.hudm .hm-spick .it { display: flex; align-items: center; gap: 6px; height: 20px; padding: 0 6px; border-radius: calc(var(--rad) * .6); cursor: pointer; white-space: nowrap; }
.hudm .hm-spick .it:hover { background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .hm-spick .it span { overflow: hidden; text-overflow: ellipsis; color: var(--tx); }
.hudm .hm-spick .it b { margin-left: auto; font-weight: 600; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .hm-spick .it.on { box-shadow: inset 2px 0 0 var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .hm-spick .it.used span::after { content: " •"; color: var(--ac); }
.hudm .hm-spick .ft { padding: 5px 4px 1px; font-size: 10px; color: var(--dim); }
`;

/** The open list (one at a time). */
export class StatPicker {
  private pop: HTMLElement | null = null;
  private plate: HTMLElement | null = null;

  constructor() {
    injectCss("hudm-statpick-css", CSS);
    document.addEventListener("mousedown", (e) => { if (this.pop && !(e.target instanceof Element && e.target.closest(".hm-spick, .hm-stat"))) this.close(); });
    // Capture: Esc closes the list before the HUD sees it (which would open the game menu).
    window.addEventListener("keydown", (e) => { if (this.pop && e.key === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); this.close(); } }, true);
  }

  get isOpen(): boolean { return !!this.pop; }

  /** Opens the list over `plate` (in the stats row `row`); a second click on the same plate closes it.
   *  `current` = every plate's key, `slot` = this plate's index, `value` = a live stat's text now. */
  toggle(plate: HTMLElement, row: HTMLElement, right: boolean, slot: number, current: string[], value: (s: PlateStat) => string, pick: (key: string) => void): void {
    if (this.plate === plate) { this.close(); return; }
    this.close();
    const pop = h("div", "hm-spick " + (right ? "r" : "l"), h("div", "hd", "Track a stat", h("span", "", "on this plate")));
    let group = "", grid: HTMLElement | null = null;
    for (const s of PLATE_STATS) {
      if (s.group !== group) { group = s.group; grid = h("div", "grid"); pop.append(h("div", "grp", group), grid); }
      const at = current.indexOf(s.key);
      const it = h("div", "it" + (at === slot ? " on" : at >= 0 ? " used" : ""), h("span", "", s.name), h("b", "", value(s)));
      it.title = at === slot ? `${s.name} (on this plate)` : at >= 0 ? `${s.name}: on another plate, picking it swaps the two` : s.name;
      it.addEventListener("click", () => { this.close(); pick(s.key); });
      grid!.append(it);
    }
    pop.append(h("div", "ft", "• already on another plate (picking it swaps them)"));
    row.append(pop);
    plate.classList.add("picking");
    plate.closest(".rig")?.classList.add("spicking");
    this.pop = pop; this.plate = plate;
  }

  close(): void {
    this.pop?.remove();
    this.plate?.classList.remove("picking");
    this.plate?.closest(".rig")?.classList.remove("spicking");
    this.pop = null; this.plate = null;
  }
}
