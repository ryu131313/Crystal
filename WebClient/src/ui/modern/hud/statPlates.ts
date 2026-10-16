// Ported from play/hudStatPick.ts (the plate picker) and play/hudMock.ts (the six stat plates, ~776-785 and 1238-1273):
// the six small plates above the HP / MP bars, each tracking a stat the player picks, fed by the real stats
// (PlayerState.stats = UserObject.Stats after RefreshStats, formatted like the desktop's CharacterDialog Stats / State
// pages, characterWindow.ts) and the live bag count, bag weight and gold (Inventory; gold is only ever read here).
// Click a plate: the list of every stat opens above it, grouped like the Stats panel with each value; a stat already
// on another plate swaps. A plate whose stat has an action (Gold: MainDialog.GoldLabel, pick the gold up to drop it on
// the map) runs it on a left click and opens the list on a right click (or the context-menu key). Esc (Hud.handleKey) or a click elsewhere closes the list. The picks are the UiPlates setting
// (the integrator's settings API: HudHost.plates / savePlates; parsePlates / formatPlates turn it into the string).
//
// Also the HUD's one small popover (the stat list and the attack / pet mode lists share it; Review-Core "make one
// popover component"): one open at a time, closed by an outside pointerdown (a capture listener in the UI scope).

import { Stat } from "../../../protocol/enums.generated.ts";
import { BELT_IDX } from "../../../game/inventory.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { asButton, h, injectCss, setText } from "../core/dom.ts";
import { fmtCompact } from "../core/format.ts";

// — Popover —

const POP_CSS = `
.hudm .hm-pop { position: absolute; bottom: calc(100% + 6px); z-index: 12; padding: 4px; border-radius: var(--rad); pointer-events: auto; font-size: 11px;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 8px 18px #000; animation: hudm-popin .12s ease-out; }
@keyframes hudm-popin { from { opacity: 0; transform: translateY(4px); } }
.hudm .hm-pop.l { left: 0; } .hudm .hm-pop.r { right: 0; }
/* The rig sits under the windows layer: lift it while one of its lists is open. */
.hudm > .rig.hm-lift { z-index: 3; }
`;

/** One open list at a time, appended inside a positioned host element of the rig. */
export class Popover {
  private el: HTMLElement | null = null;
  private anchor: HTMLElement | null = null;
  private after: (() => void) | null = null;

  constructor(private readonly rig: HTMLElement, signal: AbortSignal) {
    injectCss("hud-popover", POP_CSS);
    // Capture: an outside press closes the list before anything else sees it (the anchor's own click toggles it).
    window.addEventListener("pointerdown", (e) => {
      if (!this.el) return;
      const t = e.target;
      if (t instanceof Node && (this.el.contains(t) || this.anchor?.contains(t))) return;
      this.close();
    }, { capture: true, signal });
    signal.addEventListener("abort", () => this.close(), { once: true });
  }

  get open(): boolean { return this.el !== null; }
  /** Whether the open list belongs to `anchor` (a second click on it closes the list). */
  isFor(anchor: HTMLElement): boolean { return this.anchor === anchor; }

  /** Shows `el` (class hm-pop added; "l" / "r" side) inside `host`, closing any other list. `after` runs on close. */
  show(anchor: HTMLElement, host: HTMLElement, el: HTMLElement, right: boolean, after?: () => void): void {
    this.close();
    el.classList.add("hm-pop", right ? "r" : "l");
    host.append(el);
    this.el = el; this.anchor = anchor; this.after = after ?? null;
    this.rig.classList.add("hm-lift");
  }

  close(): void {
    if (!this.el) return;
    this.el.remove();
    const after = this.after;
    this.el = this.anchor = this.after = null;
    this.rig.classList.remove("hm-lift");
    after?.();
  }
}

// — Plate stats —

/** Where plate values come from (GameUiContext fits). */
type Src = Pick<GameUiContext, "player" | "models">;

/** A stat a plate can track. `level`: "warn" / "full" states (weight, bag). */
export interface PlateStat {
  readonly key: string; readonly name: string; readonly short: string; readonly group: string;
  value(s: Src): string;
  level?(s: Src): "" | "warn" | "full";
  /** A left click on the plate runs this (the stat list then opens on a right click); `actionHint` names it. */
  action?(s: Src): void;
  readonly actionHint?: string;
}

const st = (s: Src, k: number) => s.player.stats?.stats[k] ?? 0;
const pair = (min: number, max: number) => (s: Src) => `${st(s, min)}-${st(s, max)}`;
const plus = (k: number) => (s: Src) => `+${st(s, k)}`;
const num = (k: number) => (s: Src) => String(st(s, k));
const rate = (k: number) => (s: Src) => `+${st(s, k)}%`;

