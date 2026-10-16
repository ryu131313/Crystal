// Item tooltip mock for the scene lab's UI tester, a modern take on the desktop's item label (GameScene.CreateItemLabel:
// name / grade, attack, defence, weight, awakening, sockets, requirements, bind, set, story). One tooltip serves every
// window mock: it shows on hover over any element with data-item (itemCell in hudWindow.ts sets it, so the bag, the
// character, storage, trade, mail, the hero ... all get it; chat item links too). Item data (stats, requirements,
// grade, durability, weight, price, stack size, bind flags, special modes, set, story text) is ItemInfo from a copy of
// the live Server.MirDB (play/lab-data/items.json, lab-only); set piece counts are ItemSets.Amount and set bonuses are
// HumanObject's per-set stats. Grade colours are the desktop's GradeNameColor. Per-copy values (current durability,
// added stats, sockets, awakening) are mock, made up from the item name so they stay the same. Comparison: the change
// against what the character wears in that slot (hudCharacter.ts), and Shift shows the worn item's card beside it.
// Ctrl+click an item to link it in chat. Items missing from items.json keep the plain title tooltip.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { EQUIPPED_NAMES } from "./hudCharacter.ts";
import { h, injectCss, rafCoalesce } from "./hudWindow.ts";

export interface Item {
  name: string; type: string; grade: string; img: number; shape: number; weight: number; light: number; dura: number; price: number; stack: number;
  reqType: string; req: number; reqClass: string; reqGender: string; set: string; bind: string; unique: string; tip: string; slots: number; awaken: boolean;
  stats: Record<string, number>;
}
export interface Data { items: Item[]; sets: Record<string, { name: string; type: string }[]> }

export const GRADE: Record<string, string> = { None: "#f3e08a", Common: "#f3e08a", Rare: "#3fbfff", Legendary: "#ff9a3c", Mythical: "#e0a0ff", Heroic: "#ff5a5a" };
/** ItemSets.Amount (pieces for the bonus) and HumanObject's set stats, for the sets in items.json. */
export const SET_INFO: Record<string, [string, number, [string, string][]]> = {
  Spirit: ["Spirit", 5, [["DC", "+2-5"], ["Attack speed", "+2"]]], RedOrchid: ["Red Orchid", 3, [["Accuracy", "+2"]]], Smash: ["Smash", 3, [["DC", "+1-3"]]],
  Bug: ["Bug", 3, [["DC / MC / SC", "+0-1"], ["MAC", "+0-1"], ["Poison resist", "+1"]]], WhiteGold: ["White Gold", 5, [["DC", "+0-2"], ["AC", "+0-2"]]],
  WhiteGoldH: ["White Gold (H)", 5, [["DC", "+0-3"], ["HP", "+30"], ["Attack speed", "+2"]]], RedJadeH: ["Red Jade (H)", 5, [["MC", "+0-2"], ["MP", "+40"], ["Agility", "+2"]]],
  Hyeolryong: ["Tarragon", 5, [["SC", "+0-2"], ["HP", "+15"], ["MP", "+20"], ["Holy", "+1"], ["Accuracy", "+1"]]], Mir: ["Mir", 8, [["Bonus per piece worn", "(MirSet)"]]],
};
const BIND: Record<string, string> = {
  DontDeathdrop: "Kept on death", DontDrop: "Can't drop", DontSell: "Can't sell", DontStore: "Can't store", DontTrade: "Can't trade", DontRepair: "Can't repair",
  DontUpgrade: "Can't upgrade", DestroyOnDrop: "Destroyed when dropped", BreakOnDeath: "Breaks on death", BindOnEquip: "Binds when equipped", NoSRepair: "No special repair",
  NoWeddingRing: "Can't be a wedding ring", UnableToRent: "Can't rent", UnableToDisassemble: "Can't disassemble", NoMail: "Can't mail", NoHero: "Heroes can't use",
};
const SPECIAL: Record<string, string> = { Paralize: "Paralysis", Teleport: "Teleport", ClearRing: "Invisibility", Protection: "Protection", Revival: "Revival", Muscle: "Muscle",
  Flame: "Flame", Healing: "Healing", Probe: "Probe", Blink: "Blink" };
