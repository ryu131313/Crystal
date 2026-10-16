// Inspect window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// InspectDialog (another player's name, guild, lover, paperdoll and equipment cells; Ctrl + right-click a player). The
// equipment is real ItemInfo from the DB copy (play/lab-data/items.json, shared with the item tooltip, so every slot has
// the full tooltip); the paperdoll is the Character window's (`dollInto`: base body, armour, weapon, helmet from
// StateItem); set bonuses are counted from the pieces worn (ItemSets.Amount, HumanObject's set stats). Tabs: Stats (the
// totals of their gear's stats beside yours, with bars), Compare (slot by slot against your gear, hudCharacter.ts) and
// Profile. Gear score is a mock formula (stats weighted by grade). Opened from the chat's name menu (Inspect) or the
// lab's Simulate > Inspect button. The three players and their profiles are mock.

import { EQUIPPED_NAMES, dollInto } from "./hudCharacter.ts";
import { findIn, GRADE, itemData, norm, SET_INFO, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface InspectHost { whisper(name: string): void; say(text: string): void }
/** Slots in the Character window's order: left column, right column, bottom row. */
const SLOTS = ["Weapon", "Armour", "Torch", "Bracelet", "Ring", "Helmet", "Necklace", "Amulet", "Bracelet", "Ring", "Belt", "Boots", "Stone", "Mount"];
interface Player { name: string; cls: string; female: boolean; level: number; guild: string; rank: string; lover: string; title: string; where: string; pk: number; played: string; gear: string[]; badges: string[] }
const PLAYERS: Player[] = [
  { name: "Garrick", cls: "Warrior", female: false, level: 55, guild: "Nightfall", rank: "Leader", lover: "", title: "Ranked #4 overall", where: "Woomyon Woods", pk: 0, played: "41 days",
    gear: ["DragonSlayer", "BlackTigerArmour(M)", "DragonTorch", "TarragonBracelet", "TarragonRing", "TarragonHelmet", "TarragonNecklace", "", "TarragonBracelet", "GoldDragonRing", "TarragonBelt", "TarragonBoots", "PowerStone(M)", "WhiteTiger"],
    badges: ["Guild leader", "Zuma slayer", "Level 55", "Tarragon set"] },
  { name: "Seren", cls: "Archer", female: true, level: 31, guild: "GameMaster", rank: "Officer", lover: "GMRyu", title: "Ranked #31 overall", where: "Bichon Province", pk: 0, played: "12 days",
    gear: ["CompoundBow", "PearlArmour(F)", "Candle", "SpiritBracelet", "SpiritRing", "SpiritHelmet", "SpiritNecklace", "", "SpiritBracelet", "SpiritRing", "WhiteGoldBelt(H)", "WhiteGoldShoes(H)", "HealthStone(M)", "BengalTiger"],
    badges: ["Married", "Spirit set", "Explorer"] },
  { name: "Elowen", cls: "Taoist", female: true, level: 61, guild: "Nightfall", rank: "Officer", lover: "", title: "Ranked #2 Taoist", where: "Bichon Wall", pk: 120, played: "66 days",
    gear: ["SoulSpringWand", "TarragonArmour(F)", "DragonTorch", "RedOrchidBracelet", "RedOrchidRing", "MirHelmet", "RedOrchidNecklace", "AmuletOfRevival", "BugBracelet", "BugRing", "MirBelt", "MirBoots", "PowerStone(M)", "WhiteTiger"],
    badges: ["Healer", "Red Orchid set", "PvP veteran"] },
];
const GRADE_MULT: Record<string, number> = { None: 1, Common: 1, Rare: 1.25, Legendary: 1.6, Mythical: 2, Heroic: 2.5 };
/** Mock gear score: stats weighted (max range values 1, min 0.5, HP / MP 0.1, other stats 2) times the grade. */
function score(it: Item | undefined): number {
  if (!it) return 0;
  let s = 0;
  for (const [k, v] of Object.entries(it.stats)) s += v * (/^Max/.test(k) ? 1 : /^Min/.test(k) ? 0.5 : k === "HP" || k === "MP" ? 0.1 : 2);
  return Math.round((s + it.req * 0.3) * (GRADE_MULT[it.grade] ?? 1));
}
const ROWS: [string, string, string][] = [["AC", "MinAC", "MaxAC"], ["MAC", "MinMAC", "MaxMAC"], ["DC", "MinDC", "MaxDC"], ["MC", "MinMC", "MaxMC"], ["SC", "MinSC", "MaxSC"]];
const EXTRA = ["Accuracy", "Agility", "AttackSpeed", "HP", "MP", "Holy", "Luck", "MagicResist", "PoisonResist", "Reflect", "CriticalRate", "HPDrainRatePercent"];
const label = (k: string) => ({ AttackSpeed: "Attack speed", MagicResist: "Magic resist", PoisonResist: "Poison resist", CriticalRate: "Critical rate", HPDrainRatePercent: "HP drain %" } as Record<string, string>)[k] ?? k;
const CLASS_TAG: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };

