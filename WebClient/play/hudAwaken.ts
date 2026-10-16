// Awakening window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// NPCAwakeDialog on the server's rules (Shared Awake + AwakeningSystem.ini defaults; PlayerObject.Awakening /
// DowngradeAwakening / HasAwakeningNeedMaterials; UserItem.AwakeningPrice / DowngradePrice): only weapons (DC / MC / SC),
// helmets (AC / MAC) and armour (HP / MP) that CanAwakening, have a grade and are not DontUpgrade; the first awakening
// fixes the type; MaxUpgradeLevel 5. Each try costs 1,500 x (1 + 2 x level) x grade gold plus that grade's glyph for the
// type (ItemType.Awakening, Shape = type - 1) and an AwakeningSoul (Shape 100), 1 + level of each. Success when
// Random(0, 100) <= SuccessRate 70 (71 %): five hit orbs roll HitRate 70 % each, every hit adds ChanceMax[grade] / 5
// (at least 1 in total), times WeaponValue 1 / HelmetValue 1 / ArmorValue 5. On a failure the server deletes the item.
// Downgrade removes the last level for 3,000 x (1 + 2 x (level + 1)) x grade gold, with a 1 in 20 chance of losing a
// point of max durability. A rune circle with the item and its five level pips, the type picker, the five hit orbs
// lighting during the roll, materials have / need with the real glyph icons, price, odds, a hold-to-confirm Awaken
// (it can destroy the item) and Downgrade, and a log. Item stats and images are real (items.json; glyphs Items 3200-3227);
// your items' current levels and material counts are mock.

