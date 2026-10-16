// Skills window mock for the scene lab's UI tester (window style from hudWindow.ts). A revamp of the desktop's skill
// page (seven MagicButtons and Back / Next): a searchable list with category filters and a "learned only" switch on the
// left, the picked spell's detail on the right (icon, type, level pips, description, MP cost, cooldown, range, the
// level it unlocks / levels up at, training progress) and F1-F8 buttons that put it on the HUD skill bar.
// Spell names, icons (MagIcon2 = Icon * 2), unlock levels, training needs, base MP costs and delays are the server's
// defaults (Server/MirEnvir/Envir.cs); descriptions are the client's (src/ui/skillHints.ts). The player's level and
// class come from the HUD mock; skill levels and training are mock values derived from the level.

import { MirClass, Spell } from "../src/protocol/enums.generated.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { SKILL_DESCRIPTIONS } from "../src/ui/skillHints.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

export type Role = "Attack" | "Area" | "Buff" | "Support" | "Summon" | "Movement" | "Passive";
const ROLES: Role[] = ["Attack", "Area", "Buff", "Support", "Summon", "Movement", "Passive"];

/** [Spell key, Icon, Level1 (unlock), Need1-3 (training), BaseCost (MP), DelayBase ms (0 = none), Range, role]. */
type Row = [keyof typeof Spell, number, number, number, number, number, number, number, number, Role];
const DATA: Record<number, Row[]> = {
  [MirClass.Warrior]: [
    ["Fencing", 2, 7, 270, 600, 1300, 0, 0, 0, "Passive"], ["Slaying", 6, 15, 500, 1100, 1800, 0, 0, 0, "Passive"],
    ["Thrusting", 11, 22, 2000, 3500, 6000, 0, 0, 0, "Attack"], ["HalfMoon", 24, 26, 5000, 8000, 14000, 3, 0, 0, "Area"],
    ["ShoulderDash", 26, 30, 3000, 4000, 6000, 4, 2500, 0, "Movement"], ["TwinDrakeBlade", 37, 32, 4000, 6000, 10000, 10, 0, 0, "Attack"],
    ["Entrapment", 46, 32, 2000, 3500, 5500, 15, 0, 9, "Support"], ["FlamingSword", 25, 35, 2000, 4000, 6000, 7, 0, 0, "Attack"],
    ["LionRoar", 42, 36, 5000, 8000, 12000, 14, 0, 0, "Area"], ["CrossHalfMoon", 33, 38, 7000, 11000, 16000, 6, 0, 0, "Area"],
    ["BladeAvalanche", 43, 38, 5000, 8000, 12000, 14, 0, 0, "Area"], ["ProtectionField", 50, 39, 6000, 12000, 18000, 23, 0, 0, "Buff"],
    ["Rage", 49, 44, 8000, 14000, 20000, 20, 0, 0, "Buff"], ["Fury", 76, 45, 8000, 14000, 20000, 10, 600000, 0, "Buff"],
    ["CounterAttack", 72, 47, 7000, 11000, 15000, 12, 24000, 0, "Buff"], ["SlashingBurst", 55, 50, 10000, 16000, 24000, 25, 14000, 0, "Movement"],
    ["ImmortalSkin", 80, 60, 1560, 2200, 3000, 10, 600000, 0, "Buff"],
  ],
  [MirClass.Wizard]: [
    ["FireBall", 0, 7, 200, 350, 700, 3, 0, 9, "Attack"], ["Repulsion", 7, 12, 500, 1300, 2200, 2, 0, 0, "Area"],
    ["ElectricShock", 19, 13, 530, 1100, 2200, 3, 0, 9, "Summon"], ["GreatFireBall", 4, 15, 2000, 2700, 3500, 5, 0, 9, "Attack"],
    ["HellFire", 8, 16, 700, 2700, 3500, 10, 0, 0, "Area"], ["ThunderBolt", 10, 17, 500, 2000, 3500, 9, 0, 9, "Attack"],
    ["Teleport", 20, 19, 350, 1000, 2000, 10, 0, 0, "Movement"], ["Blink", 20, 19, 350, 1000, 2000, 10, 0, 9, "Movement"],
    ["FireBang", 22, 22, 3000, 5000, 10000, 14, 0, 9, "Area"], ["FireWall", 21, 24, 4000, 10000, 20000, 30, 0, 9, "Area"],
    ["Lightning", 9, 26, 3000, 6000, 12000, 38, 0, 0, "Area"], ["FrostCrunch", 38, 28, 3000, 5000, 8000, 15, 0, 9, "Attack"],
    ["ThunderStorm", 23, 30, 4000, 8000, 12000, 29, 0, 0, "Area"], ["MagicShield", 30, 31, 3000, 7000, 10000, 35, 0, 0, "Buff"],
    ["TurnUndead", 31, 32, 3000, 7000, 10000, 52, 0, 9, "Attack"], ["Vampirism", 47, 33, 3000, 5000, 8000, 26, 0, 9, "Attack"],
    ["IceStorm", 32, 35, 4000, 8000, 12000, 33, 0, 9, "Area"], ["FlameDisruptor", 34, 38, 5000, 9000, 14000, 28, 0, 9, "Attack"],
    ["Mirroring", 41, 41, 6000, 11000, 16000, 21, 0, 0, "Summon"], ["FlameField", 44, 42, 6000, 11000, 16000, 45, 0, 9, "Area"],
    ["Blizzard", 51, 44, 8000, 16000, 24000, 65, 0, 9, "Area"], ["MagicBooster", 73, 47, 12000, 18000, 24000, 150, 0, 0, "Buff"],
    ["MeteorStrike", 52, 49, 15000, 20000, 25000, 115, 0, 9, "Area"], ["IceThrust", 56, 53, 17000, 22000, 27000, 100, 0, 0, "Area"],
    ["StormEscape", 23, 60, 2200, 3300, 4400, 65, 0, 9, "Movement"],
  ],
  [MirClass.Taoist]: [
    ["Healing", 1, 7, 150, 350, 700, 3, 0, 9, "Support"], ["SpiritSword", 3, 9, 350, 1300, 2700, 0, 0, 0, "Passive"],
    ["Poisoning", 5, 14, 700, 1300, 2700, 2, 0, 9, "Attack"], ["SoulFireBall", 12, 18, 1300, 2700, 4000, 3, 0, 9, "Attack"],
    ["SummonSkeleton", 16, 19, 1000, 2000, 3500, 12, 0, 0, "Summon"], ["Hiding", 17, 20, 1300, 2700, 5300, 1, 0, 0, "Buff"],
    ["MassHiding", 18, 21, 1300, 2700, 5300, 2, 0, 9, "Buff"], ["SoulShield", 13, 22, 2000, 3500, 7000, 2, 0, 9, "Buff"],
    ["Revelation", 27, 23, 1500, 2500, 4000, 4, 0, 9, "Support"], ["BlessedArmour", 14, 25, 4000, 6000, 10000, 2, 0, 9, "Buff"],
    ["EnergyRepulsor", 36, 27, 1800, 2400, 3200, 2, 0, 0, "Area"], ["TrapHexagon", 15, 28, 2500, 5000, 10000, 7, 0, 9, "Support"],
    ["Purification", 39, 30, 3000, 5000, 8000, 14, 0, 9, "Support"], ["MassHealing", 28, 31, 2000, 4000, 8000, 28, 0, 9, "Support"],
    ["Hallucination", 48, 31, 4000, 6000, 9000, 22, 0, 9, "Attack"], ["UltimateEnhancer", 35, 33, 5000, 7000, 10000, 28, 0, 9, "Buff"],
    ["SummonShinsu", 29, 35, 2000, 4000, 6000, 28, 0, 0, "Summon"], ["Reincarnation", 53, 37, 2000, 6000, 10000, 125, 0, 9, "Support"],
    ["SummonHolyDeva", 40, 38, 4000, 6000, 9000, 28, 0, 0, "Summon"], ["HealingCircle", 82, 39, 7000, 12000, 15000, 10, 0, 9, "Support"],
    ["Curse", 45, 40, 4000, 6000, 9000, 17, 0, 9, "Attack"], ["Plague", 74, 42, 5000, 9000, 13000, 20, 0, 9, "Area"],
    ["PoisonCloud", 54, 43, 4000, 8000, 12000, 30, 18000, 9, "Area"], ["PetEnhancer", 78, 45, 4000, 8000, 12000, 30, 0, 0, "Buff"],
    ["EnergyShield", 57, 48, 5000, 9000, 13000, 50, 0, 9, "Buff"],
  ],
};