/** Bag cells (after the belt) in use and in total; loops without allocating. */
function bagUse(s: Src): [number, number] {
  const inv = s.models.inventory.inventory;
  let used = 0;
  for (let i = BELT_IDX; i < inv.length; i++) if (inv[i]) used++;
  return [used, Math.max(0, inv.length - BELT_IDX)];
}
const weightLevel = (cur: number, max: number): "" | "warn" | "full" => (max <= 0 ? "" : cur >= max * 0.95 ? "full" : cur >= max * 0.8 ? "warn" : "");

/** Every stat a plate can track, in list order (CharacterDialog Stats / State pages + the live inventory values). */
export const PLATE_STATS: readonly PlateStat[] = [
  { key: "Bag", name: "Bag space", short: "Bag", group: "Inventory", value: (s) => { const [u, t] = bagUse(s); return `${u}/${t}`; },
    level: (s) => { const [u, t] = bagUse(s); return t > 0 && u >= t ? "full" : ""; } },
  { key: "Weight", name: "Bag weight", short: "Wt", group: "Inventory", value: (s) => `${s.player.stats?.currentBagWeight ?? 0}/${st(s, Stat.BagWeight)}`,
    level: (s) => weightLevel(s.player.stats?.currentBagWeight ?? 0, st(s, Stat.BagWeight)) },
  { key: "Gold", name: "Gold", short: "Gold", group: "Inventory", value: (s) => fmtCompact(s.models.inventory.gold),
    // MainDialog.GoldLabel: picks the gold up (only with gold, never while a bag item is picked); a map click drops it
    // (core/drag.ts mapClick -> ItemDrop.dropGold). The amount is only ever read from Inventory.
    action: (s) => s.models.itemPick.toggleHudGold(s.models.inventory.gold), actionHint: "click to drop gold" },
  { key: "AC", name: "AC", short: "AC", group: "Attack & defence", value: pair(Stat.MinAC, Stat.MaxAC) },
  { key: "MAC", name: "MAC", short: "MAC", group: "Attack & defence", value: pair(Stat.MinMAC, Stat.MaxMAC) },
  { key: "DC", name: "DC", short: "DC", group: "Attack & defence", value: pair(Stat.MinDC, Stat.MaxDC) },
  { key: "MC", name: "MC", short: "MC", group: "Attack & defence", value: pair(Stat.MinMC, Stat.MaxMC) },
  { key: "SC", name: "SC", short: "SC", group: "Attack & defence", value: pair(Stat.MinSC, Stat.MaxSC) },
  { key: "Accuracy", name: "Accuracy", short: "Acc", group: "Combat", value: plus(Stat.Accuracy) },
  { key: "Agility", name: "Agility", short: "Agil", group: "Combat", value: plus(Stat.Agility) },
  { key: "AttackSpeed", name: "Attack Speed", short: "ASpd", group: "Combat", value: num(Stat.AttackSpeed) },
  { key: "Luck", name: "Luck", short: "Luck", group: "Combat", value: num(Stat.Luck) },
  { key: "CriticalRate", name: "Critical Rate", short: "Crit", group: "Combat", value: (s) => `${st(s, Stat.CriticalRate)}%` },
  { key: "CriticalDamage", name: "Critical Damage", short: "CDmg", group: "Combat", value: num(Stat.CriticalDamage) },
  { key: "MagicResist", name: "Magic Resist", short: "MRes", group: "Resistance & recovery", value: plus(Stat.MagicResist) },
  { key: "PoisonResist", name: "Poison Resist", short: "PRes", group: "Resistance & recovery", value: plus(Stat.PoisonResist) },
  { key: "HealthRecovery", name: "Health Recovery", short: "HPRc", group: "Resistance & recovery", value: plus(Stat.HealthRecovery) },
  { key: "SpellRecovery", name: "Mana Recovery", short: "MPRc", group: "Resistance & recovery", value: plus(Stat.SpellRecovery) },
  { key: "PoisonRecovery", name: "Poison Recovery", short: "PsRc", group: "Resistance & recovery", value: plus(Stat.PoisonRecovery) },
  { key: "Holy", name: "Holy", short: "Holy", group: "Resistance & recovery", value: num(Stat.Holy) },
  { key: "Freezing", name: "Freeze", short: "Frz", group: "Resistance & recovery", value: num(Stat.Freezing) },
  { key: "PoisonAttack", name: "Poison Attack", short: "PAtk", group: "Resistance & recovery", value: num(Stat.PoisonAttack) },
  { key: "WearWeight", name: "Wear Weight", short: "WWt", group: "Weight", value: (s) => `${s.player.stats?.currentWearWeight ?? 0}/${st(s, Stat.WearWeight)}`,
    level: (s) => weightLevel(s.player.stats?.currentWearWeight ?? 0, st(s, Stat.WearWeight)) },
  { key: "HandWeight", name: "Hand Weight", short: "HWt", group: "Weight", value: (s) => `${s.player.stats?.currentHandWeight ?? 0}/${st(s, Stat.HandWeight)}`,
    level: (s) => weightLevel(s.player.stats?.currentHandWeight ?? 0, st(s, Stat.HandWeight)) },
  { key: "ExpRate", name: "EXP Rate", short: "EXP", group: "Rates", value: rate(Stat.ExpRatePercent) },
  { key: "DropRate", name: "Drop Rate", short: "Drop", group: "Rates", value: rate(Stat.ItemDropRatePercent) },
  { key: "GoldRate", name: "Gold Rate", short: "GRt", group: "Rates", value: rate(Stat.GoldDropRatePercent) },
];
const BY_KEY = new Map(PLATE_STATS.map((s) => [s.key, s]));
/** The PlateStat for a key (the first one for an unknown key). */
export const plateStat = (key: string): PlateStat => BY_KEY.get(key) ?? PLATE_STATS[0]!;

