// Character window (C): CharacterDialog's status page — name, class, level, HP / MP / experience and the stats from
// UserObject.RefreshStats (src/game/stats.ts). Equipment stays in the inventory window.

import { MirClassName, Stat, StatName } from "../protocol/enums.generated.ts";
import type { UserStats } from "../game/stats.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matchesAny, type KeybindOption } from "../game/keybinds.ts";
import { el } from "./screens.ts";

export interface CharacterView {
  name: string; cls: number; level: number; hp: number; mp: number; experience: bigint; maxExperience: bigint; stats: UserStats | null;
}

/** The ranged / paired rows CharacterDialog shows first; any other non-zero stat follows by name. */
const PAIRS: [string, number, number][] = [
  ["AC", Stat.MinAC, Stat.MaxAC], ["MAC", Stat.MinMAC, Stat.MaxMAC], ["DC", Stat.MinDC, Stat.MaxDC], ["MC", Stat.MinMC, Stat.MaxMC], ["SC", Stat.MinSC, Stat.MaxSC],
];
const SINGLES: [string, number][] = [
  ["Accuracy", Stat.Accuracy], ["Agility", Stat.Agility], ["Luck", Stat.Luck], ["Attack speed", Stat.AttackSpeed],
  ["Magic resist", Stat.MagicResist], ["Poison resist", Stat.PoisonResist], ["Health recovery", Stat.HealthRecovery], ["Mana recovery", Stat.SpellRecovery],
  ["Poison recovery", Stat.PoisonRecovery], ["Critical rate", Stat.CriticalRate], ["Critical damage", Stat.CriticalDamage], ["Holy", Stat.Holy],
  ["Freezing", Stat.Freezing], ["Poison attack", Stat.PoisonAttack], ["Reflect", Stat.Reflect],
];
const SHOWN = new Set<number>([...PAIRS.flatMap(([, a, b]) => [a, b]), ...SINGLES.map(([, k]) => k), Stat.HP, Stat.MP, Stat.BagWeight, Stat.WearWeight, Stat.HandWeight]);

const camel = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2");

export class CharacterWindow {
  private readonly root = el("div", { class: "window char-window", hidden: true });
  private readonly body = el("div", { class: "char-body" });
  private readonly view: () => CharacterView | null;

  /** `opts` (the hero's CharacterDialog, heroCharacterWindow.ts): another title / class, and Ctrl+C instead of C
   *  (KeybindOptions.HeroEquipment). A null view hides the window. */
  constructor(stage: HTMLElement, view: () => CharacterView | null, opts: { title?: string; className?: string; ctrlKey?: boolean } = {}) {
    this.view = view;
    if (opts.className) this.root.classList.add(opts.className);
    this.root.append(el("div", { class: "window-title" }, el("span", {}, opts.title ?? "Character"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")), this.body);
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    // KeybindOptions.Equipment (F10) / Equipment2 (C without Ctrl); the hero's: HeroEquipment (Ctrl+C)
    const keys: KeybindOption[] = opts.ctrlKey ? [KeybindOptions.HeroEquipment] : [KeybindOptions.Equipment, KeybindOptions.Equipment2];
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (!matchesAny(e, ...keys) || a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (e.key === "F10") e.preventDefault(); // the browser's menu bar
      if (e.ctrlKey) {
        if (String(window.getSelection() ?? "") !== "") return; // keep Ctrl+C as copy while text is selected
        e.preventDefault();
      }
      this.toggle();
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    this.render();
  }

  render(): void {
    if (this.root.hidden) return;
    const v = this.view();
    if (!v) { this.root.hidden = true; return; }
    const st = v.stats?.stats ?? {};
    const s = (k: number) => st[k] ?? 0;
    const row = (label: string, value: string) => el("div", { class: "char-row" }, el("span", {}, label), el("span", {}, value));
    const exp = v.maxExperience > 0n ? `${((Number(v.experience) * 100) / Number(v.maxExperience)).toFixed(2)}%` : "-";
    const rows = [
      row("Name", v.name), row("Class", MirClassName[v.cls] ?? String(v.cls)), row("Level", String(v.level)),
      row("HP", `${v.hp} / ${s(Stat.HP)}`), row("MP", `${v.mp} / ${s(Stat.MP)}`), row("Experience", exp),
      ...PAIRS.map(([label, a, b]) => row(label, `${s(a)}-${s(b)}`)),
      ...SINGLES.filter(([, k]) => s(k) !== 0 || k === Stat.Accuracy || k === Stat.Agility).map(([label, k]) => row(label, String(s(k)))),
    ];
    if (v.stats) {
      rows.push(row("Bag weight", `${v.stats.currentBagWeight} / ${s(Stat.BagWeight)}`), row("Wear weight", `${v.stats.currentWearWeight} / ${s(Stat.WearWeight)}`),
        row("Hand weight", `${v.stats.currentHandWeight} / ${s(Stat.HandWeight)}`));
      for (const [k, value] of Object.entries(st)) {
        if (!value || SHOWN.has(Number(k))) continue;
        rows.push(row(camel(StatName[Number(k)] ?? `Stat ${k}`), String(value)));
      }
    }
    this.body.replaceChildren(...rows);
  }
}