export interface SkillEntry {
  key: keyof typeof Spell; name: string; icon: number; unlock: number; need: [number, number, number];
  cost: number; delay: number; range: number; role: Role; type: string; text: string;
}
/** What the window needs from the HUD: the player, the skill bar's keys, and putting a spell on a key. */
export interface SkillsHost {
  level(): number;
  cls(): number;
  /** Bar slot (0-7) holding this spell (by Spell key), or -1. */
  keyOf(key: string): number;
  assign(slot: number, s: SkillEntry): void;
}

const CLASS_NAME: Record<number, string> = { [MirClass.Warrior]: "Warrior", [MirClass.Wizard]: "Wizard", [MirClass.Taoist]: "Taoist" };
const split = (k: string) => k.replace(/([a-z])([A-Z])/g, "$1 $2");

/** The client's hint text, split into its type line ("Toggle Skill", ...) and a plain description (no name / cost / level lines). */
function describe(key: keyof typeof Spell): { name: string; type: string; text: string } {
  const raw = SKILL_DESCRIPTIONS[Spell[key]];
  if (!raw) return { name: split(key), type: "Active", text: "" };
  const lines = raw.split("\n").map((l) => l.trim());
  const typeLine = lines.find((l) => /\bSkill\b/.test(l) && l.length < 24) ?? "";
  const type = /Passive/.test(typeLine) ? "Passive" : /Toggle/.test(typeLine) ? "Toggle" : /Buff/.test(typeLine) ? "Buff" : "Active";
  const body = lines.slice(1).filter((l) => l && l !== typeLine && !/^(Mana Cost|Current Skill Level|Next Level|Instant Casting|Cast Time)/i.test(l));
  return { name: lines[0] || split(key), type, text: body.join(" ").replace(/\s+/g, " ").replace(/ \./g, ".") };
}

