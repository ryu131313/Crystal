// Refine window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// RefineDialog on the server's rules (RefineSystem.ini defaults in Settings.cs, PlayerObject.RefineItem / CollectRefine /
// CheckRefine): a weapon (OnlyRefineWeapon) and up to 16 ingredients: jewellery with DC / MC / SC and RefineOreName
// (BlackIronOre) with its purity. Cost = weapon level x 10 x RefineCost (125) gold; the weapon stays at the smith for
// RefineTime (20 minutes), then you collect it and have it tested. The refined stat is whichever of the jewellery's
// DC / MC / SC totals is strictly highest (a tie leaves none, and the test smashes the weapon). Success chance, the
// server's formula: item part (stat x 5 - weapon level + 5, 0..10; +10 if the jewellery's average level > weapon level
// - 5; +10 if all jewellery keeps its full max durability; +5 if all is at full durability) + ore part (+15 if ores >=
// jewellery pieces; +15 if average purity >= average stat; +5 if total purity == total stat) + luck (weapon luck + 5,
// 0..10) + BaseChance 20 - the weapon's added DC / MC / SC x RefineWepStatReduce 6 (at most 50); no jewellery stat or no
// ore = 0 %. The test: Random(1, 100) > chance smashes it; otherwise +RefineIncrease 1 to the stat, x RefineCritIncrease
// 2 on a RefineCritChance 10 % crit. A live chance gauge with every part explained, the outcome odds, the forge timer
// (Skip wait in the mock), and a dramatic test (glow, crit flash or shatter). Bag contents, purities, wear and luck are
// mock; item stats and images are real (items.json).