import { findIn, GRADE, itemData, norm, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface AwakenHost { gold(): number; spend(n: number): boolean; say(text: string): void }
type AType = "DC" | "MC" | "SC" | "AC" | "MAC" | "HPMP";
const ATYPES: AType[] = ["DC", "MC", "SC", "AC", "MAC", "HPMP"];
const SUCCESS = 70, HIT = 70, MAX = 5;
const GRADE_N: Record<string, number> = { Common: 1, Rare: 2, Legendary: 3, Mythical: 4, Heroic: 5 };
const RATE: Record<string, number> = { Weapon: 1, Helmet: 1, Armour: 5 };
const GLYPH = ["Bravery", "Magic", "Soul", "Protection", "EvilSlayer", "Body"];
/** Glyph / soul images: Items 3200 + glyph * 4 + grade - 1; AwakeningSoul 3224 + grade - 1 (no Heroic glyphs exist). */
const glyphImg = (t: AType, g: number) => 3200 + ATYPES.indexOf(t) * 4 + Math.min(4, g) - 1;
const soulImg = (g: number) => 3224 + Math.min(4, g) - 1;
const glyphName = (t: AType, g: number) => `${GLYPH[ATYPES.indexOf(t)]}Glyph${Math.min(4, g) - 1}`;
const soulName = (g: number) => `AwakeningSoul${Math.min(4, g) - 1}`;
const typesFor = (type: string): AType[] => type === "Weapon" ? ["DC", "MC", "SC"] : type === "Helmet" ? ["AC", "MAC"] : type === "Armour" ? ["HPMP"] : [];
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");
interface Gear { id: number; name: string; type: AType | null; levels: number[]; maxDura: number }

const CSS = `
.hudm .awwin { width: 980px; }
.hudm .awwin .panel { height: 580px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .awwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .awwin .list { width: 230px; flex: none; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 5px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .awwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .awwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .awwin .agi { display: flex; gap: 8px; align-items: center; padding: 6px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .awwin .agi:hover { border-color: color-mix(in srgb, var(--ac) 55%, transparent); } .hudm .awwin .agi.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .awwin .agi.no { opacity: .45; cursor: not-allowed; } .hudm .awwin .agi .eq { width: 38px; height: 38px; }
.hudm .awwin .agi .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .awwin .agi .t b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .awwin .agi .t small { font-size: 10px; color: var(--dim); }
.hudm .awwin .stars { color: #f2c94c; letter-spacing: 1px; font-size: 10px; }
.hudm .awwin .stage { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; position: relative;
  background: radial-gradient(circle at 50% 45%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 55%); }
.hudm .awwin .circle { position: relative; width: 250px; height: 250px; }
.hudm .awwin .circle svg.r { position: absolute; inset: 0; width: 250px; height: 250px; }
.hudm .awwin .circle .r1 { animation: hudm-awspin 30s linear infinite; } .hudm .awwin .circle .r2 { animation: hudm-awspin 18s linear infinite reverse; }
.hudm .awwin .circle.run .r1 { animation-duration: 3s; } .hudm .awwin .circle.run .r2 { animation-duration: 2s; }
@keyframes hudm-awspin { to { transform: rotate(360deg); } }
.hudm .awwin .circle circle, .hudm .awwin .circle path { fill: none; stroke: var(--ac); }
.hudm .awwin .acore { position: absolute; left: 50%; top: 50%; translate: -50% -50%; }
.hudm .awwin .acore .eq { width: 72px; height: 72px; border-radius: 50%; box-shadow: 0 0 0 1px #000, 0 0 24px var(--glow); }
.hudm .awwin .acore .eq::after { border-radius: 50%; }
.hudm .awwin .acore.boom .eq { animation: hudm-awboom 1s ease-in forwards; } @keyframes hudm-awboom { 40% { transform: scale(1.3); filter: brightness(3); } 100% { transform: scale(0); opacity: 0; } }
.hudm .awwin .acore.up .eq { animation: hudm-awup 1.2s ease-out; } @keyframes hudm-awup { 30% { transform: scale(1.2); box-shadow: 0 0 0 1px #000, 0 0 60px #f2c94c; } }
.hudm .awwin .pip { position: absolute; width: 22px; height: 22px; margin: -11px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800;
  border: 1px solid var(--fr); background: #05070c; color: var(--dim); }
.hudm .awwin .pip.on { color: #000; background: #f2c94c; border-color: #fff; box-shadow: 0 0 10px #f2c94c; }
.hudm .awwin .orbs { display: flex; gap: 10px; } .hudm .awwin .orb { width: 26px; height: 26px; border-radius: 50%; border: 1px solid var(--fr); background: radial-gradient(circle at 35% 35%, #223, #05070c); transition: all .2s; }
.hudm .awwin .orb.hit { border-color: #fff; background: radial-gradient(circle at 35% 35%, #fff, var(--ac) 50%, #024); box-shadow: 0 0 14px var(--glow); }
.hudm .awwin .orb.miss { border-color: #622; background: radial-gradient(circle at 35% 35%, #533, #100); }
.hudm .awwin .types { display: flex; gap: 6px; }
.hudm .awwin .types div { padding: 5px 14px; border-radius: 14px; cursor: pointer; font-size: 12px; font-weight: 700; border: 1px solid var(--fr); background: var(--pb); }
.hudm .awwin .types div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 10px var(--glow); }
.hudm .awwin .types div.lock { opacity: .3; cursor: not-allowed; }
.hudm .awwin .lv { display: flex; gap: 6px; font-size: 11px; color: var(--dim); } .hudm .awwin .lv span { padding: 2px 8px; border-radius: 8px; border: 1px solid var(--fr); } .hudm .awwin .lv b { color: #f2c94c; }
.hudm .awwin .head { text-align: center; } .hudm .awwin .head b { font-size: 18px; } .hudm .awwin .head small { display: block; font-size: 11px; color: var(--dim); }
.hudm .awwin .side { width: 320px; flex: none; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .awwin .side > * { flex-shrink: 0; }
.hudm .awwin .mat { display: flex; align-items: center; gap: 8px; padding: 5px 7px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); font-size: 12px; }
.hudm .awwin .mat .eq { width: 36px; height: 36px; } .hudm .awwin .mat .n { flex: 1; } .hudm .awwin .mat .c { font-weight: 700; } .hudm .awwin .mat .c.ok { color: #6fe08a; } .hudm .awwin .mat .c.no { color: #ff7a6a; }
.hudm .awwin .mat.miss { border-color: rgba(255,90,80,.5); }
.hudm .awwin .kv { display: flex; justify-content: space-between; font-size: 12px; padding: 2px 0; } .hudm .awwin .kv span { color: var(--dim); } .hudm .awwin .kv b { color: #fff; } .hudm .awwin .kv b.g { color: #f2c94c; } .hudm .awwin .kv b.r { color: #ff7a6a; }
.hudm .awwin .danger { padding: 8px 10px; border-radius: var(--rad); font-size: 11px; line-height: 1.4; color: #ffb3a6; border: 1px solid #c44; background: rgba(200,50,50,.12); }
.hudm .awwin .danger b { color: #ff7a6a; }
.hudm .awwin .hold { position: relative; height: 40px; overflow: hidden; display: flex; align-items: center; justify-content: center; cursor: pointer; user-select: none; border-radius: var(--rad); font-size: 14px; font-weight: 800; letter-spacing: .08em; color: #fff;
  border: 1px solid var(--ac); background: color-mix(in srgb, var(--ac) 30%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 14px var(--glow); }
.hudm .awwin .hold i { position: absolute; left: 0; top: 0; bottom: 0; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), #f2c94c); }
.hudm .awwin .hold.down i { width: 100%; transition: width 1.2s linear; } .hudm .awwin .hold span { position: relative; } .hudm .awwin .hold.dis { opacity: .4; pointer-events: none; }
.hudm .awwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .awwin .why { font-size: 11px; color: #ff8a6a; text-align: center; } .hudm .awwin .why.ok { color: #6fe08a; }
.hudm .awwin .log { font-size: 11px; color: var(--dim); } .hudm .awwin .log div { padding: 2px 0; }
.hudm .awwin .res { text-align: center; font-size: 14px; font-weight: 800; padding: 8px; border-radius: var(--rad); } .hudm .awwin .res.up { color: #f2c94c; background: rgba(242,201,76,.1); } .hudm .awwin .res.boom { color: #ff7a6a; background: rgba(220,60,60,.12); }
`;

export class AwakenMock extends HudWindow {
  private items = new Map<string, Item>();
  private gear: Gear[] = [
    { id: 1, name: "DragonSlayer", type: "DC", levels: [1, 2], maxDura: 33 }, { id: 2, name: "HeavenSword", type: null, levels: [], maxDura: 25 },
    { id: 3, name: "TitanArmour(M)", type: "HPMP", levels: [5], maxDura: 28 }, { id: 4, name: "CrystalArmour(M)", type: null, levels: [], maxDura: 25 },
    { id: 5, name: "HelmetOfHero", type: null, levels: [], maxDura: 10 }, { id: 6, name: "BrassHelmet", type: "AC", levels: [1, 1, 1, 1, 1], maxDura: 8 },
    { id: 7, name: "GoldDragonRing", type: null, levels: [], maxDura: 7 }, { id: 8, name: "SealedHero", type: null, levels: [], maxDura: 1 },
  ];
  private mats = new Map<string, number>();
  private sel: Gear = this.gear[0]!;
  private pick: AType = "DC";
  private busy = false;
  private orbs: ("" | "hit" | "miss")[] = ["", "", "", "", ""];
  private fx: "" | "up" | "boom" = "";
  private msg: [string, string] = ["", ""];
  private log: string[] = [];

  constructor(private readonly host: AwakenHost) {
    super("awwin", 20, 30);
    injectCss("hudm-awaken-css", CSS);
    for (let g = 1; g <= 4; g++) { for (const t of ATYPES) this.mats.set(glyphName(t, g), [6, 3, 1, 0][g - 1]! + (t === "DC" ? 2 : 0)); this.mats.set(soulName(g), [8, 5, 2, 0][g - 1]!); }
    void itemData().then((d) => { if (d) for (const i of d.items) this.items.set(norm(i.name), i); if (this.open) this.render(); });
  }
  protected override shown(): void { this.render(); }
  private item(n: string): Item | undefined { return findIn(this.items, n); }
  /** Awake.CheckAwakening's reasons, or "". */
  private blocked(g: Gear): string {
    const it = this.item(g.name);
    if (!it) return "Unknown item";
    if (!this.gear.includes(g)) return "Destroyed";
    if (it.bind.includes("DontUpgrade")) return "This item can't be upgraded";
    if (!it.awaken) return "This item can't be awakened";
    if (it.grade === "None") return "Items without a grade can't be awakened";
    if (!typesFor(it.type).length) return "Only weapons, helmets and armour";
    if (g.levels.length >= MAX) return "Fully awakened";
    return "";
  }

  private render(): void {
    const list = h("div", "list", h("div", "lbl", "Your gear", h("b", "", "weapon · helmet · armour")));
    for (const g of this.gear) {
      const it = this.item(g.name), why = this.blocked(g);
      const nm = h("b", "", g.name); nm.style.color = GRADE[it?.grade ?? "Common"]!;
      const e = h("div", "agi" + (this.sel === g ? " sel" : "") + (why && why !== "Fully awakened" ? " no" : ""), itemCell(this.el, g.name, it?.img ?? -1),
        h("div", "t", nm, h("small", "", g.levels.length ? h("span", "stars", "★".repeat(g.levels.length) + "☆".repeat(MAX - g.levels.length)) : why || `${it?.grade} ${it?.type}`)));
      e.title = why;
      e.addEventListener("click", () => { if (this.busy) return; this.sel = g; this.pick = g.type ?? typesFor(it?.type ?? "")[0] ?? "DC"; this.fx = ""; this.msg = ["", ""]; this.orbs = ["", "", "", "", ""]; this.render(); });
      list.append(e);
    }
    this.el.replaceChildren(h("div", "panel", this.titleBar("AWAKENING", "Close"), h("div", "body", list, this.stage(), this.side())));
  }

  private stage(): HTMLElement {
    const g = this.sel, it = this.item(g.name);
    const circle = h("div", "circle" + (this.busy ? " run" : ""));
    circle.innerHTML = `<svg class="r r1" viewBox="0 0 250 250"><circle cx="125" cy="125" r="118" stroke-width="1" opacity=".5"/><circle cx="125" cy="125" r="108" stroke-width="2" stroke-dasharray="4 10" opacity=".8"/>
      <path d="M125 12 L223 182 L27 182 Z" stroke-width="1" opacity=".35"/><path d="M125 238 L27 68 L223 68 Z" stroke-width="1" opacity=".35"/></svg>
      <svg class="r r2" viewBox="0 0 250 250"><circle cx="125" cy="125" r="90" stroke-width="1.5" stroke-dasharray="20 6 2 6" opacity=".6"/><circle cx="125" cy="125" r="62" stroke-width="1" opacity=".4"/></svg>`;
    const core = h("div", "acore" + (this.fx ? " " + this.fx : ""), itemCell(this.el, g.name, it?.img ?? -1));
    circle.append(core);
    for (let i = 0; i < MAX; i++) {
      const a = -Math.PI / 2 + i * 2 * Math.PI / MAX;
      const p = h("div", "pip" + (i < g.levels.length ? " on" : ""), String(i + 1));
      Object.assign(p.style, { left: 125 + Math.cos(a) * 104 + "px", top: 125 + Math.sin(a) * 104 + "px" });
      p.title = i < g.levels.length ? `Level ${i + 1}: +${g.levels[i]}` : `Level ${i + 1}`;
      circle.append(p);
    }
    const types = h("div", "types");
    for (const t of typesFor(it?.type ?? "")) {
      const locked = !!g.type && g.type !== t;
      const d = h("div", (this.pick === t ? "on" : "") + (locked ? " lock" : ""), t === "HPMP" ? "HP / MP" : t);
      d.title = locked ? `This item is awakened in ${g.type}; the type can't change` : "";
      d.addEventListener("click", () => { if (!locked && !this.busy) { this.pick = t; this.render(); } });
      types.append(d);
    }
    const orbs = h("div", "orbs"); for (const o of this.orbs) orbs.append(h("div", "orb " + o));
    const total = g.levels.reduce((a, b) => a + b, 0);
    const lv = h("div", "lv", ...g.levels.map((v, i) => h("span", "", `Lv${i + 1} `, h("b", "", `+${v}`))));
    const nm = h("b", "", g.name); nm.style.color = GRADE[it?.grade ?? "Common"]!;
    return h("div", "stage", h("div", "head", nm, h("small", "", `${it?.grade ?? ""} ${it?.type ?? ""} · awakening ${g.levels.length}/${MAX}${g.type ? ` · ${g.type === "HPMP" ? "HP/MP" : g.type} +${total}` : ""}`)),
      circle, types, orbs, g.levels.length ? lv : h("div", "lv", "Not awakened yet: the first awakening fixes the stat."));
  }

  private side(): HTMLElement {
    const s = h("div", "side");
    const g = this.sel, it = this.item(g.name), why = this.blocked(g);
    const grade = GRADE_N[it?.grade ?? ""] ?? 1, L = g.levels.length;
    const need = 1 + L; // base 1 + Materials_IncreaseValue 1 x level
    const price = 1500 * (1 + L * 2) * grade, down = 3000 * (1 + (L + 1) * 2) * grade;
    const gold = this.host.gold();
    if (why && why !== "Fully awakened") { s.append(h("div", "danger", h("b", "", why), ".")); if (this.msg[0]) s.append(h("div", "res " + this.msg[1], this.msg[0])); return s; }
    s.append(h("div", "lbl", "Materials", h("b", "", `${it?.grade} grade · ${need} each`)));
    let haveAll = true;
    for (const [n, img] of [[glyphName(this.pick, grade), glyphImg(this.pick, grade)], [soulName(grade), soulImg(grade)]] as const) {
      const has = this.mats.get(n) ?? 0, ok = has >= need; if (!ok) haveAll = false;
      s.append(h("div", "mat" + (ok ? "" : " miss"), itemCell(this.el, n, img), h("div", "n", n.replace(/(\d)$/, " ($1)").replace(/([a-z])([A-Z])/g, "$1 $2")), h("span", "c " + (ok ? "ok" : "no"), `${has} / ${need}`)));
    }
    const per = (GRADE_N[it?.grade ?? ""] ?? 1) / 5, rate = RATE[it?.type ?? ""] ?? 0;
    s.append(h("div", "lbl", "Odds"),
      h("div", "kv", h("span", "", "Success"), h("b", "", `${SUCCESS + 1}%`)),
      h("div", "kv", h("span", "", "Each hit orb"), h("b", "", `${HIT}% · +${per.toFixed(1)}`)),
      h("div", "kv", h("span", "", "Gain on success"), h("b", "", `${Math.max(1, Math.floor(per))}–${Math.max(1, Math.floor(per * 5))} ×${rate}`)),
      h("div", "kv", h("span", "", "Price"), h("b", gold < price ? "r" : "g", `${fmt(price)} gold`)));
    s.append(h("div", "danger", h("b", "", `${100 - SUCCESS - 1}% chance to destroy ${g.name}.`), " A failed awakening deletes the item and uses up the gold and materials."));
    const ok = !why && haveAll && gold >= price && !this.busy;
    const hold = h("div", "hold" + (ok ? "" : " dis"), h("i"), h("span", "", why === "Fully awakened" ? "FULLY AWAKENED" : "HOLD TO AWAKEN"));
    let t = 0;
    const cancel = () => { window.clearTimeout(t); hold.classList.remove("down"); };
    hold.addEventListener("mousedown", () => { hold.classList.add("down"); t = window.setTimeout(() => { cancel(); this.awaken(price, need); }, 1200); });
    hold.addEventListener("mouseup", cancel); hold.addEventListener("mouseleave", cancel);
    s.append(hold);
    if (!ok && !why && !this.busy) s.append(h("div", "why", !haveAll ? "Not enough materials" : "Not enough gold"));
    if (this.msg[0]) s.append(h("div", "res " + this.msg[1], this.msg[0]));
    const db = h("div", "btn" + (L && gold >= down && !this.busy ? "" : " dis"), L ? `Downgrade to level ${L - 1} · ${fmt(down)} gold` : "Downgrade (nothing to remove)");
    db.addEventListener("click", () => this.downgrade(down));
    s.append(db, h("div", "why", L ? `Removes level ${L} (+${g.levels[L - 1]}). 1 in 20 chance to lose 1 max durability.` : ""));
    if (this.log.length) { const lg = h("div", "log"); for (const l of this.log.slice(0, 6)) lg.append(h("div", "", l)); s.append(h("div", "lbl", "History"), lg); }
    return s;
  }

  /** PlayerObject.Awakening + Awake.UpgradeAwake / MakeHit. */
  private awaken(price: number, need: number): void {
    const g = this.sel, it = this.item(g.name)!;
    const grade = GRADE_N[it.grade] ?? 1;
    // HasAwakeningNeedMaterials + the gold check (also guards a programmatic click on the disabled button).
    if (this.busy || this.blocked(g) || [glyphName(this.pick, grade), soulName(grade)].some((n) => (this.mats.get(n) ?? 0) < need) || !this.host.spend(price)) return;
    for (const n of [glyphName(this.pick, grade), soulName(grade)]) this.mats.set(n, (this.mats.get(n) ?? 0) - need);
    this.busy = true; this.fx = ""; this.msg = ["", ""]; this.orbs = ["", "", "", "", ""];
    const success = Math.floor(Math.random() * 100) <= SUCCESS;
    const hits = Array.from({ length: 5 }, () => Math.floor(Math.random() * 100) < HIT);
    this.render();
    hits.forEach((hit, i) => window.setTimeout(() => { this.orbs[i] = hit ? "hit" : "miss"; this.el.querySelectorAll(".orb")[i]!.className = "orb " + this.orbs[i]; }, 300 + i * 320));
    window.setTimeout(() => {
      this.busy = false;
      if (success) {
        const step = grade / 5, total = hits.filter(Boolean).length * step;
        const gain = (total <= 1 ? 1 : Math.floor(total)) * (RATE[it.type] ?? 0);
        if (!g.type) g.type = this.pick;
        g.levels.push(gain);
        this.fx = "up"; this.msg = [`✦ ${g.name} reached awakening ${g.levels.length}: +${gain} ${g.type === "HPMP" ? "HP/MP" : g.type}`, "up"];
      } else {
        const at = this.gear.indexOf(g);
        if (at >= 0) this.gear.splice(at, 1);
        this.fx = "boom"; this.msg = [`💥 The awakening failed. ${g.name} was destroyed.`, "boom"];
      }
      this.log.unshift(this.msg[0]); this.host.say(this.msg[0]);
      this.render();
      // The destroyed item stays on show (blocked: "Destroyed") until the next one is picked.
      if (!success) window.setTimeout(() => { if (this.sel === g && this.gear.length) { this.sel = this.gear[0]!; this.fx = ""; this.render(); } }, 1800);
    }, 300 + 5 * 320 + 300);
  }
  /** PlayerObject.DowngradeAwakening. */
  private downgrade(price: number): void {
    const g = this.sel;
    if (!g.levels.length || this.busy || !this.host.spend(price)) return;
    const v = g.levels.pop()!;
    if (!g.levels.length) g.type = null;
    const lose = Math.floor(Math.random() * 20) === 0;
    if (lose) g.maxDura = Math.max(1, g.maxDura - 1);
    this.msg = [`${g.name} lost awakening level ${g.levels.length + 1} (+${v}).${lose ? " Its max durability dropped by 1." : ""}`, ""];
    this.log.unshift(this.msg[0]); this.host.say(this.msg[0]); this.fx = ""; this.orbs = ["", "", "", "", ""];
    this.render();
  }
}