function entries(cls: number): SkillEntry[] {
  return (DATA[cls] ?? DATA[MirClass.Warrior]!).map(([key, icon, unlock, n1, n2, n3, cost, delay, range, role]) => {
    const d = describe(key);
    return { key, name: d.name, icon, unlock, need: [n1, n2, n3], cost, delay, range, role, type: d.type, text: d.text };
  });
}

/** Mock progress from the player's level: skill level 0-3 and training points towards the next. */
function progress(s: SkillEntry, level: number): { learned: boolean; lv: number; exp: number; next: number } {
  if (level < s.unlock) return { learned: false, lv: 0, exp: 0, next: s.need[0] };
  const lv = Math.min(3, Math.floor((level - s.unlock) / 3));
  if (lv >= 3) return { learned: true, lv: 3, exp: 0, next: 0 };
  const seed = (s.icon * 37 + s.unlock * 11 + lv * 5) % 97;
  const next = s.need[lv]!;
  return { learned: true, lv, exp: Math.round(next * (0.1 + seed / 120)), next };
}

const CSS = `
.hudm .swin { --winh: 476px; width: 620px; }
.hudm .swin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .swin .cols { flex: 1; min-height: 0; display: flex; gap: 0; }
.hudm .swin .left { width: 340px; display: flex; flex-direction: column; padding: 10px 10px 8px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin .tools { display: flex; gap: 6px; align-items: center; }
.hudm .swin .search { flex: 1; position: relative; }
.hudm .swin .search input { width: 100%; height: 26px; box-sizing: border-box; padding: 0 8px 0 26px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .swin .search input:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin .search input::placeholder { color: var(--dim); }
.hudm .swin .search svg { position: absolute; left: 8px; top: 7px; width: 12px; height: 12px; fill: none; stroke: var(--dim); stroke-width: 2.4; stroke-linecap: round; }
.hudm .swin .chips { display: flex; flex-wrap: wrap; gap: 4px; margin: 8px 0 6px; }
.hudm .swin .chip { height: 20px; padding: 0 8px; display: inline-flex; align-items: center; gap: 4px; font-size: 11px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 10px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .swin .chip i { font-style: normal; font-size: 10px; opacity: .7; }
.hudm .swin .chip:hover { color: var(--tx); border-color: var(--frl); }
.hudm .swin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .swin .row { position: relative; display: flex; align-items: center; gap: 9px; height: 46px; padding: 0 8px 0 4px; margin-bottom: 3px; cursor: pointer;
  border: 1px solid transparent; border-radius: var(--rad); }
.hudm .swin .row:nth-child(odd) { background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .swin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .swin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .swin .ic { position: relative; width: 38px; height: 36px; flex: none; border-radius: calc(var(--rad) * .8); overflow: hidden; border: 1px solid var(--fr);
  background: center / auto no-repeat color-mix(in srgb, var(--pb) 70%, #000); image-rendering: pixelated; box-shadow: 0 0 0 1px #000; }
.hudm .swin .row.locked .ic { filter: grayscale(1) brightness(.45); }
.hudm .swin .row.locked .nm { color: var(--dim); }
.hudm .swin .mid { flex: 1; min-width: 0; }
.hudm .swin .nm { display: flex; align-items: center; gap: 6px; font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; }
.hudm .swin .badge { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .swin .sub { display: flex; align-items: center; gap: 7px; margin-top: 4px; }
.hudm .swin .pips { display: flex; gap: 2px; }
.hudm .swin .pips b { width: 9px; height: 4px; border-radius: 1px; background: color-mix(in srgb, var(--fr) 60%, #000); }
.hudm .swin .pips b.on { background: var(--ac); box-shadow: 0 0 4px var(--glow); }
.hudm .swin .tbar { flex: 1; height: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .swin .tbar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .swin .rt { flex: none; text-align: right; font-size: 10px; line-height: 1.3; color: var(--dim); }
.hudm .swin .rt b { display: block; font-size: 11px; color: var(--tx); font-weight: 600; }
.hudm .swin .key { position: absolute; left: 1px; top: 1px; z-index: 1; font-size: 9px; font-weight: 700; padding: 0 3px; border-radius: 3px; color: #fff;
  background: color-mix(in srgb, var(--ac) 70%, #000); box-shadow: 0 0 4px var(--glow); }
.hudm .swin .empty { padding: 30px 10px; text-align: center; color: var(--dim); font-size: 12px; }
.hudm .swin .foot { padding-top: 6px; font-size: 11px; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .swin .foot b { color: var(--tx); font-weight: 600; }
.hudm .swin .detail { flex: 1; min-width: 0; padding: 14px 14px 12px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .swin .head { display: flex; gap: 12px; align-items: center; }
.hudm .swin .big { width: 76px; height: 72px; flex: none; border-radius: var(--rad); border: 1px solid var(--ac); image-rendering: pixelated;
  background: center / 72px 68px no-repeat color-mix(in srgb, var(--pb) 70%, #000); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .swin .big.locked { filter: grayscale(1) brightness(.5); border-color: var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .swin .head h3 { margin: 0 0 4px; font-size: 16px; color: #fff; }
.hudm .swin .head .tags { display: flex; gap: 5px; margin-bottom: 6px; }
.hudm .swin .head .pips b { width: 14px; height: 5px; }
.hudm .swin .desc { font-size: 12px; line-height: 1.5; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); }
.hudm .swin .facts { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 10px; }
.hudm .swin .facts div { display: flex; justify-content: space-between; font-size: 11px; padding: 3px 7px; border-radius: calc(var(--rad) * .5); background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .swin .facts span { color: var(--dim); } .hudm .swin .facts b { color: var(--tx); font-weight: 600; }
.hudm .swin .train { font-size: 11px; color: var(--dim); }
.hudm .swin .train .tbar { height: 6px; margin-top: 4px; border: 1px solid var(--fr); }
.hudm .swin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .swin .keys { display: grid; grid-template-columns: repeat(4, 1fr); gap: 4px; margin-top: 5px; }
.hudm .swin .keys .btn { padding: 0; }
.hudm .swin .keys .btn.on { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 30%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin .keys .btn.dis { opacity: .4; pointer-events: none; }
`;

