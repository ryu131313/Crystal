// Sockets & gems window mock for the scene lab's UI tester (window style from hudWindow.ts). Two benches:
// Gems & Orbs, the upgrade this server really uses (PlayerObject's gem combine, Settings.GemStatIndependent = true): a gem
// or orb (ItemType.Gem, Shape 3 / 4; the 26 in play/lab-data/items.json) on an item it fits (ValidGemForItem, by the
// gem's Unique flags: Paralize = weapon, Teleport = armour, ClearRing = helmet, Protection = necklace, Revival = bracelet,
// Muscle = ring, ...; the item must have no special mode and no DontUpgrade). The gem's ItemInfo stats hold its rules:
// CriticalRate = base chance, Reflect = minus per point of that stat already added to the item (the item's gem count for
// durability gems), CriticalDamage = most gems per item, HPDrainRatePercent = most of that stat. Chance = base - Reflect x
// added (0 if that reaches the base) + GemRatePercent; success adds the gem's stat; a failed gem (Shape 3, not an orb)
// destroys the item 3 times in 15, otherwise nothing happens; the gem is used either way.
// Sockets, the SocketDialog (UserItem.Slots, socket items = ItemType.Socket; Shape 1 weapon only, 2 armour only, 3
// jewellery only): this server defines no socket items and no item has sockets, so this bench shows the dialog with
// sample sockets and sample jewels (marked as samples): socket them, remove them back to the bag, see the stats they add.
// Your gear's added stats, gem counts and gem amounts are mock. Simulate > Sockets & gems.