/** The default picks, left wing first (the mock's). */
export const PLATE_DEFAULTS: readonly string[] = ["AC", "MAC", "DC", "Bag", "Weight", "Gold"];
/** The UiPlates setting string ("AC,MAC,DC,Bag,Weight,Gold") -> six known, distinct keys; the defaults otherwise. */
export function parsePlates(s: string): string[] {
  const keys = s.split(",").map((k) => k.trim());
  return keys.length === PLATE_DEFAULTS.length && keys.every((k) => BY_KEY.has(k)) && new Set(keys).size === keys.length ? keys : [...PLATE_DEFAULTS];
}
/** The six picks -> the UiPlates setting string. */
export function formatPlates(keys: readonly string[]): string { return keys.join(","); }

/** Reads / saves the picks (the integrator maps it to the UiPlates setting). */
export interface PlateStore { plates(): readonly string[]; savePlates(keys: readonly string[]): void }

const CSS = `
.hudm .hm-hrow.stats { gap: 2px; }
.hudm .hm-stat { position: relative; flex: 1; min-width: 0; height: 19px; display: flex; align-items: center; gap: 6px; padding: 0 8px; pointer-events: auto; cursor: pointer;
  background: linear-gradient(180deg, var(--pa), var(--pb));
  box-shadow: inset 0 1px 0 color-mix(in srgb, var(--frl) 25%, transparent), inset 0 0 0 1px var(--fr);
  clip-path: polygon(6px 0, calc(100% - 6px) 0, 100% 50%, calc(100% - 6px) 100%, 6px 100%, 0 50%); font-size: 11px; }
.hudm .hm-stat b { color: var(--dim); font-weight: 700; letter-spacing: .04em; white-space: nowrap; }
.hudm .hm-stat span { margin-left: auto; color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; white-space: nowrap; overflow: hidden; }
.hudm .hm-stat:hover, .hudm .hm-stat.picking { background: linear-gradient(180deg, color-mix(in srgb, var(--pa) 70%, var(--ac)), var(--pb)); }
.hudm .hm-stat.warn span, .hudm .hm-stat.warn b { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .hm-stat.full span, .hudm .hm-stat.full b { color: #fff; text-shadow: 0 0 8px var(--ac); }
/* Full: an accent overlay pulses by opacity (Performance.md T3-8: no animated filter). */
.hudm .hm-stat::after { content: ""; position: absolute; inset: 0; pointer-events: none; opacity: 0; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .hm-stat.full::after { animation: hudm-halo 1s ease-in-out infinite; }
.hudm .hm-spick { width: 300px; padding: 6px; }
.hudm .hm-spick .hd { display: flex; align-items: baseline; padding: 2px 4px 5px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac); }
.hudm .hm-spick .hd span { margin-left: auto; letter-spacing: 0; text-transform: none; font-weight: 400; color: var(--dim); }
.hudm .hm-spick .grp { padding: 5px 4px 2px; font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .hm-spick .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1px 4px; }
.hudm .hm-spick .it { display: flex; align-items: center; gap: 6px; height: 20px; padding: 0 6px; border-radius: calc(var(--rad) * .6); cursor: pointer; white-space: nowrap; }
.hudm .hm-spick .it:hover, .hudm .hm-spick .it:focus-visible { background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .hm-spick .it span { overflow: hidden; text-overflow: ellipsis; color: var(--tx); }
.hudm .hm-spick .it b { margin-left: auto; font-weight: 600; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .hm-spick .it.on { box-shadow: inset 2px 0 0 var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .hm-spick .it.used span::after { content: " \\2022"; color: var(--ac); }
.hudm .hm-spick .ft { padding: 5px 4px 1px; font-size: 10px; color: var(--dim); }
`;