export class SkillsMock extends HudWindow {
  private list!: HTMLElement;
  private detail!: HTMLElement;
  private foot!: HTMLElement;
  private chips!: HTMLElement;
  private search!: HTMLInputElement;
  private role: Role | "All" = "All";
  private learnedOnly = false;
  private all: SkillEntry[] = [];
  private picked = "";
  private builtFor = "";

  constructor(private readonly host: SkillsHost) {
    super("swin", 380, 60);
    injectCss("hudm-skills-css", CSS);
    this.build();
  }

  protected override shown(): void { this.refresh(true); }

  /** Re-read the HUD (class / level / keys); rebuilds the list when the class changed. */
  refresh(force = false): void {
    if (!this.open && !force) return;
    const key = `${this.host.cls()}/${this.host.level()}`;
    if (key !== this.builtFor) {
      if (this.builtFor.split("/")[0] !== String(this.host.cls())) { this.all = entries(this.host.cls()); this.picked = ""; }
      this.builtFor = key;
    }
    this.renderChips();
    this.renderList();
    this.renderDetail();
  }

  private build(): void {
    const icon = '<svg viewBox="0 0 24 24"><path d="M10.5 3a7.5 7.5 0 105.2 12.9L21 21M15.7 15.9A7.5 7.5 0 0010.5 3"/></svg>';
    this.search = h("input");
    this.search.placeholder = "Search skills…";
    this.search.addEventListener("input", () => this.renderList());
    this.search.addEventListener("keydown", (e) => e.stopPropagation()); // typing must not reach the HUD / lab keys
    const sWrap = h("div", "search", this.search);
    sWrap.insertAdjacentHTML("afterbegin", icon);
    const learned = h("div", "chip", "Learned only");
    learned.addEventListener("click", () => { this.learnedOnly = !this.learnedOnly; learned.classList.toggle("on", this.learnedOnly); this.renderList(); });
    this.chips = h("div", "chips");
    this.list = h("div", "list");
    this.foot = h("div", "foot");
    const left = h("div", "left", h("div", "tools", sWrap, learned), this.chips, this.list, this.foot);
    this.detail = h("div", "detail");
    this.el.append(h("div", "panel", this.titleBar("SKILLS", "Close (S)"), h("div", "cols", left, this.detail)));
  }