const RANGES: [string, string][] = [["DC", "DC"], ["MC", "MC"], ["SC", "SC"], ["AC", "AC"], ["MAC", "MAC"]];
const NAMES: Record<string, string> = { AttackSpeed: "Attack speed", HPDrainRatePercent: "HP drain", MagicResist: "Magic resist", PoisonResist: "Poison resist", CriticalRate: "Critical rate",
  HandWeight: "Hand weight", WearWeight: "Wear weight", BagWeight: "Bag weight", MentorDamageRatePercent: "Mentor damage", DamageReductionPercent: "Damage reduction", Holy: "Holy" };
const statName = (k: string) => NAMES[k] ?? k.replace(/Percent$/, "").replace(/([a-z])([A-Z])/g, "$1 $2");
const pct = (k: string) => /Percent$/.test(k) ? "%" : "";
const CLASSES: [string, string][] = [["Warrior", "WAR"], ["Wizard", "WIZ"], ["Taoist", "TAO"], ["Assassin", "ASN"], ["Archer", "ARC"]];
const WEARABLE = new Set(["Weapon", "Armour", "Helmet", "Necklace", "Bracelet", "Ring", "Amulet", "Belt", "Boots", "Stone", "Torch", "Mount"]);
export const norm = (s: string) => s.replace(/\s+/g, "").toLowerCase();
/** A stable number 0..1 from a name, so mock per-copy values don't change between hovers. */
const hash = (s: string, salt = 0) => { let x = 2166136261 ^ salt; for (const c of s) x = Math.imul(x ^ c.charCodeAt(0), 16777619); return ((x >>> 0) % 1000) / 1000; };

/** Gems and orbs (ItemType.Gem, Shape 3 / 4) keep their upgrade rules in ItemInfo stats (PlayerObject gem combine):
 *  CriticalRate = base chance, Reflect = minus per point of that stat the item already has, CriticalDamage = most gems per
 *  item, HPDrainRatePercent = most of that stat; the items they fit come from Unique flags (ValidGemForItem). */
const GEM_FITS: [string, string][] = [["Paralize", "Weapon"], ["Teleport", "Armour"], ["ClearRing", "Helmet"], ["Protection", "Necklace"], ["Revival", "Bracelet"],
  ["Muscle", "Ring"], ["Flame", "Amulet"], ["Healing", "Belt"], ["Probe", "Boots"], ["Skill", "Stone"], ["NoDuraLoss", "Torch"]];
const RULE_STATS = new Set(["CriticalRate", "Reflect", "CriticalDamage", "HPDrainRatePercent"]);
export function gemRules(it: Item) {
  const flags = it.unique.split(/,\s*/);
  const adds: Record<string, number> = {};
  for (const [k, v] of Object.entries(it.stats)) if (!RULE_STATS.has(k)) adds[k] = v;
  if (it.dura && !Object.keys(adds).length) adds["MaxDurability"] = it.dura / 1000;
  return { adds, base: it.stats.CriticalRate ?? 0, penalty: it.stats.Reflect ?? 0, maxGems: it.stats.CriticalDamage ?? 0, maxStat: it.stats.HPDrainRatePercent ?? 0,
    fits: GEM_FITS.filter(([f]) => flags.includes(f)).map(([, t]) => t), orb: it.shape === 4 };
}

let dataP: Promise<Data | null> | null = null;
/** play/lab-data/items.json, fetched once (the tooltip and the inspect window share it). */
/** Mock per-copy values (UserItem) for an item name: current / max durability, added stats, sockets, awakening level.
 *  Made up from the name; NPC services (hudNpcBox.ts: repair, reset, downgrade) change them through editCopy. */