const CSS = `
.hudm .inwin { width: 900px; }
.hudm .inwin .panel { height: 560px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .inwin .ban { display: flex; align-items: center; gap: 14px; padding: 10px 16px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 16%, transparent), transparent 60%); }
.hudm .inwin .ban .cl { width: 46px; height: 46px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800; color: #fff; border: 2px solid var(--ac);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); box-shadow: 0 0 0 2px #000, 0 0 12px var(--glow); }
.hudm .inwin .ban .nm { flex: 1; } .hudm .inwin .ban .nm b { font-size: 20px; color: #fff; } .hudm .inwin .ban .nm b.pk { color: #ff6a6a; }
.hudm .inwin .ban .nm small { display: block; font-size: 11px; color: var(--dim); } .hudm .inwin .ban .nm small em { font-style: normal; color: var(--ac); }
.hudm .inwin .gs { text-align: center; } .hudm .inwin .gs b { display: block; font-size: 26px; line-height: 1; color: #f2c94c; text-shadow: 0 0 10px rgba(242,201,76,.5); font-variant-numeric: tabular-nums; }
.hudm .inwin .gs small { font-size: 9px; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); } .hudm .inwin .gs i { display: block; font-style: normal; font-size: 10px; }
.hudm .inwin .gs i.up { color: #6fe08a; } .hudm .inwin .gs i.dn { color: #ff7a6a; }
.hudm .inwin .pick { display: flex; gap: 4px; } .hudm .inwin .pick div { padding: 3px 9px; border-radius: 10px; cursor: pointer; font-size: 11px; border: 1px solid var(--fr); color: var(--dim); }
.hudm .inwin .pick div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .inwin .ibody { flex: 1; min-height: 0; display: flex; }
.hudm .inwin .doll { width: 360px; flex: none; position: relative; padding: 10px 12px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 8px; }
.hudm .inwin .drow { position: relative; height: 262px; display: flex; justify-content: space-between; }
.hudm .inwin .drow .col { display: flex; flex-direction: column; gap: 4px; z-index: 1; }
.hudm .inwin .figure { position: absolute; left: 52px; right: 52px; top: 0; bottom: 0; overflow: hidden; border-radius: var(--rad);
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%), radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .inwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; } .hudm .inwin .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .inwin .bottom { display: flex; justify-content: center; gap: 4px; }
.hudm .inwin .eq { position: relative; }
.hudm .inwin .eq .gl { position: absolute; inset: 0; border-radius: var(--rad); pointer-events: none; box-shadow: inset 0 0 0 1px var(--g), inset 0 0 8px color-mix(in srgb, var(--g) 50%, transparent); }
.hudm .inwin .sets { display: flex; flex-wrap: wrap; gap: 4px; justify-content: center; }
.hudm .inwin .sets span { font-size: 10px; padding: 2px 8px; border-radius: 9px; border: 1px solid var(--fr); color: var(--dim); }
.hudm .inwin .sets span.on { color: #6fe08a; border-color: #4a9a5a; background: rgba(80,200,110,.1); box-shadow: 0 0 6px rgba(80,200,110,.3); }
.hudm .inwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .inwin .tabs { display: flex; gap: 2px; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .inwin .tabs div { padding: 6px 12px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .inwin .tabs div.on { color: var(--ac); border-bottom-color: var(--ac); }
.hudm .inwin .pane { flex: 1; min-height: 0; overflow-y: auto; padding: 10px 14px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .inwin .sr { display: grid; grid-template-columns: 96px 70px 1fr 70px; align-items: center; gap: 8px; padding: 4px 0; font-size: 12px; }
.hudm .inwin .sr.hd { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .inwin .sr span:first-child { color: var(--dim); } .hudm .inwin .sr b { color: #fff; font-variant-numeric: tabular-nums; text-align: right; } .hudm .inwin .sr b.me { text-align: left; color: var(--ac); }
.hudm .inwin .vs { position: relative; height: 8px; display: flex; } .hudm .inwin .vs i { height: 100%; } .hudm .inwin .vs .t { margin-left: auto; background: linear-gradient(90deg, transparent, #f2c94c); border-radius: 4px 0 0 4px; }
.hudm .inwin .vs .m { background: linear-gradient(90deg, var(--ac), transparent); border-radius: 0 4px 4px 0; } .hudm .inwin .vs::after { content: ""; position: absolute; left: 50%; top: -2px; bottom: -2px; width: 1px; background: var(--fr); }
.hudm .inwin .vs .h { width: 50%; display: flex; } .hudm .inwin .vs .h.r { justify-content: flex-start; }
.hudm .inwin .cr { display: grid; grid-template-columns: 70px 1fr 40px 1fr; align-items: center; gap: 8px; padding: 3px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .inwin .cr > span:first-child { color: var(--dim); } .hudm .inwin .cr .c { display: flex; align-items: center; gap: 6px; min-width: 0; }
.hudm .inwin .cr .c .eq { width: 30px; height: 30px; } .hudm .inwin .cr .c span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .inwin .cr .d { text-align: center; font-weight: 700; font-size: 11px; } .hudm .inwin .cr .d.up { color: #f2c94c; } .hudm .inwin .cr .d.dn { color: var(--ac); }
.hudm .inwin .cr.hd { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .inwin .pf { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .inwin .pf div { padding: 8px 10px; border-radius: var(--rad); font-size: 11px; color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .inwin .pf b { display: block; font-size: 14px; color: #fff; }
.hudm .inwin .bdg { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 10px; }
.hudm .inwin .bdg span { font-size: 11px; padding: 3px 10px; border-radius: 10px; color: #fff; border: 1px solid var(--ac); background: color-mix(in srgb, var(--ac) 18%, transparent); }
.hudm .inwin .acts { display: flex; gap: 6px; padding: 8px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .inwin .acts .btn { flex: 1; } .hudm .inwin .btn.imain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); }
.hudm .inwin .btn.rep:hover { border-color: #e05555; }
.hudm .inwin .lbl { margin: 10px 0 4px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
`;