import { findIn, itemData, norm, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface RefineHost { gold(): number; spend(n: number): boolean; say(text: string): void }
const BASE = 20, COST = 125, TIME_MIN = 20, INC = 1, CRIT = 10, CRIT_X = 2, WEP_REDUCE = 6, ORE = "BlackIronOre", ORE_IMG = 284;
/** A bag entry: item name, mock purity (ore), whether it keeps its full max durability / is at full durability, added stats. */
interface Thing { id: number; name: string; purity?: number; fullMax: boolean; fullCur: boolean; added: Record<string, number>; luck: number }
type Phase = "idle" | "forging" | "ready" | "testing" | "done";
const BAG: Omit<Thing, "id">[] = [
  { name: "DragonSlayer", fullMax: true, fullCur: true, added: { MaxDC: 2 }, luck: 1 }, { name: "HeavenSword", fullMax: true, fullCur: true, added: {}, luck: 0 },
  { name: "DragonStaff", fullMax: true, fullCur: true, added: {}, luck: 2 }, { name: "SoulSpringWand", fullMax: true, fullCur: false, added: { MaxSC: 1 }, luck: 0 },
  { name: "GoldDragonRing", fullMax: true, fullCur: true, added: {}, luck: 0 }, { name: "HeroNecklace", fullMax: true, fullCur: true, added: {}, luck: 0 },
  { name: "WhiteGoldNecklace", fullMax: true, fullCur: true, added: {}, luck: 0 }, { name: "WhiteGoldBracelet", fullMax: false, fullCur: true, added: {}, luck: 0 },
  { name: "SoulRing", fullMax: true, fullCur: false, added: {}, luck: 0 }, { name: "RedOrchidRing", fullMax: true, fullCur: true, added: {}, luck: 0 },
  { name: "SmashWheel", fullMax: true, fullCur: true, added: {}, luck: 0 }, { name: "BugNecklace", fullMax: true, fullCur: true, added: {}, luck: 0 },
  { name: "TarragonNecklace", fullMax: true, fullCur: true, added: {}, luck: 0 }, { name: "GoldRing", fullMax: true, fullCur: true, added: {}, luck: 0 },
  ...[14, 12, 11, 9, 7, 5].map((p) => ({ name: ORE, purity: p, fullMax: true, fullCur: true, added: {}, luck: 0 })),
];
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");

const CSS = `
.hudm .rfwin { width: 980px; }
.hudm .rfwin .panel { height: 580px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .rfwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .rfwin .bag { width: 220px; flex: none; padding: 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 6px; }
.hudm .rfwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .rfwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .rfwin .bg { display: grid; grid-template-columns: repeat(5, 38px); gap: 3px; } .hudm .rfwin .bg .eq { width: 38px; height: 38px; }
.hudm .rfwin .eq .pu { position: absolute; left: 2px; top: 1px; z-index: 2; font-size: 9px; font-weight: 800; color: #ffd27a; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .rfwin .eq.used { opacity: .25; pointer-events: none; }
.hudm .rfwin .hint { font-size: 10px; color: var(--dim); line-height: 1.4; }
.hudm .rfwin .forge { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 12px; position: relative;
  background: radial-gradient(ellipse 55% 45% at 50% 62%, rgba(255,120,40,.16), transparent 70%), radial-gradient(ellipse 70% 40% at 50% 20%, color-mix(in srgb, var(--ac) 10%, transparent), transparent 70%); }
.hudm .rfwin .anvil { position: relative; display: flex; flex-direction: column; align-items: center; gap: 4px; }
.hudm .rfwin .anvil .eq { width: 72px; height: 72px; border-radius: 50%; border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 18px var(--glow); }
.hudm .rfwin .anvil .eq::after { border-radius: 50%; }
.hudm .rfwin .anvil .eq.empty { border-style: dashed; box-shadow: none; }
.hudm .rfwin .anvil b { font-size: 15px; color: #fff; } .hudm .rfwin .anvil small { font-size: 11px; color: var(--dim); }
.hudm .rfwin .anvil .glow { position: absolute; top: -8px; width: 92px; height: 92px; border-radius: 50%; pointer-events: none; }
.hudm .rfwin .anvil.forging .glow { background: radial-gradient(circle, rgba(255,140,40,.55), transparent 65%); animation: hudm-rfheat 1.2s ease-in-out infinite; }
@keyframes hudm-rfheat { 50% { transform: scale(1.12); opacity: .7; } }
.hudm .rfwin .anvil.win .eq { animation: hudm-rfwin 1.2s ease-out; } @keyframes hudm-rfwin { 30% { box-shadow: 0 0 0 1px #000, 0 0 50px #6fe08a; transform: scale(1.15); } }
.hudm .rfwin .anvil.crit .eq { animation: hudm-rfcrit 1.4s ease-out; } @keyframes hudm-rfcrit { 20%, 50% { box-shadow: 0 0 0 1px #000, 0 0 70px #f2c94c; transform: scale(1.25); } }
.hudm .rfwin .anvil.smash .eq { animation: hudm-rfsmash .9s ease-in forwards; } @keyframes hudm-rfsmash { 30% { transform: rotate(-8deg) scale(1.05); } 100% { transform: scale(.2) rotate(30deg); opacity: 0; filter: grayscale(1); } }
.hudm .rfwin .cru { display: grid; grid-template-columns: repeat(8, 40px); gap: 4px; padding: 10px; border-radius: var(--rad); border: 1px solid rgba(255,140,40,.4);
  background: linear-gradient(180deg, rgba(255,120,40,.08), rgba(0,0,0,.3)); box-shadow: inset 0 0 20px rgba(255,100,30,.15); }
.hudm .rfwin .cru .eq { width: 40px; height: 40px; }
.hudm .rfwin .stat3 { display: flex; gap: 8px; }
.hudm .rfwin .stat3 div { min-width: 70px; text-align: center; padding: 5px 8px; border-radius: var(--rad); border: 1px solid var(--fr); font-size: 10px; color: var(--dim); }
.hudm .rfwin .stat3 b { display: block; font-size: 18px; color: #fff; } .hudm .rfwin .stat3 div.win { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: 0 0 10px var(--glow); }
.hudm .rfwin .stat3 div.win b { color: var(--ac); } .hudm .rfwin .stat3 div.tie { border-color: #e05555; }
.hudm .rfwin .warn { font-size: 11px; color: #ff8a6a; text-align: center; min-height: 14px; }
.hudm .rfwin .side { width: 320px; flex: none; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .rfwin .side > * { flex-shrink: 0; }
.hudm .rfwin .gauge { position: relative; width: 150px; height: 84px; margin: 0 auto; }
.hudm .rfwin .gauge svg { width: 150px; height: 84px; } .hudm .rfwin .gauge path { fill: none; stroke-width: 12; stroke-linecap: round; }
.hudm .rfwin .gauge .bg { stroke: #05070c; } .hudm .rfwin .gauge .fg { stroke: url(#hudm-rfgrad); filter: drop-shadow(0 0 4px var(--glow)); transition: stroke-dashoffset .5s; }
.hudm .rfwin .gauge div { position: absolute; left: 0; right: 0; bottom: 0; text-align: center; } .hudm .rfwin .gauge b { font-size: 26px; color: #fff; } .hudm .rfwin .gauge small { display: block; font-size: 9px; letter-spacing: .14em; color: var(--dim); }
.hudm .rfwin .part { display: grid; grid-template-columns: 1fr 110px 34px; gap: 8px; align-items: center; font-size: 11px; padding: 2px 0; }
.hudm .rfwin .part span { color: var(--tx); } .hudm .rfwin .part small { display: block; font-size: 9px; color: var(--dim); }
.hudm .rfwin .part .bar { height: 6px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .rfwin .part .bar i { display: block; height: 100%; background: var(--ac); }
.hudm .rfwin .part.neg .bar i { background: #e05555; } .hudm .rfwin .part b { text-align: right; font-variant-numeric: tabular-nums; color: #fff; } .hudm .rfwin .part.neg b { color: #ff7a6a; }
.hudm .rfwin .odds { display: flex; height: 14px; border-radius: 7px; overflow: hidden; border: 1px solid var(--fr); }
.hudm .rfwin .odds i { display: block; height: 100%; } .hudm .rfwin .odds .w { background: #4fbf6a; } .hudm .rfwin .odds .c { background: #f2c94c; } .hudm .rfwin .odds .s { background: #b33; }
.hudm .rfwin .ol { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); } .hudm .rfwin .ol b { color: #fff; }
.hudm .rfwin .go { height: 38px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: var(--rad); font-size: 14px; font-weight: 800; letter-spacing: .08em; color: #fff;
  border: 1px solid #ff8c3a; background: linear-gradient(180deg, rgba(255,140,58,.5), rgba(160,60,10,.6)); box-shadow: 0 0 0 1px #000, 0 0 14px rgba(255,140,58,.4); }
.hudm .rfwin .go:hover { filter: brightness(1.15); } .hudm .rfwin .go.dis { opacity: .4; pointer-events: none; }
.hudm .rfwin .go.test { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 40%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 14px var(--glow); }
.hudm .rfwin .timer { text-align: center; font-size: 12px; color: var(--dim); } .hudm .rfwin .timer b { display: block; font-size: 22px; color: #ffb36a; font-variant-numeric: tabular-nums; }
.hudm .rfwin .row { display: flex; gap: 6px; } .hudm .rfwin .row .btn { flex: 1; }
.hudm .rfwin .res { text-align: center; font-size: 14px; font-weight: 800; padding: 8px; border-radius: var(--rad); }
.hudm .rfwin .res.win { color: #6fe08a; background: rgba(80,200,110,.1); } .hudm .rfwin .res.crit { color: #f2c94c; background: rgba(242,201,76,.12); } .hudm .rfwin .res.smash { color: #ff7a6a; background: rgba(220,60,60,.12); }
.hudm .rfwin .log { font-size: 11px; color: var(--dim); } .hudm .rfwin .log div { padding: 2px 0; }
.hudm .rfwin .shard { position: absolute; z-index: 9; width: 8px; height: 3px; background: #aab; pointer-events: none; animation: hudm-rfshard 1s ease-out forwards; }
@keyframes hudm-rfshard { to { transform: translate(var(--dx), var(--dy)) rotate(var(--r)); opacity: 0; } }
.hudm .rfwin .ember { position: absolute; z-index: 1; width: 3px; height: 3px; border-radius: 50%; background: #ffb36a; box-shadow: 0 0 6px #ff8c3a; pointer-events: none; animation: hudm-rfember 2.4s linear infinite; }
@keyframes hudm-rfember { from { transform: translateY(0); opacity: 1; } to { transform: translateY(-160px) translateX(var(--sx)); opacity: 0; } }
`;

export class RefineMock extends HudWindow {
  private items = new Map<string, Item>();
  private bag: Thing[] = BAG.map((b, i) => ({ ...b, id: i + 1 }));
  private weapon: Thing | null = null;
  private crucible: Thing[] = [];
  private phase: Phase = "idle";
  private readyAt = 0;
  private chanceLocked = 0;
  private statLocked = "";
  private outcome: "" | "win" | "crit" | "smash" = "";
  private log: string[] = [];

  constructor(private readonly host: RefineHost) {
    super("rfwin", 20, 30);
    injectCss("hudm-refine-css", CSS);
    void itemData().then((d) => { if (d) for (const i of d.items) this.items.set(norm(i.name), i); if (this.open) this.render(); });
    window.setInterval(() => {
      if (this.phase !== "forging") return;
      if (Date.now() >= this.readyAt) { this.phase = "ready"; this.host.say(`Your ${this.weapon?.name} is ready to collect.`); if (this.open) this.render(); return; }
      const t = this.el.querySelector(".timer b"); if (t) t.textContent = this.clock();
    }, 1000);
  }
  protected override shown(): void { this.render(); }
  private item(n: string): Item | undefined { return findIn(this.items, n); }
  private clock(): string { const s = Math.max(0, Math.ceil((this.readyAt - Date.now()) / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`; }

  /** PlayerObject.RefineItem's tally and formula. */
  private calc() {
    const w = this.weapon, wi = w && this.item(w.name);
    let dc = 0, mc = 0, sc = 0, lv = 0, items = 0, fullMax = 0, fullCur = 0, ores = 0, purity = 0;
    for (const t of this.crucible) {
      const it = this.item(t.name);
      if (t.name === ORE) { ores++; purity += t.purity ?? 0; continue; }
      if (!it) continue;
      const s = it.stats;
      if ((s.MaxDC ?? 0) > 0 || (s.MaxMC ?? 0) > 0 || (s.MaxSC ?? 0) > 0) {
        dc += (s.MinDC ?? 0) + (s.MaxDC ?? 0) + (t.added.MaxDC ?? 0); mc += (s.MinMC ?? 0) + (s.MaxMC ?? 0) + (t.added.MaxMC ?? 0); sc += (s.MinSC ?? 0) + (s.MaxSC ?? 0) + (t.added.MaxSC ?? 0);
        lv += it.req; items++; if (t.fullMax) fullMax++; if (t.fullCur) fullCur++;
      }
    }
    const stat = dc > mc && dc > sc ? "DC" : mc > dc && mc > sc ? "MC" : sc > dc && sc > mc ? "SC" : "";
    const val = stat === "DC" ? dc : stat === "MC" ? mc : stat === "SC" ? sc : 0;
    const wlv = wi?.req ?? 0;
    const parts: [string, string, number, number][] = [];
    let zero = "";
    if (!dc && !mc && !sc) zero = "Add jewellery with DC, MC or SC.";
    else if (!ores) zero = `Add ${ORE}: without ore the refine always fails.`;
    let itemS = Math.max(0, Math.min(10, val * 5 - wlv + 5));
    // Integer averages, like the server (short / byte division in RefineItem).
    const avgLv = items ? Math.floor(lv / items) : 0, avgPur = ores ? Math.floor(purity / ores) : 0, avgStat = items ? Math.floor(val / items) : 0;
    const lvB = items && avgLv > wlv - 5 ? 10 : 0, maxB = items && fullMax === items ? 10 : 0, curB = items && fullCur === items ? 5 : 0;
    const oreA = ores >= items && items ? 15 : 0, oreP = ores && items && avgPur >= avgStat ? 15 : 0, oreE = ores && purity === val ? 5 : 0;
    const luck = Math.max(0, Math.min(10, (w?.luck ?? 0) + 5));
    const added = (w?.added.MaxDC ?? 0) + (w?.added.MaxMC ?? 0) + (w?.added.MaxSC ?? 0);
    const penalty = Math.min(50, added * WEP_REDUCE);
    parts.push([`${stat || "Stat"} strength`, `stat ${val} × 5 − weapon Lv ${wlv} + 5`, itemS, 10], ["Jewellery level", `avg Lv ${avgLv} > ${wlv - 5}`, lvB, 10],
      ["Jewellery max durability", `${fullMax}/${items} untouched`, maxB, 10], ["Jewellery durability", `${fullCur}/${items} full`, curB, 5],
      ["Ore amount", `${ores} ore ≥ ${items} jewellery`, oreA, 15], ["Ore purity", `avg ${avgPur} ≥ avg stat ${avgStat} (whole numbers)`, oreP, 15],
      ["Exact purity", `purity ${purity} = stat ${val}`, oreE, 5], ["Luck", `weapon luck ${w?.luck ?? 0} + 5`, luck, 10], ["Base chance", "BaseChance", BASE, 20]);
    if (penalty) parts.push(["Already refined", `+${added} added × ${WEP_REDUCE}`, -penalty, 50]);
    itemS = itemS + lvB + maxB + curB;
    const chance = zero ? 0 : Math.max(0, itemS + oreA + oreP + oreE + luck + BASE - penalty);
    return { dc, mc, sc, stat, val, parts, chance, zero, cost: wlv * 10 * COST, wlv };
  }

  private render(): void {
    const c = this.calc();
    const busy = this.phase !== "idle";
    // Bag
    const bg = h("div", "bg");
    for (const t of this.bag) {
      const it = this.item(t.name);
      const used = this.weapon === t || this.crucible.includes(t);
      const cell = itemCell(this.el, t.name === ORE ? `${ORE} (purity ${t.purity})` : t.name, it?.img ?? (t.name === ORE ? ORE_IMG : -1));
      if (t.purity) cell.append(h("span", "pu", String(t.purity)));
      if (used) cell.classList.add("used");
      cell.addEventListener("click", () => this.put(t));
      bg.append(cell);
    }
    const bag = h("div", "bag", h("div", "lbl", "Your bag", h("b", "", "click to add")), bg,
      h("div", "hint", "Weapons go on the anvil. Jewellery with DC / MC / SC and Black Iron Ore (number = purity) go in the crucible. Click a slot to take an item back."));
    // Forge
    const w = this.weapon, wi = w && this.item(w.name);
    const wc = itemCell(this.el, w ? w.name : "Weapon", wi?.img ?? -1);
    if (w && !busy) wc.addEventListener("click", () => { this.weapon = null; this.outcome = ""; this.render(); });
    const added = w ? Object.entries(w.added).map(([k, v]) => `+${v} ${k.replace("Max", "")}`).join(" ") : "";
    const anvil = h("div", "anvil" + (this.phase === "forging" ? " forging" : "") + (this.outcome ? " " + this.outcome : ""), h("div", "glow"), wc,
      h("b", "", w ? w.name : "Put a weapon on the anvil"), h("small", "", w ? `Level ${c.wlv}${added ? " · refined " + added : ""}${w.luck ? " · luck +" + w.luck : ""}` : "Only weapons can be refined"));
    const cru = h("div", "cru");
    for (let i = 0; i < 16; i++) {
      const t = this.crucible[i];
      const it = t && this.item(t.name);
      const cell = itemCell(this.el, t ? (t.name === ORE ? `${ORE} (purity ${t.purity})` : t.name) : "", t ? it?.img ?? (t.name === ORE ? ORE_IMG : -1) : -1);
      if (t?.purity) cell.append(h("span", "pu", String(t.purity)));
      if (t && !busy) { cell.classList.remove("empty"); cell.addEventListener("click", () => { this.crucible.splice(i, 1); this.render(); }); }
      cru.append(cell);
    }
    const st = h("div", "stat3");
    for (const [k, v] of [["DC", c.dc], ["MC", c.mc], ["SC", c.sc]] as const) {
      const tie = !c.stat && v > 0 && v === Math.max(c.dc, c.mc, c.sc);
      st.append(h("div", c.stat === k ? "win" : tie ? "tie" : "", h("b", "", String(v)), `${k} from jewellery`));
    }
    const warn = !w || busy ? "" : c.zero || (!c.stat && (c.dc || c.mc || c.sc) ? "DC, MC and SC are tied: nothing will be refined and the test will smash the weapon." : "");
    const forge = h("div", "forge", anvil, h("div", "lbl", "Crucible", h("b", "", `${this.crucible.length} / 16`)), cru, st, h("div", "warn", warn));
    if (this.phase === "forging") for (let i = 0; i < 14; i++) { const e = h("div", "ember"); Object.assign(e.style, { left: 40 + Math.random() * 20 + "%", top: "38%", animationDelay: (i * 0.17).toFixed(2) + "s" }); e.style.setProperty("--sx", (Math.random() * 60 - 30).toFixed(0) + "px"); forge.append(e); }
    this.el.replaceChildren(h("div", "panel", this.titleBar("REFINE", "Close"), h("div", "body", bag, forge, this.side(c))));
  }

  private side(c: ReturnType<RefineMock["calc"]>): HTMLElement {
    const s = h("div", "side");
    const chance = this.phase === "idle" ? c.chance : this.chanceLocked;
    const gauge = h("div", "gauge");
    const L = Math.PI * 64;
    gauge.innerHTML = `<svg viewBox="0 0 150 84"><defs><linearGradient id="hudm-rfgrad"><stop offset="0" stop-color="#e05555"/><stop offset=".5" stop-color="#f2c94c"/><stop offset="1" stop-color="#6fe08a"/></linearGradient></defs>
      <path class="bg" d="M11 78 A64 64 0 0 1 139 78"/><path class="fg" d="M11 78 A64 64 0 0 1 139 78" stroke-dasharray="${L}" stroke-dashoffset="${L * (1 - chance / 100)}"/></svg>`;
    gauge.append(h("div", "", h("b", "", `${chance}%`), h("small", "", "SUCCESS CHANCE")));
    s.append(gauge);
    const win = chance * (100 - CRIT) / 100, crit = chance * CRIT / 100, smash = 100 - chance;
    const odds = h("div", "odds"); for (const [cls, v] of [["w", win], ["c", crit], ["s", smash]] as const) { const i = h("i", cls); i.style.width = v + "%"; odds.append(i); }
    const stat = (this.phase === "idle" ? c.stat : this.statLocked) || "stat";
    s.append(h("div", "lbl", "Outcome"), odds, h("div", "ol", h("span", "", h("b", "", `${win.toFixed(0)}%`), ` +${INC} ${stat}`), h("span", "", h("b", "", `${crit.toFixed(0)}%`), ` crit +${INC * CRIT_X}`), h("span", "", h("b", "", `${smash.toFixed(0)}%`), " smashed")));
    const gold = this.host.gold();
    if (this.phase === "idle") {
      const ok = !!this.weapon && gold >= c.cost;
      const go = h("div", "go" + (ok ? "" : " dis"), this.weapon ? `REFINE · ${fmt(c.cost)} GOLD` : "REFINE");
      go.addEventListener("click", () => this.start(c));
      s.append(go, h("div", "hint", `Cost = weapon level × 10 × ${COST}. The ingredients are used up. The weapon stays at the smith for ${TIME_MIN} minutes.`));
    } else if (this.phase === "forging") {
      const skip = h("div", "btn", "Skip wait (mock)"); skip.addEventListener("click", () => { this.readyAt = Date.now(); });
      s.append(h("div", "timer", h("b", "", this.clock()), `Refining ${this.weapon?.name}…`), h("div", "row", skip));
    } else if (this.phase === "ready") {
      const t = h("div", "go test", "COLLECT & TEST"); t.addEventListener("click", () => this.test());
      s.append(h("div", "timer", h("b", "", "Ready"), "Collect it and have the smith test the refine."), t);
    } else if (this.phase === "done") {
      const txt = this.outcome === "smash" ? `${this.log[0]}` : this.log[0]!;
      s.append(h("div", "res " + this.outcome, txt));
      const again = h("div", "btn", "Refine another"); again.addEventListener("click", () => { this.phase = "idle"; this.outcome = ""; if (!this.bag.includes(this.weapon!)) this.weapon = null; this.render(); });
      s.append(h("div", "row", again));
    }
    if (this.phase === "idle") {
      s.append(h("div", "lbl", "How it adds up", h("b", "", "server formula")));
      for (const [n, why, v, max] of c.parts) {
        const bar = h("div", "bar", h("i")); (bar.firstChild as HTMLElement).style.width = Math.abs(v) / max * 100 + "%";
        s.append(h("div", "part" + (v < 0 ? " neg" : ""), h("span", "", n, h("small", "", why)), bar, h("b", "", (v > 0 ? "+" : "") + v)));
      }
    }
    if (this.log.length > 1) { const lg = h("div", "log"); for (const l of this.log.slice(1, 6)) lg.append(h("div", "", l)); s.append(h("div", "lbl", "Earlier"), lg); }
    return s;
  }

  private put(t: Thing): void {
    if (this.phase !== "idle") return;
    if (this.weapon === t || this.crucible.includes(t)) return;
    const it = this.item(t.name);
    if (it?.type === "Weapon") { this.weapon = t; this.outcome = ""; }
    else if (this.crucible.length < 16) this.crucible.push(t);
    this.render();
  }
  private start(c: ReturnType<RefineMock["calc"]>): void {
    if (!this.weapon || !this.host.spend(c.cost)) return;
    this.chanceLocked = c.chance; this.statLocked = c.stat;
    for (const t of this.crucible) this.bag.splice(this.bag.indexOf(t), 1);
    this.crucible = [];
    this.phase = "forging"; this.readyAt = Date.now() + TIME_MIN * 60000;
    this.host.say(`${this.weapon.name} is being refined. Come back in ${TIME_MIN} minutes.`);
    this.render();
  }
  /** CollectRefine + CheckRefine. */
  private test(): void {
    const w = this.weapon!;
    this.phase = "testing"; this.render();
    window.setTimeout(() => {
      const fail = !this.statLocked || 1 + Math.floor(Math.random() * 99) > this.chanceLocked;
      const crit = 1 + Math.floor(Math.random() * 99) < CRIT;
      if (fail) {
        this.outcome = "smash"; this.bag.splice(this.bag.indexOf(w), 1);
        this.log.unshift(`💥 ${w.name} was smashed in the test.`);
      } else {
        const add = INC * (crit ? CRIT_X : 1), k = "Max" + this.statLocked;
        w.added[k] = (w.added[k] ?? 0) + add;
        this.outcome = crit ? "crit" : "win";
        this.log.unshift(`${crit ? "⭐ Critical! " : "✓ "}${w.name} gained +${add} ${this.statLocked}.`);
      }
      this.host.say(this.log[0]!);
      this.phase = "done"; this.render();
      if (fail) this.shatter();
    }, 1400);
  }
  private shatter(): void {
    const a = this.el.querySelector<HTMLElement>(".anvil .eq"), panel = this.el.querySelector<HTMLElement>(".panel");
    if (!a || !panel) return;
    const pr = panel.getBoundingClientRect(), r = a.getBoundingClientRect(), k = pr.width / panel.offsetWidth || 1;
    for (let i = 0; i < 24; i++) {
      const s = h("div", "shard"), ang = Math.random() * Math.PI * 2, d = 40 + Math.random() * 120;
      Object.assign(s.style, { left: (r.left + r.width / 2 - pr.left) / k + "px", top: (r.top + r.height / 2 - pr.top) / k + "px" });
      s.style.setProperty("--dx", Math.cos(ang) * d + "px"); s.style.setProperty("--dy", Math.sin(ang) * d + 40 + "px"); s.style.setProperty("--r", Math.random() * 720 + "deg");
      panel.append(s);
    }
    window.setTimeout(() => { for (const s of panel.querySelectorAll(".shard")) s.remove(); }, 1100);
  }
}