export interface Copy { cur: number; max: number; added: Record<string, number>; gems: number; awake: number }
const copyEdits = new Map<string, { added?: Record<string, number>; awake?: number; max?: number }>();
export function editCopy(name: string, patch: { added?: Record<string, number>; awake?: number; max?: number }): void { copyEdits.set(norm(name), { ...copyEdits.get(norm(name)), ...patch }); }
export function copyOf(it: Item): Copy {
  const wear = WEARABLE.has(it.type) && it.dura > 0;
  const e = copyEdits.get(norm(it.name)) ?? {};
  const max = wear ? Math.round(it.dura * (e.max ?? 1)) : it.dura;
  const cur = wear ? Math.min(max, Math.round(it.dura * duraFrac(it.name))) : it.dura;
  let added: Record<string, number> = {};
  if (wear && hash(it.name, 2) > 0.45) { const k = it.stats.MaxDC ? "MaxDC" : it.stats.MaxAC ? "MaxAC" : it.stats.MaxMC ? "MaxMC" : it.stats.MaxSC ? "MaxSC" : ""; if (k) added[k] = 1 + Math.floor(hash(it.name, 3) * 3); }
  if (wear && hash(it.name, 4) > 0.7) added.Accuracy = 1;
  if (e.added) added = { ...e.added };
  const gems = it.type === "Weapon" || it.type === "Armour" ? Math.floor(hash(it.name, 5) * 3) : 0;
  const awake = e.awake ?? (it.type === "Weapon" && hash(it.name, 6) > 0.4 ? 1 + Math.floor(hash(it.name, 7) * 4) : 0);
  return { cur, max, added, gems, awake };
}

/** Mock current durability as a fraction of the max, per item name (made up from the name until something wears or
 *  repairs it: the HUD's durability strip, hudExtras.ts). The tooltip reads it, so both agree. */
const duraLeft = new Map<string, number>();
export function duraFrac(name: string): number { return duraLeft.get(norm(name)) ?? 0.35 + 0.65 * hash(name, 1); }
export function setDura(name: string, frac: number): void { duraLeft.set(norm(name), Math.max(0, Math.min(1, frac))); }

export function itemData(): Promise<Data | null> {
  return dataP ??= fetch(new URL("./lab-data/items.json", location.href)).then((r) => r.ok ? r.json() as Promise<Data> : null).catch(() => null);
}
export function findIn(map: Map<string, Item>, name: string): Item | undefined {
  const n = norm(name);
  return map.get(n) ?? map.get(n.replace(/\(weddingring\)$/, "")) ?? map.get(norm(name.replace(/ \([^)]*\)$/, "")));
}