export class InspectMock extends HudWindow {
  private items = new Map<string, Item>();
  private who: Player = PLAYERS[0]!;
  private tab: "Stats" | "Compare" | "Profile" = "Stats";
  private figure: HTMLElement | null = null;
  private dollFor = "";

  constructor(private readonly host: InspectHost) {
    super("inwin", 50, 40);
    injectCss("hudm-inspect-css", CSS);
    void itemData().then((d) => { if (d) for (const i of d.items) this.items.set(norm(i.name), i); if (this.open) this.render(); });
  }
  static readonly names = PLAYERS.map((p) => p.name);
  protected override shown(): void { this.render(); }

  /** Inspect a player by name; false when they are not one of the mock players. */
  show(name: string): boolean {
    const p = PLAYERS.find((x) => x.name.toLowerCase() === name.toLowerCase());
    if (!p) return false;
    this.who = p; this.tab = "Stats";
    this.toggle(true); this.render();
    return true;
  }

  private item(n: string): Item | undefined { return n ? findIn(this.items, n) : undefined; }
  private totals(gear: string[]): Record<string, number> {
    const t: Record<string, number> = {};
    for (const n of gear) for (const [k, v] of Object.entries(this.item(n)?.stats ?? {})) t[k] = (t[k] ?? 0) + v;
    return t;
  }
  private gearScore(gear: string[]): number { return gear.reduce((s, n) => s + score(this.item(n)), 0); }