  private filtered(): SkillEntry[] {
    const q = this.search.value.trim().toLowerCase();
    const lvl = this.host.level();
    return this.all.filter((s) => (this.role === "All" || s.role === this.role) && (!this.learnedOnly || lvl >= s.unlock)
      && (!q || s.name.toLowerCase().includes(q) || s.text.toLowerCase().includes(q) || s.role.toLowerCase().includes(q) || s.type.toLowerCase().includes(q)));
  }

  private renderChips(): void {
    this.chips.replaceChildren();
    for (const r of ["All", ...ROLES] as (Role | "All")[]) {
      const n = r === "All" ? this.all.length : this.all.filter((s) => s.role === r).length;
      if (!n) continue;
      const c = h("div", "chip" + (this.role === r ? " on" : ""), r, h("i", "", String(n)));
      c.addEventListener("click", () => { this.role = r; this.renderChips(); this.renderList(); });
      this.chips.append(c);
    }
  }

  private renderList(): void {
    const lvl = this.host.level();
    const items = this.filtered();
    this.list.replaceChildren();
    if (!items.length) this.list.append(h("div", "empty", "No skills match."));
    for (const s of items) {
      const p = progress(s, lvl);
      const ic = h("div", "ic");
      void libPicture(Lib.MagIcon2, s.icon * 2).then((pic) => { if (pic) ic.style.backgroundImage = `url(${pic.url})`; });
      const slot = this.host.keyOf(s.key);
      if (slot >= 0) ic.append(h("span", "key", "F" + (slot + 1)));
      const pips = h("div", "pips");
      for (let i = 0; i < 3; i++) pips.append(h("b", i < p.lv ? "on" : ""));
      const bar = h("div", "tbar", h("i"));
      (bar.firstChild as HTMLElement).style.width = p.lv >= 3 ? "100%" : p.next ? (p.exp / p.next * 100).toFixed(0) + "%" : "0";
      const right = p.learned
        ? h("div", "rt", h("b", "", p.lv >= 3 ? "MAX" : `Lv ${p.lv}`), s.cost ? `${s.cost} MP` : s.type)
        : h("div", "rt", h("b", "", `Lv ${s.unlock}`), "locked");
      const row = h("div", "row" + (p.learned ? "" : " locked") + (s.name === this.picked ? " sel" : ""), ic,
        h("div", "mid", h("div", "nm", s.name, h("span", "badge", s.type)), h("div", "sub", pips, p.learned ? bar : h("span", "", ""))), right);
      row.addEventListener("click", () => { this.picked = s.name; this.renderList(); this.renderDetail(); });
      this.list.append(row);
    }
    const learnedN = this.all.filter((s) => lvl >= s.unlock).length;
    this.foot.replaceChildren(h("span", "", h("b", "", String(learnedN)), ` of ${this.all.length} learned`), h("span", "", `${CLASS_NAME[this.host.cls()] ?? ""} · Level ${lvl}`));
  }