const CSS = `
.hudm .itip { position: absolute; z-index: 50; pointer-events: none; display: flex; gap: 6px; align-items: flex-start; animation: hudm-tipin .12s ease-out; }
@keyframes hudm-tipin { from { opacity: 0; transform: translateY(4px); } }
.hudm .itip .tc { position: relative; width: 304px; border-radius: var(--rad); overflow: hidden; color: var(--tx); font-size: 12px;
  background: linear-gradient(180deg, color-mix(in srgb, var(--pb) 94%, #000), color-mix(in srgb, var(--pb) 70%, #000)); border: 1px solid color-mix(in srgb, var(--gc) 70%, var(--fr));
  box-shadow: 0 0 0 1px #000, 0 12px 30px rgba(0,0,0,.75), 0 0 18px color-mix(in srgb, var(--gc) 25%, transparent); }
.hudm .itip .tc.shine::before { content: ""; position: absolute; inset: 0; z-index: 0; pointer-events: none; background: linear-gradient(115deg, transparent 40%, color-mix(in srgb, var(--gc) 22%, transparent) 50%, transparent 60%);
  background-size: 250% 100%; animation: hudm-tipshine 2.6s linear infinite; }
@keyframes hudm-tipshine { from { background-position: 150% 0; } to { background-position: -100% 0; } }
.hudm .itip .tc > * { position: relative; z-index: 1; }
.hudm .itip .hd { display: flex; gap: 10px; align-items: center; padding: 10px 10px 8px; background: linear-gradient(180deg, color-mix(in srgb, var(--gc) 26%, transparent), transparent); }
.hudm .itip .ic { width: 50px; height: 50px; flex: none; border-radius: calc(var(--rad) * .8); border: 1px solid var(--gc); background: #000 center / auto no-repeat; image-rendering: pixelated;
  box-shadow: 0 0 0 1px #000, inset 0 0 12px color-mix(in srgb, var(--gc) 35%, transparent), 0 0 10px color-mix(in srgb, var(--gc) 35%, transparent); }
.hudm .itip .nm { flex: 1; min-width: 0; } .hudm .itip .nm b { display: block; font-size: 15px; color: var(--gc); text-shadow: 0 0 8px color-mix(in srgb, var(--gc) 40%, transparent); line-height: 1.15; }
.hudm .itip .nm small { display: block; margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .itip .lv { align-self: flex-start; font-size: 10px; font-weight: 800; padding: 2px 7px; border-radius: 8px; color: #fff; background: #0008; border: 1px solid var(--fr); white-space: nowrap; }
.hudm .itip .lv.bad { color: #ff8a7a; border-color: #c44; }
.hudm .itip .tag { position: absolute; left: 10px; top: -1px; font-size: 9px; font-weight: 800; letter-spacing: .12em; padding: 1px 8px; border-radius: 0 0 6px 6px; color: #000; background: var(--gc); }
.hudm .itip .sec { padding: 6px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .itip .pri { display: flex; gap: 16px; flex-wrap: wrap; padding: 6px 12px 8px; }
.hudm .itip .pri div { font-size: 11px; color: var(--dim); } .hudm .itip .pri b { display: block; font-size: 22px; line-height: 1.05; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .itip .pri em, .hudm .itip .st em { font-style: normal; color: #6fe08a; font-weight: 700; margin-left: 4px; font-size: 12px; }
.hudm .itip .st { display: flex; justify-content: space-between; padding: 1px 0; } .hudm .itip .st span { color: var(--dim); } .hudm .itip .st b { color: #fff; font-weight: 600; }
.hudm .itip .cmp { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; font-size: 10px; color: var(--dim); }
.hudm .itip .cmp i { font-style: normal; font-weight: 700; padding: 1px 6px; border-radius: 6px; font-size: 10px; }
.hudm .itip .cmp i.up { color: #6fe08a; background: rgba(80,200,110,.14); } .hudm .itip .cmp i.dn { color: #ff7a6a; background: rgba(255,90,80,.14); } .hudm .itip .cmp i.eq { color: var(--ac); background: color-mix(in srgb, var(--ac) 15%, transparent); }
.hudm .itip .sk { display: flex; gap: 6px; align-items: center; font-size: 11px; color: var(--dim); }
.hudm .itip .sk u { width: 14px; height: 14px; border-radius: 50%; text-decoration: none; border: 1px solid var(--fr); background: #05070c; box-shadow: inset 0 0 3px #000; }
.hudm .itip .sk u.full { border-color: #ff6ad5; background: radial-gradient(circle at 35% 35%, #ffd0f2, #c03aa0 60%, #50103c); box-shadow: 0 0 6px rgba(255,106,213,.6); }
.hudm .itip .aw { display: flex; align-items: center; gap: 6px; font-size: 11px; } .hudm .itip .aw b { color: #f2c94c; } .hudm .itip .aw span { color: #f2c94c; letter-spacing: 1px; }
.hudm .itip .du { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .itip .du .bar { flex: 1; height: 5px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .itip .du .bar i { display: block; height: 100%; background: var(--ac); }
.hudm .itip .du .bar.low i { background: #e05555; }
.hudm .itip .rq { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; font-size: 11px; } .hudm .itip .rq > :first-child { margin-right: 4px; }
.hudm .itip .rq .ok { color: #6fe08a; } .hudm .itip .rq .no { color: #ff7a6a; }
.hudm .itip .cl { font-size: 9px; font-weight: 800; padding: 1px 5px; border-radius: 4px; border: 1px solid var(--fr); color: var(--dim); opacity: .45; }
.hudm .itip .cl.on { opacity: 1; color: #fff; border-color: var(--ac); } .hudm .itip .cl.me { box-shadow: 0 0 6px var(--glow); }
.hudm .itip .set b { color: #6fe08a; } .hudm .itip .set .pc { display: flex; justify-content: space-between; font-size: 11px; color: var(--dim); padding: 1px 0; }
.hudm .itip .set .pc.on { color: #cfe8d0; } .hudm .itip .set .pc.on span:first-child::before { content: "✓ "; color: #6fe08a; } .hudm .itip .set .pc:not(.on) span:first-child::before { content: "○ "; }
.hudm .itip .set .bn { margin-top: 3px; font-size: 11px; color: var(--dim); } .hudm .itip .set .bn.on { color: #6fe08a; }
.hudm .itip .bd { display: flex; flex-wrap: wrap; gap: 4px; }
.hudm .itip .bd span { font-size: 10px; padding: 1px 7px; border-radius: 8px; color: #ffb3a6; background: rgba(255,90,80,.12); border: 1px solid rgba(255,90,80,.35); }
.hudm .itip .bd span.sp { color: #c9b6ff; background: rgba(160,120,255,.12); border-color: rgba(160,120,255,.4); }
.hudm .itip .story { font-style: italic; color: #d9c79a; line-height: 1.4; }
.hudm .itip .ft { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; font-size: 11px; color: var(--dim); background: #0006; }
.hudm .itip .ft b { color: #f2c94c; }
.hudm .itip .hint { padding: 4px 12px 7px; font-size: 10px; color: var(--dim); background: #0006; }
.hudm .itip .hint kbd { font: 600 9px ui-monospace, Consolas, monospace; padding: 0 4px; border-radius: 3px; border: 1px solid var(--fr); color: var(--tx); }
.hudm .itip .tc.worn { opacity: .92; } .hudm .itip .tc.worn .tag { background: var(--dim); }
`;