  private render(): void {
    const p = this.who;
    const mine = this.gearScore(EQUIPPED_NAMES), theirs = this.gearScore(p.gear), d = theirs - mine;
    const pick = h("div", "pick");
    for (const x of PLAYERS) { const e = h("div", x === p ? "on" : "", x.name); e.addEventListener("click", () => this.show(x.name)); pick.append(e); }
    const ban = h("div", "ban", h("div", "cl", CLASS_TAG[p.cls] ?? "?"),
      h("div", "nm", h("b", p.pk >= 100 ? "pk" : "", p.name), h("small", "", `Level ${p.level} ${p.cls} · `, h("em", "", `${p.guild}`), ` <${p.rank}>${p.lover ? " · ♥ " + p.lover : ""}`),
        h("small", "", `${p.title} · ${p.where}${p.pk >= 100 ? " · PK " + p.pk : ""}`)),
      pick, h("div", "gs", h("b", "", String(theirs)), h("small", "", "Gear score"), h("i", d >= 0 ? "up" : "dn", `${d >= 0 ? "+" : ""}${d} vs you`)));
    const tabs = h("div", "tabs");
    for (const t of ["Stats", "Compare", "Profile"] as const) { const e = h("div", this.tab === t ? "on" : "", t.toUpperCase()); e.addEventListener("click", () => { this.tab = t; this.render(); }); tabs.append(e); }
    const pane = h("div", "pane");
    if (this.tab === "Stats") this.stats(pane); else if (this.tab === "Compare") this.compare(pane); else this.profile(pane);
    const act = (t: string, cls: string, fn: () => void) => { const b = h("div", "btn " + cls, t); b.addEventListener("click", fn); return b; };
    const acts = h("div", "acts", act("Whisper", "imain", () => this.host.whisper(p.name)), act("Trade", "", () => this.host.say(`Trade request sent to ${p.name}.`)),
      act("Group invite", "", () => this.host.say(`Group invite sent to ${p.name}.`)), act("Add friend", "", () => this.host.say(`${p.name} was added to your friends.`)),
      act("Report", "rep", () => this.host.say(`Report about ${p.name} sent to the GMs (mock).`)));
    this.el.replaceChildren(h("div", "panel", this.titleBar("INSPECT", "Close"), ban, h("div", "ibody", this.doll(), h("div", "main", tabs, pane, acts))));
  }

  private doll(): HTMLElement {
    const p = this.who;
    const cell = (i: number) => {
      const it = this.item(p.gear[i]!);
      const c = itemCell(this.el, it ? `${SLOTS[i]}: ${it.name}` : SLOTS[i]!, it ? it.img : -1);
      if (it && it.grade !== "None" && it.grade !== "Common") { const g = h("div", "gl"); g.style.setProperty("--g", GRADE[it.grade] ?? "transparent"); c.append(g); }
      return c;
    };
    const col = (from: number) => { const c = h("div", "col"); for (let i = from; i < from + 5; i++) c.append(cell(i)); return c; };
    const armour = this.item(p.gear[1]!)?.img ?? -1, weapon = this.item(p.gear[0]!)?.img ?? -1, helmet = this.item(p.gear[5]!)?.img ?? -1;
    if (this.dollFor !== p.name || !this.figure) {
      const org = h("div", "org"), fig = h("div", "figure", org);
      this.figure = fig;
      // Drawn once per player, after the item data has loaded (the figure is kept between renders).
      if (armour >= 0) { this.dollFor = p.name; window.setTimeout(() => void dollInto(fig, org, [armour, weapon, helmet], p.female)); }
    }
    const bottom = h("div", "bottom"); for (let i = 10; i < 14; i++) bottom.append(cell(i));
    // Sets: pieces worn per set against ItemSets.Amount.
    const counts = new Map<string, number>();
    for (const n of p.gear) { const s = this.item(n)?.set; if (s && s !== "None") counts.set(s, (counts.get(s) ?? 0) + 1); }
    const sets = h("div", "sets");
    for (const [s, n] of counts) { const si = SET_INFO[s]; if (!si) continue; const on = n >= si[1]; const e = h("span", on ? "on" : "", `${si[0]} ${Math.min(n, si[1])}/${si[1]}${on ? " ✓" : ""}`); e.title = si[2].map(([k, v]) => `${k} ${v}`).join(", "); sets.append(e); }
    return h("div", "doll", h("div", "drow", col(0), this.figure!, col(5)), bottom, sets);
  }