import { findIn, gemRules, GRADE, itemData, norm, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface SocketHost { say(text: string): void }
const GEM_RATE = 0; // the player's Stat.GemRatePercent (mock)
const STAT_LABEL: Record<string, string> = { MaxDC: "DC", MaxMC: "MC", MaxSC: "SC", MaxAC: "AC", MaxMAC: "MAC", AttackSpeed: "Attack speed", Agility: "Agility", Accuracy: "Accuracy",
  PoisonAttack: "Poison attack", Freezing: "Freezing", MagicResist: "Magic resist", PoisonResist: "Poison resist", MaxDurability: "Max durability" };
interface Piece { id: number; name: string; added: Record<string, number>; gems: number; gone?: boolean }
/** Sample socket jewels (no ItemType.Socket items exist on this server). */
const JEWELS: [string, string, number, Record<string, number>, number][] = [
  ["Ruby of Fury", "#ff5a5a", 1, { MaxDC: 2 }, 0], ["Sapphire of Wisdom", "#4fa8ff", 1, { MaxMC: 2 }, 0], ["Emerald of Spirit", "#4fe07a", 1, { MaxSC: 2 }, 0],
  ["Topaz of Warding", "#f2c94c", 2, { MaxAC: 3 }, 0], ["Amethyst of Will", "#c08cff", 2, { MaxMAC: 3 }, 0], ["Diamond of Haste", "#e8f4ff", 3, { Accuracy: 1, Agility: 1 }, 0],
];

const CSS = `
.hudm .skwin { width: 1000px; }
.hudm .skwin .panel { height: 590px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .skwin .tabs { display: flex; gap: 2px; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .skwin .tabs div { padding: 7px 14px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .skwin .tabs div.on { color: var(--ac); border-bottom-color: var(--ac); } .hudm .skwin .tabs small { font-size: 9px; letter-spacing: 0; text-transform: none; margin-left: 4px; color: var(--dim); }
.hudm .skwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .skwin .col { padding: 10px; overflow-y: auto; display: flex; flex-direction: column; gap: 5px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .skwin .col > * { flex-shrink: 0; }
.hudm .skwin .col.l { width: 220px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .skwin .col.r { width: 300px; flex: none; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .skwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); display: flex; justify-content: space-between; margin-top: 4px; }
.hudm .skwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .skwin .pc { display: flex; gap: 8px; align-items: center; padding: 5px 6px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .skwin .pc:hover { border-color: color-mix(in srgb, var(--ac) 55%, transparent); } .hudm .skwin .pc.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .skwin .pc.no { opacity: .4; } .hudm .skwin .pc .eq { width: 36px; height: 36px; } .hudm .skwin .pc .t { flex: 1; min-width: 0; font-size: 12px; }
.hudm .skwin .pc .t b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .skwin .pc .t small { font-size: 10px; color: var(--dim); }
.hudm .skwin .pc .n { font-weight: 700; font-size: 12px; color: var(--tx); }
.hudm .skwin .gg { display: grid; grid-template-columns: repeat(4, 1fr); gap: 5px; }
.hudm .skwin .gm { position: relative; display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 5px 2px; border-radius: var(--rad); cursor: pointer; font-size: 9px; text-align: center;
  border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .skwin .gm .eq { width: 36px; height: 36px; } .hudm .skwin .gm.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: 0 0 8px var(--glow); }
.hudm .skwin .gm.bad { opacity: .3; } .hudm .skwin .gm.sel.bad { opacity: .55; } .hudm .skwin .gm.fit { border-color: color-mix(in srgb, var(--ac) 50%, transparent); } .hudm .skwin .gm i { font-style: normal; color: var(--dim); }
.hudm .skwin .gm .orb { position: absolute; right: 3px; top: 2px; font-size: 8px; font-weight: 800; color: #f2c94c; }
.hudm .skwin .mid { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 12px; position: relative;
  background: radial-gradient(circle at 50% 42%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 55%); }
.hudm .skwin .pair { display: flex; align-items: center; gap: 22px; }
.hudm .skwin .skslot { display: flex; flex-direction: column; align-items: center; gap: 4px; font-size: 11px; color: var(--dim); }
.hudm .skwin .skslot .eq { width: 74px; height: 74px; border-radius: 50%; box-shadow: 0 0 0 1px #000, 0 0 18px var(--glow); } .hudm .skwin .skslot .eq::after { border-radius: 50%; }
.hudm .skwin .skslot b { color: #fff; font-size: 13px; }
.hudm .skwin .arrow { font-size: 30px; color: var(--ac); text-shadow: 0 0 10px var(--glow); animation: hudm-skarr 1.4s ease-in-out infinite; } @keyframes hudm-skarr { 50% { transform: translateX(5px); } }
.hudm .skwin .pair.run .arrow { animation-duration: .3s; }
.hudm .skwin .skslot.win .eq { animation: hudm-skwin 1s ease-out; } @keyframes hudm-skwin { 30% { transform: scale(1.2); box-shadow: 0 0 0 1px #000, 0 0 50px #6fe08a; } }
.hudm .skwin .skslot.boom .eq { animation: hudm-skboom .9s ease-in forwards; } @keyframes hudm-skboom { 40% { transform: scale(1.25); filter: brightness(3); } 100% { transform: scale(0); opacity: 0; } }
.hudm .skwin .skslot.dud .eq { animation: hudm-skdud .5s; } @keyframes hudm-skdud { 25%, 75% { transform: translateX(-4px); } 50% { transform: translateX(4px); } }
.hudm .skwin .added { display: flex; flex-wrap: wrap; justify-content: center; gap: 5px; }
.hudm .skwin .added span { font-size: 11px; padding: 2px 9px; border-radius: 9px; border: 1px solid var(--fr); } .hudm .skwin .added span b { color: #6fe08a; }
.hudm .skwin .cap { display: flex; gap: 14px; font-size: 11px; color: var(--dim); } .hudm .skwin .cap b { color: #fff; } .hudm .skwin .cap b.max { color: #ff7a6a; }
.hudm .skwin .pips { display: flex; gap: 4px; } .hudm .skwin .pips i { width: 10px; height: 10px; border-radius: 50%; border: 1px solid var(--fr); } .hudm .skwin .pips i.on { background: var(--ac); border-color: #fff; box-shadow: 0 0 5px var(--glow); }
.hudm .skwin .big { font-size: 30px; font-weight: 800; color: #fff; text-align: center; line-height: 1; } .hudm .skwin .big small { display: block; font-size: 9px; letter-spacing: .14em; color: var(--dim); margin-top: 4px; }
.hudm .skwin .calc { font-size: 11px; color: var(--dim); text-align: center; } .hudm .skwin .calc b { color: var(--tx); }
.hudm .skwin .odds { display: flex; height: 14px; width: 100%; border-radius: 7px; overflow: hidden; border: 1px solid var(--fr); } .hudm .skwin .odds i { display: block; height: 100%; }
.hudm .skwin .odds .w { background: #4fbf6a; } .hudm .skwin .odds .n { background: #556; } .hudm .skwin .odds .d { background: #b33; }
.hudm .skwin .ol { display: flex; justify-content: space-between; width: 100%; font-size: 10px; color: var(--dim); } .hudm .skwin .ol b { color: #fff; }
.hudm .skwin .warn { font-size: 11px; color: #ff8a6a; text-align: center; } .hudm .skwin .warn.ok { color: #6fe08a; }
.hudm .skwin .go { width: 100%; height: 38px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: var(--rad); font-size: 14px; font-weight: 800; letter-spacing: .08em; color: #fff;
  border: 1px solid var(--ac); background: color-mix(in srgb, var(--ac) 32%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 14px var(--glow); } .hudm .skwin .go.dis { opacity: .4; pointer-events: none; }
.hudm .skwin .go.risk { border-color: #e05555; background: linear-gradient(180deg, rgba(220,70,70,.45), rgba(120,20,20,.6)); box-shadow: 0 0 0 1px #000, 0 0 14px rgba(220,70,70,.4); }
.hudm .skwin .log { font-size: 11px; color: var(--dim); } .hudm .skwin .log div { padding: 2px 0; }
.hudm .skwin .note { font-size: 11px; line-height: 1.45; color: var(--dim); padding: 8px 10px; border-radius: var(--rad); border: 1px dashed var(--fr); } .hudm .skwin .note b { color: var(--tx); }
.hudm .skwin .ring { position: relative; width: 240px; height: 240px; }
.hudm .skwin .ring::before { content: ""; position: absolute; inset: 30px; border-radius: 50%; border: 2px dashed color-mix(in srgb, var(--ac) 50%, transparent); animation: hudm-skspin 24s linear infinite; }
@keyframes hudm-skspin { to { transform: rotate(360deg); } }
.hudm .skwin .ring .skcore { position: absolute; left: 50%; top: 50%; translate: -50% -50%; }
.hudm .skwin .ring .sk { position: absolute; width: 46px; height: 46px; margin: -23px; border-radius: 50%; cursor: pointer; border: 2px solid var(--fr); background: radial-gradient(circle at 40% 35%, #1a2030, #05070c);
  box-shadow: inset 0 0 8px #000; display: flex; align-items: center; justify-content: center; }
.hudm .skwin .ring .sk:hover { border-color: var(--ac); } .hudm .skwin .ring .sk.full { border-color: var(--jc); box-shadow: 0 0 12px var(--jc), inset 0 0 8px #000; }
.hudm .skwin .ring .sk.target { border-color: var(--ac); animation: hudm-skpulse 1s ease-in-out infinite; } @keyframes hudm-skpulse { 50% { box-shadow: 0 0 14px var(--glow); } }
.hudm .skwin .jw { width: 22px; height: 22px; transform: rotate(45deg); border-radius: 4px; background: radial-gradient(circle at 35% 35%, #fff, var(--jc) 45%, #000 110%); box-shadow: 0 0 8px var(--jc); }
.hudm .skwin .jl { display: flex; gap: 8px; align-items: center; padding: 5px 7px; border-radius: var(--rad); cursor: pointer; font-size: 12px; border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .skwin .jl:hover { border-color: var(--ac); } .hudm .skwin .jl.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); } .hudm .skwin .jl.bad { opacity: .35; }
.hudm .skwin .jl .t { flex: 1; } .hudm .skwin .jl small { display: block; font-size: 10px; color: var(--dim); }
.hudm .skwin .sample { position: absolute; top: 8px; left: 50%; translate: -50% 0; font-size: 10px; font-weight: 800; letter-spacing: .12em; padding: 2px 10px; border-radius: 9px; color: #000; background: #f2c94c; }
`;

export class SocketMock extends HudWindow {
  private items = new Map<string, Item>();
  private tab: "Gems" | "Sockets" = "Gems";
  private gear: Piece[] = [
    { id: 1, name: "DragonSlayer", added: { MaxDC: 2, Accuracy: 1 }, gems: 3 }, { id: 2, name: "TitanArmour(M)", added: { MaxAC: 1 }, gems: 1 },
    { id: 3, name: "HelmetOfHero", added: {}, gems: 0 }, { id: 4, name: "GoldDragonRing", added: { MaxDC: 4 }, gems: 4 },
    { id: 5, name: "WhiteGoldBracelet", added: {}, gems: 0 }, { id: 6, name: "HeroNecklace", added: { MaxDC: 1 }, gems: 1 }, { id: 7, name: "WhiteGoldBelt", added: {}, gems: 0 },
    { id: 8, name: "WhiteGoldShoes", added: {}, gems: 0 }, { id: 9, name: "(HP)DrugXL", added: {}, gems: 0 },
  ];
  private gemCount = new Map<string, number>();
  private sel: Piece = this.gear[0]!;
  private gem: string | null = null;
  private busy = false;
  private fx: "" | "win" | "boom" | "dud" = "";
  private msg: [string, string] = ["", ""];
  private log: string[] = [];
  // Sockets bench
  private sockets: (number | null)[] = [0, null, null];
  private jewels = [1, 1, 2, 1, 1, 1];
  private jewelSel: number | null = null;
  private sockItem = "DragonSlayer";

  constructor(private readonly host: SocketHost) {
    super("skwin", 12, 22);
    injectCss("hudm-socket-css", CSS);
    void itemData().then((d) => {
      if (!d) return;
      for (const i of d.items) { this.items.set(norm(i.name), i); if (i.type === "Gem") this.gemCount.set(i.name, i.shape === 4 ? 1 + (i.name.length % 2) : 2 + (i.name.length % 4)); }
      if (this.open) this.render();
    });
  }
  protected override shown(): void { this.render(); }
  private item(n: string): Item | undefined { return findIn(this.items, n); }

  private render(): void {
    const tabs = h("div", "tabs");
    for (const [t, l, sub] of [["Gems", "Gems & orbs", "live rules"], ["Sockets", "Sockets", "sample: none on this server"]] as const) {
      const d = h("div", this.tab === t ? "on" : "", l, h("small", "", sub)); d.addEventListener("click", () => { this.tab = t; this.render(); }); tabs.append(d);
    }
    this.el.replaceChildren(h("div", "panel", this.titleBar("SOCKETS & GEMS", "Close"), tabs, this.tab === "Gems" ? this.gemsBench() : this.socketBench()));
  }

  // — Gems & orbs —
  /** PlayerObject gem combine: the reason it can't be applied, or "". */
  private why(p: Piece, gemName: string): string {
    const it = this.item(p.name), g = this.item(gemName);
    if (!it || !g) return "Unknown item";
    if (p.gone) return "Destroyed";
    if (it.bind.includes("DontUpgrade") || it.unique !== "None") return "This item can't be upgraded";
    const r = gemRules(g);
    if (!r.fits.includes(it.type)) return `${gemName} doesn't fit a ${it.type.toLowerCase()} (fits ${r.fits.join(", ")})`;
    const stat = Object.keys(r.adds)[0] ?? "";
    if (p.gems >= r.maxGems) return `This item already holds ${p.gems} gems (max ${r.maxGems} for this one)`;
    if ((p.added[stat] ?? 0) >= r.maxStat && stat !== "MaxDurability") return `${STAT_LABEL[stat] ?? stat} is already at its maximum (${r.maxStat})`;
    return "";
  }
  private chance(p: Piece, g: Item): number {
    const r = gemRules(g), stat = Object.keys(r.adds)[0] ?? "";
    const already = stat === "MaxDurability" ? p.gems : (p.added[stat] ?? 0); // GemStatIndependent: the default case multiplies by GemCount
    const pen = r.penalty * already;
    return pen >= r.base ? 0 : r.base - pen + GEM_RATE;
  }

  private gemsBench(): HTMLElement {
    const left = h("div", "col l", h("div", "lbl", "Your gear", h("b", "", "gems used")));
    for (const p of this.gear) {
      if (p.gone) continue;
      const it = this.item(p.name);
      const nm = h("b", "", p.name); nm.style.color = GRADE[it?.grade ?? "Common"]!;
      const e = h("div", "pc" + (this.sel === p ? " sel" : "") + (it && (it.unique !== "None") ? " no" : ""), itemCell(this.el, p.name, it?.img ?? -1), h("div", "t", nm, h("small", "", it?.type ?? "")), h("span", "n", `◆${p.gems}`));
      e.addEventListener("click", () => { if (this.busy) return; this.sel = p; this.fx = ""; this.msg = ["", ""]; this.render(); });
      left.append(e);
    }
    const right = h("div", "col r", h("div", "lbl", "Gems & orbs", h("b", "", "click to pick")));
    const gg = h("div", "gg");
    const gems = [...this.items.values()].filter((i) => i.type === "Gem").sort((a, b) => a.shape - b.shape || a.name.localeCompare(b.name));
    for (const g of gems) {
      const n = this.gemCount.get(g.name) ?? 0, w = this.why(this.sel, g.name);
      // Lit only when it fits AND you still have one; used up = greyed like a gem that doesn't fit.
      const e = h("div", "gm" + (this.gem === g.name ? " sel" : "") + (w || !n ? " bad" : " fit"), itemCell(this.el, g.name, g.img), h("span", "", g.name.replace(/(Gem|Orb)$/, "")), h("i", "", `×${n}`));
      if (g.shape === 4) e.append(h("span", "orb", "ORB"));
      e.title = w || (n ? `${g.name}: fits this item` : `You have no ${g.name} left`);
      e.addEventListener("click", () => { if (this.busy) return; this.gem = g.name; this.fx = ""; this.msg = ["", ""]; this.render(); });
      gg.append(e);
    }
    right.append(gg, h("div", "note", h("b", "", "Gems"), " can destroy the item when they fail (3 in 15 failures). ", h("b", "", "Orbs"), " never do, and start at a higher chance."));
    return h("div", "body", left, this.gemMid(), right);
  }

  private gemMid(): HTMLElement {
    const p = this.sel, it = this.item(p.name);
    const g = this.gem ? this.item(this.gem) : undefined;
    const mid = h("div", "mid");
    const nm = h("b", "", p.name); nm.style.color = GRADE[it?.grade ?? "Common"]!;
    const target = h("div", "skslot" + (this.fx ? " " + this.fx : ""), itemCell(this.el, p.name, it?.img ?? -1), nm, it?.type ?? "");
    const gemSlot = h("div", "skslot", itemCell(this.el, g?.name ?? "Pick a gem", g?.img ?? -1), h("b", "", g?.name ?? "Pick a gem or orb"), g ? (g.shape === 4 ? "Orb" : "Gem") : "from the right");
    mid.append(h("div", "pair" + (this.busy ? " run" : ""), gemSlot, h("div", "arrow", "➜"), target));
    const added = h("div", "added");
    for (const [k, v] of Object.entries(p.added)) added.append(h("span", "", `${STAT_LABEL[k] ?? k} `, h("b", "", `+${v}`)));
    if (!added.childElementCount) added.append(h("span", "", "No added stats yet"));
    mid.append(added);
    if (!g) { mid.append(h("div", "warn", "Pick a gem or orb that fits this item (lit on the right).")); return mid; }
    const r = gemRules(g), stat = Object.keys(r.adds)[0] ?? "", why = this.why(p, g.name);
    const pips = h("div", "pips"); for (let i = 0; i < r.maxGems; i++) pips.append(h("i", i < p.gems ? "on" : ""));
    mid.append(h("div", "cap", h("span", "", "Gems ", h("b", p.gems >= r.maxGems ? "max" : "", `${p.gems}/${r.maxGems}`)), pips,
      h("span", "", `${STAT_LABEL[stat] ?? stat} `, h("b", (p.added[stat] ?? 0) >= r.maxStat ? "max" : "", `${p.added[stat] ?? 0}/${r.maxStat}`))));
    const ch = why ? 0 : this.chance(p, g);
    const already = stat === "MaxDurability" ? p.gems : (p.added[stat] ?? 0);
    mid.append(h("div", "big", `${ch}%`, h("small", "", "SUCCESS CHANCE")),
      h("div", "calc", "base ", h("b", "", `${r.base}%`), " − ", h("b", "", `${r.penalty}%`), ` × ${already} already ${stat === "MaxDurability" ? "gems" : "added"}`, GEM_RATE ? ` + ${GEM_RATE}% gem rate` : ""));
    const destroy = r.orb ? 0 : (100 - ch) * 3 / 15, none = 100 - ch - destroy;
    const odds = h("div", "odds"); for (const [c, v] of [["w", ch], ["n", none], ["d", destroy]] as const) { const i = h("i", c); i.style.width = v + "%"; odds.append(i); }
    mid.append(odds, h("div", "ol", h("span", "", h("b", "", `${ch.toFixed(0)}%`), ` +${r.adds[stat]} ${STAT_LABEL[stat] ?? stat}`), h("span", "", h("b", "", `${none.toFixed(0)}%`), " no effect"),
      h("span", "", h("b", "", `${destroy.toFixed(0)}%`), " destroyed")));
    const have = this.gemCount.get(g.name) ?? 0;
    const go = h("div", "go" + (destroy ? " risk" : "") + (why || !have || this.busy ? " dis" : ""), destroy ? `APPLY · ${destroy.toFixed(0)}% RISK` : "APPLY");
    go.addEventListener("click", () => this.apply(p, g));
    mid.append(go);
    if (why) mid.append(h("div", "warn", why)); else if (!have) mid.append(h("div", "warn", `You have no ${g.name}.`));
    if (this.msg[0]) mid.append(h("div", "warn " + this.msg[1], this.msg[0]));
    if (this.log.length) { const lg = h("div", "log"); for (const l of this.log.slice(0, 4)) lg.append(h("div", "", l)); mid.append(lg); }
    return mid;
  }

  private apply(p: Piece, g: Item): void {
    if (this.busy || this.why(p, g.name) || !(this.gemCount.get(g.name) ?? 0)) return;
    const ch = this.chance(p, g), r = gemRules(g), stat = Object.keys(r.adds)[0] ?? "";
    this.gemCount.set(g.name, (this.gemCount.get(g.name) ?? 0) - 1);
    this.busy = true; this.fx = ""; this.render();
    window.setTimeout(() => {
      this.busy = false;
      const ok = Math.floor(Math.random() * 100) < ch;
      if (ok) { p.added[stat] = (p.added[stat] ?? 0) + (r.adds[stat] ?? 1); p.gems++; this.fx = "win"; this.msg = [`${p.name} +${r.adds[stat]} ${STAT_LABEL[stat] ?? stat}!`, "ok"]; }
      else if (!r.orb && Math.floor(Math.random() * 15) < 3) { p.gone = true; this.fx = "boom"; this.msg = [`${g.name} failed and ${p.name} was destroyed.`, ""]; }
      else { this.fx = "dud"; this.msg = [`${g.name} had no effect.`, ""]; }
      this.log.unshift(this.msg[0]); this.host.say(this.msg[0]);
      this.render();
      if (p.gone) window.setTimeout(() => { const next = this.gear.find((x) => !x.gone); if (this.sel === p && next) { this.sel = next; this.fx = ""; this.render(); } }, 1300);
    }, 900);
  }

  // — Sockets (sample) —
  private socketBench(): HTMLElement {
    const it = this.item(this.sockItem);
    const left = h("div", "col l", h("div", "lbl", "Socketed item"));
    for (const n of ["DragonSlayer", "TitanArmour(M)", "GoldDragonRing"]) {
      const i = this.item(n);
      const e = h("div", "pc" + (this.sockItem === n ? " sel" : ""), itemCell(this.el, n, i?.img ?? -1), h("div", "t", h("b", "", n), h("small", "", `${i?.type ?? ""} · 3 sample sockets`)));
      e.addEventListener("click", () => {
        // The jewels in the old item's sockets go back to the bag.
        for (const s of this.sockets) if (s !== null) this.jewels[s]!++;
        this.sockItem = n; this.sockets = [null, null, null]; this.jewelSel = null; this.render();
      });
      left.append(e);
    }
    left.append(h("div", "note", h("b", "", "Not on this server yet. "), "No item has sockets (ItemInfo.Slots) and no ItemType.Socket items exist, so the sockets and jewels here are samples showing how the dialog would work."));
    const shapeOk = (shape: number) => shape === 0 || (shape === 1 && it?.type === "Weapon") || (shape === 2 && it?.type === "Armour") || (shape === 3 && ["Ring", "Bracelet", "Necklace"].includes(it?.type ?? ""));
    const ring = h("div", "ring");
    const core = h("div", "skcore", itemCell(this.el, this.sockItem, it?.img ?? -1));
    ring.append(core, h("div", "sample", "SAMPLE"));
    this.sockets.forEach((s, i) => {
      const a = -Math.PI / 2 + i * 2 * Math.PI / this.sockets.length;
      const e = h("div", "sk" + (s !== null ? " full" : this.jewelSel !== null ? " target" : ""));
      Object.assign(e.style, { left: 120 + Math.cos(a) * 90 + "px", top: 120 + Math.sin(a) * 90 + "px" });
      if (s !== null) { const j = JEWELS[s]!; e.style.setProperty("--jc", j[1]); e.append(h("div", "jw")); e.title = `${j[0]}: click to remove`; }
      else e.title = this.jewelSel !== null ? "Click to socket the jewel here" : "Empty socket";
      e.addEventListener("click", () => {
        if (s !== null) { this.jewels[s]!++; this.sockets[i] = null; this.host.say(`${JEWELS[s]![0]} was removed and returned to your bag.`); }
        else if (this.jewelSel !== null && shapeOk(JEWELS[this.jewelSel]![2]) && this.jewels[this.jewelSel]! > 0) { this.sockets[i] = this.jewelSel; this.jewels[this.jewelSel]!--; this.host.say(`${JEWELS[this.jewelSel]![0]} socketed into ${this.sockItem}.`); this.jewelSel = null; }
        this.render();
      });
      ring.append(e);
    });
    const tot: Record<string, number> = {};
    for (const s of this.sockets) if (s !== null) for (const [k, v] of Object.entries(JEWELS[s]![3])) tot[k] = (tot[k] ?? 0) + v;
    const added = h("div", "added");
    for (const [k, v] of Object.entries(tot)) added.append(h("span", "", `${STAT_LABEL[k] ?? k} `, h("b", "", `+${v}`)));
    if (!added.childElementCount) added.append(h("span", "", "Empty sockets add nothing"));
    const mid = h("div", "mid", ring, h("div", "lbl", "From sockets"), added, h("div", "calc", "Pick a jewel on the right, then an empty socket. Click a filled socket to take the jewel out."));
    const right = h("div", "col r", h("div", "lbl", "Socket jewels", h("b", "", "samples")));
    JEWELS.forEach(([n, c, shape, st], i) => {
      const ok = shapeOk(shape);
      const sw = h("div", "jw"); sw.style.setProperty("--jc", c); sw.style.margin = "0 6px";
      const e = h("div", "jl" + (this.jewelSel === i ? " sel" : "") + (ok ? "" : " bad"), sw, h("div", "t", n, h("small", "", `${Object.entries(st).map(([k, v]) => `+${v} ${STAT_LABEL[k] ?? k}`).join(", ")} · ${["any", "weapons", "armour", "jewellery"][shape]} only`)), h("b", "", `×${this.jewels[i]}`));
      (e.firstChild as HTMLElement).style.setProperty("--jc", c);
      e.title = ok ? "" : `Shape ${shape}: ${["", "weapons", "armour", "rings, bracelets and necklaces"][shape]} only`;
      e.addEventListener("click", () => { if (ok && this.jewels[i]! > 0) { this.jewelSel = this.jewelSel === i ? null : i; this.render(); } });
      right.append(e);
    });
    return h("div", "body", left, mid, right);
  }
}