export class ItemTooltip {
  readonly el: HTMLElement;
  /** Ctrl+click: put [Item] in the chat input (hudMock.ts wires ChatMock.insertLink). */
  onLink: (name: string) => void = () => {};
  private data = new Map<string, Item>();
  private sets: Data["sets"] = {};
  private level = 42;
  private cls = "Warrior";
  private gender = "Male";
  private hoverEl: HTMLElement | null = null;
  private shift = false;
  private x = 0; private y = 0;

  constructor(private readonly root: HTMLElement, private readonly layer: HTMLElement) {
    injectCss("hudm-tooltip-css", CSS);
    this.el = h("div", "itip");
    this.el.hidden = true;
    void itemData().then((d) => {
      if (!d) return;
      for (const i of d.items) this.data.set(norm(i.name), i);
      this.sets = d.sets;
    }).catch(() => {});
    root.addEventListener("mouseover", (e) => {
      const t = (e.target as HTMLElement).closest?.<HTMLElement>("[data-item]");
      if (t === this.hoverEl) return;
      this.leave();
      if (!t || !root.contains(t)) return;
      const it = this.find(t.dataset.item ?? "");
      if (!it) return;
      this.hoverEl = t;
      if (t.title) { t.dataset.title = t.title; t.removeAttribute("title"); }
      this.render(it, +(t.dataset.count ?? 1));
    });
    // Follows the mouse at most once a frame, from the box measured when the tooltip was drawn (no layout reads).
    const follow = rafCoalesce(() => { if (this.hoverEl) this.place(); });
    root.addEventListener("mousemove", (e) => { this.x = e.clientX; this.y = e.clientY; if (this.hoverEl) follow(); });
    root.addEventListener("mouseout", (e) => { if (this.hoverEl && !this.hoverEl.contains(e.relatedTarget as Node)) this.leave(); });
    root.addEventListener("click", (e) => {
      if (!e.ctrlKey) return;
      const t = (e.target as HTMLElement).closest?.<HTMLElement>("[data-item]");
      const it = t && this.find(t.dataset.item ?? "");
      if (it) { e.preventDefault(); e.stopPropagation(); this.onLink(it.name); }
    }, true);
    const key = (e: KeyboardEvent) => { if (e.key !== "Shift") return; const on = e.type === "keydown"; if (on === this.shift) return; this.shift = on; if (this.hoverEl) { const it = this.find(this.hoverEl.dataset.item ?? ""); if (it) this.render(it, +(this.hoverEl.dataset.count ?? 1)); } };
    window.addEventListener("keydown", key); window.addEventListener("keyup", key);
  }

  setProfile(level: number, cls: string): void { this.level = level; this.cls = cls; }
  known(name: string): boolean { return !!this.find(name); }