  private stats(pane: HTMLElement): void {
    const t = this.totals(this.who.gear), m = this.totals(EQUIPPED_NAMES);
    pane.append(h("div", "sr hd", h("span", "", "From gear"), h("b", "", this.who.name), h("span", "", ""), h("b", "me", "You")));
    const row = (name: string, a: number, b: number, ta: string, tb: string) => {
      const max = Math.max(1, a, b);
      const vs = h("div", "vs", h("div", "h", h("i", "t")), h("div", "h r", h("i", "m")));
      (vs.querySelector(".t") as HTMLElement).style.width = a / max * 100 + "%"; (vs.querySelector(".m") as HTMLElement).style.width = b / max * 100 + "%";
      pane.append(h("div", "sr", h("span", "", name), h("b", "", ta), vs, h("b", "me", tb)));
    };
    for (const [n, lo, hi] of ROWS) { const a = [t[lo] ?? 0, t[hi] ?? 0], b = [m[lo] ?? 0, m[hi] ?? 0]; if (a[1] || b[1]) row(n, a[1]!, b[1]!, `${a[0]}–${a[1]}`, `${b[0]}–${b[1]}`); }
    pane.append(h("div", "lbl", "Other"));
    const sg = (v: number) => (v < 0 ? "" : "+") + v;
    for (const k of EXTRA) { const a = t[k] ?? 0, b = m[k] ?? 0; if (a || b) row(label(k), Math.max(0, a), Math.max(0, b), sg(a), sg(b)); }
    pane.append(h("div", "lbl", "Totals of item stats only; set bonuses, levels and buffs are not included."));
  }

  private compare(pane: HTMLElement): void {
    pane.append(h("div", "cr hd", h("span", "", "Slot"), h("span", "", this.who.name), h("span", "", "Δ"), h("span", "", "You")));
    SLOTS.forEach((slot, i) => {
      const a = this.item(this.who.gear[i]!), b = this.item(EQUIPPED_NAMES[i] ?? "");
      const c = (it: Item | undefined) => h("div", "c", itemCell(this.el, it ? `${slot}: ${it.name}` : slot, it ? it.img : -1), h("span", "", it?.name ?? "—"));
      const d = score(a) - score(b);
      pane.append(h("div", "cr", h("span", "", slot), c(a), h("span", "d " + (d > 0 ? "up" : d < 0 ? "dn" : ""), d ? `${d > 0 ? "▲" : "▼"} ${Math.abs(d)}` : "="), c(b)));
    });
    pane.append(h("div", "lbl", "Δ = their gear score minus yours for that slot (gold: theirs is better)."));
  }

  private profile(pane: HTMLElement): void {
    const p = this.who;
    const mount = this.item(p.gear[13]!);
    const pf = h("div", "pf", h("div", "", h("b", "", `${p.guild}`), `<${p.rank}>`), h("div", "", h("b", "", p.lover || "—"), "Partner"),
      h("div", "", h("b", "", p.title.replace(/^Ranked /, "")), "Ranking"), h("div", "", h("b", "", mount?.name ?? "—"), "Mount"),
      h("div", "", h("b", "", String(p.pk)), p.pk >= 100 ? "PK points (red name)" : "PK points"), h("div", "", h("b", "", p.played), "Played"));
    const bdg = h("div", "bdg"); for (const b of p.badges) bdg.append(h("span", "", b));
    pane.append(pf, h("div", "lbl", "Badges"), bdg);
  }
}