  private renderDetail(): void {
    const s = this.all.find((x) => x.name === this.picked) ?? this.filtered()[0] ?? this.all[0];
    this.detail.replaceChildren();
    if (!s) return;
    if (!this.picked) this.picked = s.name;
    const p = progress(s, this.host.level());
    const big = h("div", "big" + (p.learned ? "" : " locked"));
    void libPicture(Lib.MagIcon2, s.icon * 2).then((pic) => { if (pic) big.style.backgroundImage = `url(${pic.url})`; });
    const pips = h("div", "pips");
    for (let i = 0; i < 3; i++) pips.append(h("b", i < p.lv ? "on" : ""));
    const head = h("div", "head", big, h("div", "", h("h3", "", s.name), h("div", "tags", h("span", "badge", s.type), h("span", "badge", s.role)), pips));
    const fact = (k: string, v: string) => h("div", "", h("span", "", k), h("b", "", v));
    const nextAt = p.lv >= 3 ? "—" : `Lv ${s.unlock + (p.learned ? p.lv + 1 : 0) * 3}`;
    const facts = h("div", "facts", fact("MP cost", s.cost ? String(s.cost) : "—"), fact("Cooldown", s.delay ? (s.delay >= 60000 ? `${s.delay / 60000} min` : `${s.delay / 1000} s`) : "—"),
      fact("Range", s.range ? `${s.range} cells` : "Self / melee"), fact("Unlocks", `Lv ${s.unlock}`), fact("Skill level", p.learned ? `${p.lv} / 3` : "—"), fact("Next level", nextAt));
    const parts: HTMLElement[] = [head, h("div", "desc", s.text || "No description.")];
    parts.push(facts);
    if (p.learned && p.lv < 3) {
      const bar = h("div", "tbar", h("i"));
      (bar.firstChild as HTMLElement).style.width = (p.exp / p.next * 100).toFixed(0) + "%";
      parts.push(h("div", "train", `Training ${p.exp.toLocaleString("en-GB")} / ${p.next.toLocaleString("en-GB")}`, bar));
    }
    // Put it on the HUD bar: F1-F8 (the bar slot this spell sits on is lit; a locked spell cannot be bound).
    const keys = h("div", "keys");
    const at = this.host.keyOf(s.key);
    for (let i = 0; i < 8; i++) {
      const b = h("div", "btn" + (i === at ? " on" : "") + (p.learned && s.type !== "Passive" ? "" : " dis"), "F" + (i + 1));
      b.title = p.learned ? `Put ${s.name} on F${i + 1}` : `Unlocks at level ${s.unlock}`;
      b.addEventListener("click", () => { this.host.assign(i, s); this.renderList(); this.renderDetail(); });
      keys.append(b);
    }
    parts.push(h("div", "", h("div", "lbl", s.type === "Passive" ? "Passive: always on" : "Skill bar key"), keys));
    this.detail.append(...parts);
  }
}