  private find(name: string): Item | undefined {
    return findIn(this.data, name);
  }
  private leave(): void {
    if (this.hoverEl?.dataset.title) { this.hoverEl.title = this.hoverEl.dataset.title; delete this.hoverEl.dataset.title; }
    this.hoverEl = null;
    this.el.hidden = true;
  }

  /** The worn item of the same type (the first of a pair for rings / bracelets). */
  private worn(it: Item): Item | undefined {
    if (!WEARABLE.has(it.type)) return undefined;
    for (const n of EQUIPPED_NAMES) { const w = this.find(n); if (w && w.type === it.type) return w; }
    return undefined;
  }

  private render(it: Item, count: number): void {
    const worn = this.worn(it);
    const cards = [this.card(it, count, worn, false)];
    if (this.shift && worn && worn.name !== it.name) cards.push(this.card(worn, 1, undefined, true));
    this.el.replaceChildren(...cards);
    this.el.hidden = false;
    this.el.style.animation = "none"; void this.el.offsetWidth; this.el.style.animation = "";
    // One layout read per draw: the layer's box and scale and the tooltip's size, reused by every mouse move.
    const r = this.layer.getBoundingClientRect(), lw = this.layer.offsetWidth;
    this.box = { left: r.left, top: r.top, k: r.width / (lw || 1) || 1, W: lw, H: this.layer.offsetHeight, w: this.el.offsetWidth, hh: this.el.offsetHeight };
    this.place();
  }

  private box = { left: 0, top: 0, k: 1, W: 0, H: 0, w: 0, hh: 0 };
  private place(): void {
    const { left, top, k, W, H, w, hh } = this.box;
    const r = { left, top };
    let x = (this.x - r.left) / k + 18, y = (this.y - r.top) / k + 18;
    if (x + w > W - 4) x = (this.x - r.left) / k - w - 14;
    if (y + hh > H - 4) y = Math.max(4, H - hh - 4);
    this.el.style.left = Math.max(4, x) + "px"; this.el.style.top = y + "px";
  }

  private copy(it: Item): Copy { return copyOf(it); }