interface PlateEl { el: HTMLElement; b: HTMLElement; v: HTMLElement; level: string; key: string }

/** The six plates: `left` / `right` are the stats rows of the two wings (three plates each). */
export class StatPlates {
  readonly left = h("div", "hm-hrow stats");
  readonly right = h("div", "hm-hrow stats");
  private readonly els: PlateEl[] = [];
  private picks: string[];

  constructor(private readonly src: Src, private readonly store: PlateStore, private readonly pop: Popover) {
    injectCss("hud-plates", CSS);
    this.picks = [...store.plates()];
    if (this.picks.length !== PLATE_DEFAULTS.length || this.picks.some((k) => !BY_KEY.has(k))) this.picks = [...PLATE_DEFAULTS];
    for (let slot = 0; slot < 6; slot++) {
      const b = h("b"), v = h("span");
      const el = asButton(h("div", "hm-stat", b, v));
      el.addEventListener("click", () => {
        const s = plateStat(this.picks[slot]!);
        if (s.action) { this.pop.close(); s.action(this.src); } else this.toggle(slot);
      });
      el.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        if (plateStat(this.picks[slot]!).action) this.toggle(slot);
      });
      this.els.push({ el, b, v, level: "", key: "" });
      (slot < 3 ? this.left : this.right).append(el);
      this.label(slot);
    }
    this.update();
  }

  /** Writes the plates' values (cheap; only changed text / classes touch the DOM). The HUD calls it ~4x a second. */
  update(): void {
    for (let slot = 0; slot < this.els.length; slot++) {
      const p = this.els[slot]!, s = plateStat(this.picks[slot]!);
      setText(p.v, s.value(this.src));
      const level = s.level?.(this.src) ?? "";
      if (level !== p.level) {
        p.el.classList.toggle("warn", level === "warn");
        p.el.classList.toggle("full", level === "full");
        p.level = level;
      }
    }
  }

  /** The picks were changed elsewhere (Options' plate picker): re-read the store. */
  reload(): void {
    const next = [...this.store.plates()];
    if (next.length !== PLATE_DEFAULTS.length || next.some((k) => !BY_KEY.has(k))) return;
    this.picks = next;
    for (let i = 0; i < this.els.length; i++) this.label(i);
    this.update();
  }

  private label(slot: number): void {
    const p = this.els[slot]!, s = plateStat(this.picks[slot]!);
    if (p.key === s.key) return;
    p.key = s.key;
    setText(p.b, s.short);
    p.el.title = s.action ? `${s.name} · ${s.actionHint ?? "click to use"} · right-click to track another stat` : `${s.name} · click to track another stat`;
    p.el.setAttribute("aria-label", `${s.name} plate`);
  }

  /** Opens / closes the stat list over plate `slot`. */
  private toggle(slot: number): void {
    const p = this.els[slot]!;
    if (this.pop.isFor(p.el)) { this.pop.close(); return; }
    const right = slot >= 3;
    const list = h("div", "hm-spick", h("div", "hd", "Track a stat", h("span", "", "on this plate")));
    let group = "", grid: HTMLElement | null = null;
    for (const s of PLATE_STATS) {
      if (s.group !== group) { group = s.group; grid = h("div", "grid"); list.append(h("div", "grp", group), grid); }
      const at = this.picks.indexOf(s.key);
      const it = asButton(h("div", "it" + (at === slot ? " on" : at >= 0 ? " used" : ""), h("span", "", s.name), h("b", "", s.value(this.src))));
      it.title = at === slot ? `${s.name} (on this plate)` : at >= 0 ? `${s.name}: on another plate, picking it swaps the two` : s.name;
      it.addEventListener("click", () => { this.pop.close(); this.pick(slot, s.key); });
      grid!.append(it);
    }
    list.append(h("div", "ft", "• already on another plate (picking it swaps them)"));
    p.el.classList.add("picking");
    this.pop.show(p.el, right ? this.right : this.left, list, right, () => p.el.classList.remove("picking"));
  }

  /** Plate `slot` tracks `key`; a plate already tracking it takes this plate's old stat (a swap). */
  private pick(slot: number, key: string): void {
    const other = this.picks.indexOf(key);
    if (other === slot) return;
    if (other >= 0) { this.picks[other] = this.picks[slot]!; this.label(other); }
    this.picks[slot] = key;
    this.label(slot);
    this.update();
    this.store.savePlates([...this.picks]);
  }
}