  private card(it: Item, count: number, worn: Item | undefined, isWorn: boolean): HTMLElement {
    const gc = GRADE[it.grade] ?? GRADE.Common!;
    const c = h("div", "tc" + (it.grade === "Legendary" || it.grade === "Mythical" || it.grade === "Heroic" ? " shine" : "") + (isWorn ? " worn" : ""));
    c.style.setProperty("--gc", gc);
    const cp = this.copy(it);
    const gem = it.type === "Gem" ? gemRules(it) : null;
    const st: Record<string, number> = gem ? { ...gem.adds } : { ...it.stats };
    for (const [k, v] of Object.entries(cp.added)) st[k] = (st[k] ?? 0) + v;
    // Header
    const ic = h("div", "ic");
    void libPicture(Lib.Items, it.img).then((p) => { if (p) ic.style.backgroundImage = `url(${p.url})`; });
    const grade = it.grade === "None" ? "" : it.grade + " ";
    const type = it.type === "Ring" || it.type === "Bracelet" ? it.type : it.type;
    const lvNeed = it.reqType === "Level" && it.req > 0;
    const lvOk = !lvNeed || this.level >= it.req;
    const head = h("div", "hd", ic, h("div", "nm", h("b", "", it.name), h("small", "", `${grade}${type}${it.set !== "None" && SET_INFO[it.set] ? " · " + SET_INFO[it.set]![0] + " set" : ""}`)));
    if (lvNeed) head.append(h("span", "lv" + (lvOk ? "" : " bad"), `Lv ${it.req}`));
    if (isWorn) c.append(h("div", "tag", "EQUIPPED"));
    c.append(head);
    // Primary: ranges for weapons and armour; restore amounts for potions
    const pri = h("div", "pri");
    const rng = (k: string) => { const lo = st["Min" + k] ?? 0, hi = st["Max" + k] ?? 0; return lo || hi ? [lo, hi] as const : null; };
    const shown = new Set<string>();
    const primary = it.type === "Weapon" ? ["DC", "MC", "SC"] : ["Armour", "Helmet"].includes(it.type) ? ["AC", "MAC"] : ["DC", "MC", "SC", "AC", "MAC"];
    if (gem) {
      for (const [k, v] of Object.entries(gem.adds)) { pri.append(h("div", "", h("b", "", `+${v}`), statName(k.replace(/^Max(?=(AC|MAC|DC|MC|SC)$)/, "")))); shown.add(k); }
    } else if (it.type === "Potion") {
      for (const k of ["HP", "MP"]) if (st[k]) { pri.append(h("div", "", h("b", "", `+${st[k]}`), `${k} restored`)); shown.add(k); }
    } else for (const k of primary) {
      const r = rng(k); if (!r) continue;
      const add = (cp.added["Max" + k] ?? 0);
      const b = h("b", "", `${r[0]}–${r[1]}`); if (add) b.append(h("em", "", `+${add}`));
      pri.append(h("div", "", b, k)); shown.add("Min" + k); shown.add("Max" + k);
      if (pri.childElementCount >= 3) break;
    }
    if (pri.childElementCount) c.append(pri);
    // Comparison against the worn item
    if (worn && !isWorn) {
      const cmp = h("div", "sec cmp");
      if (worn.name === it.name) cmp.append(h("i", "eq", "Equipped"));
      else {
        cmp.append(`vs ${worn.name}:`);
        let any = false;
        for (const [k] of RANGES) {
          const a = (st["Max" + k] ?? 0) - (worn.stats["Max" + k] ?? 0);
          if (a) { any = true; cmp.append(h("i", a > 0 ? "up" : "dn", `${a > 0 ? "▲ +" : "▼ "}${a} ${k}`)); }
        }
        for (const k of ["Accuracy", "Agility", "AttackSpeed", "HP", "MP", "Luck"]) {
          const a = (st[k] ?? 0) - (worn.stats[k] ?? 0);
          if (a) { any = true; cmp.append(h("i", a > 0 ? "up" : "dn", `${a > 0 ? "▲ +" : "▼ "}${a} ${statName(k)}`)); }
        }
        if (!any) cmp.append(h("i", "eq", "no change"));
      }
      c.append(cmp);
    }
    // Other stats
    const rest = Object.entries(st).filter(([k, v]) => v && !shown.has(k));
    if (rest.length) {
      const sec = h("div", "sec");
      const done = new Set<string>();
      for (const [k, v] of rest) {
        const m = /^(Min|Max)(AC|MAC|DC|MC|SC)$/.exec(k);
        if (m) { if (done.has(m[2]!)) continue; done.add(m[2]!); const r = rng(m[2]!)!; sec.append(h("div", "st", h("span", "", m[2]!), h("b", "", `${r[0]}–${r[1]}`))); continue; }
        const base = it.stats[k] ?? 0, add = cp.added[k] ?? 0;
        const row = h("div", "st", h("span", "", statName(k)), h("b", "", base ? `${base > 0 ? "+" : ""}${base}${pct(k)}` : ""));
        if (add) (row.lastChild as HTMLElement).append(h("em", "", `+${add}${pct(k)}`));
        sec.append(row);
      }
      c.append(sec);
    }
    // Sockets / awakening
    if (cp.gems || it.type === "Weapon" || it.type === "Armour") {
      if (it.type === "Weapon" || it.type === "Armour") {
        const sk = h("div", "sec sk", "Sockets");
        for (let i = 0; i < 3; i++) { const u = h("u", i < cp.gems ? "full" : ""); u.title = i < cp.gems ? "Gem: +1 DC (mock)" : "Empty socket"; sk.append(u); }
        c.append(sk);
      }
    }
    if (cp.awake) c.append(h("div", "sec aw", h("b", "", `Awakened DC ${cp.awake}`), h("span", "", "★".repeat(cp.awake) + "☆".repeat(5 - cp.awake)), `+${cp.awake * 2} DC`));
    // Durability / weight / light
    const dl = h("div", "sec du");
    if (it.dura && WEARABLE.has(it.type)) {
      const bar = h("div", "bar" + (cp.cur / it.dura < 0.3 ? " low" : ""), h("i")); (bar.firstChild as HTMLElement).style.width = cp.cur / it.dura * 100 + "%";
      dl.append(`Durability ${Math.ceil(cp.cur / 1000)}/${Math.ceil(cp.max / 1000)}`, bar);
    }
    dl.append(`Weight ${it.weight}`);
    if (it.light) dl.append(`· Light ${it.light}`);
    if (it.stack > 1) dl.append(`· Stack ${count}/${it.stack}`);
    c.append(dl);
    // Requirements
    const needs: (Node | string)[] = [];
    if (it.req > 0) {
      const label = it.reqType === "Level" ? `Level ${it.req}` : `${it.reqType.replace(/^Max/, "")} ${it.req}`;
      needs.push(h("span", lvOk || it.reqType !== "Level" ? "ok" : "no", `${lvOk || it.reqType !== "Level" ? "✓" : "✕"} ${label}`));
    }
    if (it.reqClass !== "None") {
      const allowed = it.reqClass === "WarWizTao" ? ["Warrior", "Wizard", "Taoist"] : it.reqClass.split(/,\s*/);
      for (const [cl, tg] of CLASSES) needs.push(h("span", "cl" + (allowed.includes(cl) ? " on" : "") + (cl === this.cls ? " me" : ""), tg));
    }
    if (it.reqGender !== "None") needs.push(h("span", it.reqGender === this.gender ? "ok" : "no", it.reqGender));
    if (needs.length) c.append(h("div", "sec rq", "Requires", ...needs));
    // Set
    const si = SET_INFO[it.set];
    if (si && this.sets[it.set]) {
      const pieces = this.sets[it.set]!;
      const wornNames = new Set(EQUIPPED_NAMES.map(norm));
      const have = pieces.filter((p) => wornNames.has(norm(p.name))).length;
      const sec = h("div", "sec set", h("b", "", `${si[0]} set (${Math.min(have, si[1])}/${si[1]})`));
      for (const p of pieces) sec.append(h("div", "pc" + (wornNames.has(norm(p.name)) ? " on" : ""), h("span", "", p.name), h("span", "", p.type)));
      for (const [k, v] of si[2]) sec.append(h("div", "bn" + (have >= si[1] ? " on" : ""), `${si[1]} pieces: ${k} ${v}`));
      c.append(sec);
    }
    if (gem) c.append(h("div", "sec", h("div", "st", h("span", "", "Success"), h("b", "", `${gem.base}%`)), h("div", "st", h("span", "", "Less per point already added"), h("b", "", `−${gem.penalty}%`)),
      h("div", "st", h("span", "", "Most gems per item"), h("b", "", String(gem.maxGems))), h("div", "st", h("span", "", "Most of this stat"), h("b", "", String(gem.maxStat))),
      h("div", "st", h("span", "", "Fits"), h("b", "", gem.fits.join(", "))), h("div", "st", h("span", "", "On failure"), h("b", "", gem.orb ? "no effect" : "20% chance to destroy the item"))));
    // Bind and special
    const chips: HTMLElement[] = [];
    for (const f of it.bind.split(/,\s*/)) if (BIND[f]) chips.push(h("span", "", BIND[f]!));
    if (!gem) for (const f of it.unique.split(/,\s*/)) if (SPECIAL[f]) chips.push(h("span", "sp", SPECIAL[f]!));
    if (chips.length) c.append(h("div", "sec bd", ...chips));
    if (it.tip && it.tip.length > 6) c.append(h("div", "sec story", it.tip));
    // Footer
    if (!isWorn) {
      c.append(h("div", "ft", h("span", "", it.price ? h("b", "", `${Math.floor(it.price / 2).toLocaleString("en-GB")} Gold`) : "No sale value", it.price ? " sell value" : ""),
        h("span", "", it.type === "Potion" || it.type === "Scroll" ? "Right-click to use" : WEARABLE.has(it.type) ? "Right-click to equip" : "")));
      const hint = h("div", "hint");
      hint.innerHTML = (worn && worn.name !== it.name ? "<kbd>Shift</kbd> compare · " : "") + "<kbd>Ctrl</kbd>+click link in chat";
      c.append(hint);
    }
    return c;
  }
}
