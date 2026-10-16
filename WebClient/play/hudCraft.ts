// Crafting window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// CraftDialog on the server's rules (RecipeInfo + NPCScript.Craft): this server's 79 recipes (Envir/Recipe/*.txt,
// converted read-only into play/lab-data/recipes.json with each item's type and Items.Lib image): result amount, chance,
// gold per craft, tools (each craft uses 1 durability point of each tool), ingredients with counts and a minimum purity
// for ores, criteria (level, class, gender). A batch of N crafts takes N x the gold and ingredients and makes ONE roll:
// Random(100) < Chance + CraftRatePercent succeeds, otherwise everything is lost (CraftingAttemptFailed). Categories
// with counts, search, "can craft now"; recipe cards with chance and how many you can make; a detail pane with the
// result (item tooltip when items.json knows it), a chance ring, gold, tools with durability, ingredients have / need,
// a quantity stepper capped by materials, gold, tool durability and stack size, a crafting bar, success / failure
// effects, a "find on the market" link for missing materials, and a log with your success rate. Your materials, gold
// and the CraftRate bonus are mock. Simulate > Crafting opens it.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { info } from "./hudItems.ts";

export interface CraftHost { gold(): number; spend(n: number): boolean; level(): number; cls(): string; say(text: string): void; market(name: string): void }
interface Recipe { name: string; amount: number; chance: number; gold: number; tools: string[]; ing: [string, number, number][]; level: number; cls: string[]; gender: string }
interface Data { recipes: Recipe[]; items: Record<string, { type: string; img: number }> }
type Cat = "All" | "Potions" | "Food" | "Gems" | "Orbs" | "Jewellery" | "Gear" | "Materials" | "Other";
const CATS: Cat[] = ["All", "Potions", "Food", "Gems", "Orbs", "Jewellery", "Gear", "Materials", "Other"];
const CRAFT_RATE = 5; // the player's Stat.CraftRatePercent (mock)
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");
const pretty = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2");
/** Item types that never stack (StackSize 1), for items items.json lacks. */
const SINGLE = ["Weapon", "Armour", "Helmet", "Necklace", "Bracelet", "Ring", "Belt", "Boots", "Stone", "Torch", "Mount"];
const hash = (s: string, salt = 0) => { let x = 2166136261 ^ salt; for (const c of s) x = Math.imul(x ^ c.charCodeAt(0), 16777619); return ((x >>> 0) % 10000) / 10000; };

const CSS = `
.hudm .cfwin { width: 1000px; }
.hudm .cfwin .panel { height: 600px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .cfwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .cfwin .cats { width: 160px; flex: none; padding: 8px; display: flex; flex-direction: column; gap: 2px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .cfwin .cats input[type=text] { height: 26px; box-sizing: border-box; padding: 0 9px; margin-bottom: 6px; border-radius: 13px; border: 1px solid var(--fr); background: #05070c; color: var(--tx); font-size: 12px; outline: none; }
.hudm .cfwin .cats input[type=text]:focus { border-color: var(--ac); }
.hudm .cfwin .ct { display: flex; justify-content: space-between; padding: 5px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.hudm .cfwin .ct:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); } .hudm .cfwin .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .cfwin .ct small { color: var(--dim); font-size: 10px; } .hudm .cfwin .ct small b { color: #6fe08a; font-weight: 700; }
.hudm .cfwin .cats label { display: flex; align-items: center; gap: 6px; margin-top: 8px; font-size: 11px; cursor: pointer; } .hudm .cfwin .cats input[type=checkbox] { accent-color: var(--ac); margin: 0; }
.hudm .cfwin .grid { flex: 1; min-width: 0; overflow-y: auto; padding: 10px; display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); grid-auto-rows: max-content; gap: 6px; align-content: start;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cfwin .rc { position: relative; display: flex; gap: 8px; align-items: center; padding: 7px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); transition: transform .12s, border-color .12s; }
.hudm .cfwin .rc:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); transform: translateY(-1px); }
.hudm .cfwin .rc.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: 0 0 10px color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .cfwin .rc.lock { opacity: .5; } .hudm .cfwin .rc .eq { width: 40px; height: 40px; }
.hudm .cfwin .rc .t { flex: 1; min-width: 0; } .hudm .cfwin .rc .t b { display: block; font-size: 12px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .cfwin .rc .t small { font-size: 10px; color: var(--dim); } .hudm .cfwin .rc .t small.ok { color: #6fe08a; } .hudm .cfwin .rc .t small.no { color: #ff8a6a; }
.hudm .cfwin .rc .ch { position: absolute; right: 6px; top: 5px; font-size: 9px; font-weight: 800; padding: 0 5px; border-radius: 6px; color: #000; }
.hudm .cfwin .det { width: 340px; flex: none; overflow-y: auto; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px;
  background: radial-gradient(ellipse 80% 30% at 50% 10%, color-mix(in srgb, var(--ac) 12%, transparent), transparent 70%); }
.hudm .cfwin .det > * { flex-shrink: 0; }
.hudm .cfwin .hero { display: flex; gap: 12px; align-items: center; }
.hudm .cfwin .res { position: relative; width: 64px; height: 64px; flex: none; border-radius: var(--rad); border: 1px solid var(--ac); background: #000 center / auto no-repeat; image-rendering: pixelated;
  box-shadow: 0 0 0 1px #000, 0 0 16px var(--glow), inset 0 0 12px color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .cfwin .res b { position: absolute; right: 3px; bottom: 1px; font-size: 11px; color: #fff; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .cfwin .res.ok { animation: hudm-cfok .9s ease-out; } @keyframes hudm-cfok { 30% { transform: scale(1.18); box-shadow: 0 0 0 1px #000, 0 0 40px #f2c94c; } }
.hudm .cfwin .res.bad { animation: hudm-cfbad .6s ease-in-out; } @keyframes hudm-cfbad { 20%, 60% { transform: translateX(-5px); filter: grayscale(1) brightness(.6); } 40%, 80% { transform: translateX(5px); filter: grayscale(1) brightness(.6); } }
.hudm .cfwin .hero h3 { margin: 0; font-size: 18px; color: #fff; } .hudm .cfwin .hero small { display: block; font-size: 11px; color: var(--dim); }
.hudm .cfwin .ring { position: relative; width: 62px; height: 62px; margin-left: auto; flex: none; }
.hudm .cfwin .ring svg { width: 62px; height: 62px; transform: rotate(-90deg); } .hudm .cfwin .ring circle { fill: none; stroke-width: 6; } .hudm .cfwin .ring .bg { stroke: #05070c; }
.hudm .cfwin .ring .fg { stroke: var(--ac); stroke-linecap: round; filter: drop-shadow(0 0 3px var(--glow)); } .hudm .cfwin .ring .bn { stroke: #6fe08a; }
.hudm .cfwin .ring div { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; } .hudm .cfwin .ring b { font-size: 15px; color: #fff; } .hudm .cfwin .ring small { font-size: 8px; color: var(--dim); }
.hudm .cfwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .cfwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .cfwin .ing { display: flex; align-items: center; gap: 8px; padding: 4px 6px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); font-size: 12px; }
.hudm .cfwin .ing .eq { width: 34px; height: 34px; } .hudm .cfwin .ing .n { flex: 1; min-width: 0; } .hudm .cfwin .ing .n small { display: block; font-size: 10px; color: var(--dim); }
.hudm .cfwin .ing .c { font-weight: 700; font-variant-numeric: tabular-nums; } .hudm .cfwin .ing .c.ok { color: #6fe08a; } .hudm .cfwin .ing .c.no { color: #ff7a6a; }
.hudm .cfwin .ing.miss { border-color: rgba(255,90,80,.5); background: rgba(255,90,80,.06); }
.hudm .cfwin .ing a { font-size: 10px; color: var(--ac); cursor: pointer; text-decoration: underline; margin-left: 4px; }
.hudm .cfwin .ing .du { height: 4px; margin-top: 3px; border-radius: 2px; background: #05070c; overflow: hidden; } .hudm .cfwin .ing .du i { display: block; height: 100%; background: var(--ac); }
.hudm .cfwin .req { display: flex; gap: 6px; flex-wrap: wrap; font-size: 11px; } .hudm .cfwin .req .ok { color: #6fe08a; } .hudm .cfwin .req .no { color: #ff7a6a; }
.hudm .cfwin .qty { display: flex; align-items: center; gap: 6px; }
.hudm .cfwin .qty .b { width: 26px; height: 26px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: 4px; border: 1px solid var(--fr); font-weight: 700; } .hudm .cfwin .qty .b:hover { border-color: var(--ac); }
.hudm .cfwin .qty input { width: 54px; height: 26px; text-align: center; border-radius: 4px; border: 1px solid var(--fr); background: #05070c; color: #fff; font-size: 14px; font-weight: 700; outline: none; }
.hudm .cfwin .qty .max { padding: 0 8px; height: 26px; display: flex; align-items: center; cursor: pointer; border-radius: 4px; border: 1px solid var(--fr); font-size: 11px; } .hudm .cfwin .qty .max:hover { border-color: var(--ac); }
.hudm .cfwin .qty .tot { margin-left: auto; font-size: 11px; color: var(--dim); text-align: right; } .hudm .cfwin .qty .tot b { color: #f2c94c; }
.hudm .cfwin .go { position: relative; height: 38px; display: flex; align-items: center; justify-content: center; cursor: pointer; overflow: hidden; border-radius: var(--rad); font-size: 15px; font-weight: 800; letter-spacing: .1em; color: #fff;
  border: 1px solid var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(12px * var(--gl)) var(--glow); }
.hudm .cfwin .go:hover { background: color-mix(in srgb, var(--ac) 50%, var(--pb)); } .hudm .cfwin .go.dis { opacity: .45; pointer-events: none; box-shadow: none; }
.hudm .cfwin .go i { position: absolute; left: 0; top: 0; bottom: 0; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 30%, transparent), var(--ac)); }
.hudm .cfwin .go span { position: relative; } .hudm .cfwin .go.run i { animation: hudm-cfbar var(--d) linear forwards; } @keyframes hudm-cfbar { to { width: 100%; } }
.hudm .cfwin .why { font-size: 11px; color: #ff8a6a; text-align: center; } .hudm .cfwin .why.ok { color: #6fe08a; } .hudm .cfwin .why.fail { color: #ff7a6a; font-weight: 700; }
.hudm .cfwin .foot { display: flex; align-items: center; gap: 14px; padding: 6px 14px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 11px; color: var(--dim); }
.hudm .cfwin .foot b { color: #fff; } .hudm .cfwin .foot .g { color: #f2c94c; } .hudm .cfwin .foot .sp { flex: 1; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.hudm .cfwin .spark { position: absolute; z-index: 9; width: 6px; height: 6px; border-radius: 50%; pointer-events: none; background: #f2c94c; box-shadow: 0 0 8px #f2c94c; animation: hudm-cfsp 1s ease-out forwards; }
@keyframes hudm-cfsp { to { transform: translate(var(--dx), var(--dy)); opacity: 0; } }
`;

export class CraftMock extends HudWindow {
  private data: Data | null = null;
  private have = new Map<string, number>();
  private toolDura = new Map<string, number>();
  private cat: Cat = "All";
  private q = "";
  private canOnly = false;
  private sel: Recipe | null = null;
  private qty = 1;
  private busy = false;
  private result: "" | "ok" | "fail" = "";
  private log: [boolean, string][] = [];

  constructor(private readonly host: CraftHost) {
    super("cfwin", 12, 24);
    injectCss("hudm-craft-css", CSS);
    void fetch(new URL("./lab-data/recipes.json", location.href)).then((r) => r.ok ? r.json() as Promise<Data> : null).then((d) => {
      if (!d) return;
      this.data = d;
      // Mock materials: enough for some recipes, short for others (stable per name).
      for (const r of d.recipes) {
        for (const [n, c] of r.ing) if (!this.have.has(n)) this.have.set(n, Math.floor(hash(n) * c * 4.2));
        for (const t of r.tools) { this.have.set(t, 1); this.toolDura.set(t, 12 + Math.floor(hash(t, 2) * 30)); }
      }
      this.sel = d.recipes.find((r) => r.name === "BugNecklace") ?? d.recipes[0]!;
      if (this.open) this.render();
    }).catch(() => {});
  }
  protected override shown(): void { this.render(); }

  private info(n: string) { return this.data?.items[n]; }
  private cat0(r: Recipe): Cat {
    const t = this.info(r.name)?.type ?? "";
    if (/Broth|Dumpling|Bone$/.test(r.name) && t !== "CraftingMaterial") return "Food";
    if (t === "Potion") return "Potions";
    if (t === "Gem") return /Orb$/.test(r.name) ? "Orbs" : "Gems";
    if (["Necklace", "Ring", "Bracelet", "Amulet"].includes(t)) return "Jewellery";
    if (["Belt", "Boots", "Helmet", "Armour", "Weapon", "Torch"].includes(t)) return "Gear";
    if (t === "CraftingMaterial") return "Materials";
    return "Other";
  }
  private meetsCriteria(r: Recipe): string[] {
    const out: string[] = [];
    if (r.level && this.host.level() < r.level) out.push(`Needs level ${r.level}`);
    if (r.cls.length && !r.cls.some((c) => c.toLowerCase() === this.host.cls().toLowerCase())) out.push(`${r.cls.join(" / ")} only`);
    return out;
  }
  /** ItemInfo.StackSize when known (items.json, else 1 for gear types), 0 when unknown. */
  private stack(n: string): number { return info(n)?.stack ?? (SINGLE.includes(this.info(n)?.type ?? "") ? 1 : 0); }
  /** The most crafts of this recipe that materials, tools, gold and the stack sizes allow (NPCScript.Craft's checks:
   *  count <= result StackSize / amount, ingredient count x crafts <= the ingredient's StackSize). */
  private maxCraft(r: Recipe): number {
    let m = 99;
    const rs = this.stack(r.name);
    if (rs) m = Math.min(m, Math.floor(rs / Math.max(1, r.amount)));
    for (const [n, c] of r.ing) { const s = this.stack(n); if (s) m = Math.min(m, Math.floor(s / Math.max(1, c))); }
    for (const [n, c] of r.ing) m = Math.min(m, Math.floor((this.have.get(n) ?? 0) / Math.max(1, c)));
    for (const t of r.tools) m = Math.min(m, (this.have.get(t) ?? 0) ? this.toolDura.get(t) ?? 0 : 0);
    if (r.gold) m = Math.min(m, Math.floor(this.host.gold() / r.gold));
    return this.meetsCriteria(r).length ? 0 : Math.max(0, m);
  }

  private render(): void {
    if (!this.data) { this.el.replaceChildren(h("div", "panel", this.titleBar("CRAFTING", "Close"), h("div", "body", h("div", "why", "Loading recipes…")))); return; }
    const rs = this.data.recipes;
    const cats = h("div", "cats");
    const s = h("input"); s.type = "text"; s.placeholder = "Search recipes…"; s.value = this.q;
    s.addEventListener("keydown", (e) => e.stopPropagation());
    s.addEventListener("input", () => { this.q = s.value; this.renderGrid(); });
    cats.append(s);
    for (const c of CATS) {
      const inCat = rs.filter((r) => c === "All" || this.cat0(r) === c);
      const can = inCat.filter((r) => this.maxCraft(r) > 0).length;
      const e = h("div", "ct" + (this.cat === c ? " on" : ""), h("span", "", c), h("small", "", h("b", "", can ? `${can} ` : ""), `/ ${inCat.length}`));
      e.title = `${can} you can craft now`;
      e.addEventListener("click", () => { this.cat = c; this.render(); });
      cats.append(e);
    }
    const cb = h("input"); cb.type = "checkbox"; cb.checked = this.canOnly; cb.addEventListener("change", () => { this.canOnly = cb.checked; this.renderGrid(); });
    cats.append(h("label", "", cb, "Can craft now"));
    const done = this.log.length, ok = this.log.filter((l) => l[0]).length;
    const foot = h("div", "foot", h("span", "", "Gold ", h("b", "g", fmt(this.host.gold()))), h("span", "", "Craft rate bonus ", h("b", "", `+${CRAFT_RATE}%`)),
      h("span", "", "Session ", h("b", "", done ? `${ok}/${done} succeeded` : "no crafts yet")), h("span", "sp", this.log[0]?.[1] ?? ""));
    this.el.replaceChildren(h("div", "panel", this.titleBar("CRAFTING", "Close"), h("div", "body", cats, h("div", "grid"), this.detail()), foot));
    this.renderGrid();
  }

  private renderGrid(): void {
    const grid = this.el.querySelector<HTMLElement>(".grid");
    if (!grid || !this.data) return;
    const q = this.q.trim().toLowerCase();
    const list = this.data.recipes.filter((r) => (this.cat === "All" || this.cat0(r) === this.cat) && (!q || r.name.toLowerCase().includes(q) || r.ing.some(([n]) => n.toLowerCase().includes(q))))
      .map((r) => [r, this.maxCraft(r)] as const).filter(([, m]) => !this.canOnly || m > 0).sort((a, b) => (b[1] > 0 ? 1 : 0) - (a[1] > 0 ? 1 : 0) || a[0].name.localeCompare(b[0].name));
    grid.replaceChildren(...list.map(([r, m]) => {
      const crit = this.meetsCriteria(r);
      const i = this.info(r.name);
      const ch = h("span", "ch", `${r.chance}%`); ch.style.background = r.chance >= 100 ? "#6fe08a" : r.chance >= 50 ? "#f2c94c" : "#ff8a6a";
      const c = h("div", "rc" + (this.sel === r ? " sel" : "") + (crit.length ? " lock" : ""), itemCell(this.el, r.name, i?.img ?? -1, r.amount),
        h("div", "t", h("b", "", pretty(r.name)), h("small", crit.length ? "no" : m ? "ok" : "", crit[0] ?? (m ? `Can make ${m}` : "Missing materials"))), ch);
      c.addEventListener("click", () => { this.sel = r; this.qty = 1; this.result = ""; this.render(); });
      return c;
    }));
    if (!list.length) grid.append(h("div", "why", "No recipes match."));
  }

  private detail(): HTMLElement {
    const d = h("div", "det");
    const r = this.sel;
    if (!r) return d;
    const i = this.info(r.name);
    const res = h("div", "res" + (this.result === "ok" ? " ok" : this.result === "fail" ? " bad" : ""), ...(r.amount > 1 ? [h("b", "", `×${r.amount}`)] : []));
    res.dataset.item = r.name;
    void libPicture(Lib.Items, i?.img ?? -1).then((p) => { if (p) res.style.backgroundImage = `url(${p.url})`; });
    const total = Math.min(100, r.chance + CRAFT_RATE), C = 2 * Math.PI * 26;
    const ring = h("div", "ring");
    ring.innerHTML = `<svg viewBox="0 0 62 62"><circle class="bg" cx="31" cy="31" r="26"/><circle class="bn" cx="31" cy="31" r="26" stroke-dasharray="${C}" stroke-dashoffset="${C * (1 - total / 100)}"/><circle class="fg" cx="31" cy="31" r="26" stroke-dasharray="${C}" stroke-dashoffset="${C * (1 - r.chance / 100)}"/></svg>`;
    ring.append(h("div", "", h("b", "", `${total}%`), h("small", "", "SUCCESS")));
    ring.title = `Recipe ${r.chance}% + your craft rate ${CRAFT_RATE}%`;
    d.append(h("div", "hero", res, h("div", "", h("h3", "", pretty(r.name)), h("small", "", `${i?.type ?? ""} · makes ${r.amount}`), h("small", "", `${fmt(r.gold)} gold per craft`)), ring));
    const crit = this.meetsCriteria(r);
    if (r.level || r.cls.length || r.gender) {
      const req = h("div", "req");
      if (r.level) req.append(h("span", this.host.level() >= r.level ? "ok" : "no", `${this.host.level() >= r.level ? "✓" : "✕"} Level ${r.level}`));
      if (r.cls.length) req.append(h("span", crit.some((c) => c.endsWith("only")) ? "no" : "ok", r.cls.join(" / ")));
      if (r.gender) req.append(h("span", "", r.gender));
      d.append(req);
    }
    const max = this.maxCraft(r);
    const q = Math.max(1, Math.min(this.qty, Math.max(1, max)));
    if (r.tools.length) {
      d.append(h("div", "lbl", "Tools", h("b", "", "1 durability per craft")));
      for (const t of r.tools) {
        const own = (this.have.get(t) ?? 0) > 0, du = this.toolDura.get(t) ?? 0;
        const bar = h("div", "du", h("i")); (bar.firstChild as HTMLElement).style.width = Math.min(100, du / 42 * 100) + "%";
        d.append(h("div", "ing" + (own && du >= q ? "" : " miss"), itemCell(this.el, t, this.info(t)?.img ?? -1), h("div", "n", pretty(t), own ? bar : h("small", "", "You don't have this tool")), h("span", "c " + (own && du >= q ? "ok" : "no"), own ? `${du} left` : "0")));
      }
    }
    d.append(h("div", "lbl", "Ingredients", h("b", "", `for ${q} craft${q > 1 ? "s" : ""}`)));
    for (const [n, c, dura] of r.ing) {
      const need = c * q, has = this.have.get(n) ?? 0, ok = has >= need;
      const link = h("a", "", "find on market"); link.addEventListener("click", () => this.host.market(n));
      d.append(h("div", "ing" + (ok ? "" : " miss"), itemCell(this.el, n, this.info(n)?.img ?? -1, c), h("div", "n", pretty(n), ...(dura ? [h("small", "", `Purity ${dura / 1000}+`)] : []), ...(ok ? [] : [link])),
        h("span", "c " + (ok ? "ok" : "no"), `${has} / ${need}`)));
    }
    const inp = h("input"); inp.value = String(q);
    inp.addEventListener("keydown", (e) => e.stopPropagation());
    inp.addEventListener("change", () => { this.qty = Math.max(1, Math.min(max || 1, +inp.value || 1)); this.render(); });
    const b = (t: string, fn: () => void) => { const e = h("div", "b", t); e.addEventListener("click", fn); return e; };
    const mx = h("div", "max", `Max ${max}`); mx.addEventListener("click", () => { this.qty = Math.max(1, max); this.render(); });
    d.append(h("div", "lbl", "Quantity"), h("div", "qty", b("−", () => { this.qty = Math.max(1, q - 1); this.render(); }), inp, b("+", () => { this.qty = Math.min(Math.max(1, max), q + 1); this.render(); }), mx,
      h("div", "tot", "Makes ", h("b", "", String(r.amount * q)), h("br"), "Costs ", h("b", "", fmt(r.gold * q)))));
    const why = crit[0] ?? (max ? "" : r.gold && this.host.gold() < r.gold ? "Not enough gold" : r.tools.some((t) => !(this.have.get(t) ?? 0)) ? "You need the tool" : "Not enough materials");
    const go = h("div", "go" + (max && !this.busy ? "" : " dis"), h("i"), h("span", "", this.busy ? "CRAFTING…" : `CRAFT ×${q}`));
    go.addEventListener("click", () => this.craft(r, q, go));
    d.append(go);
    if (this.result) d.append(h("div", "why " + (this.result === "ok" ? "ok" : "fail"), this.result === "ok" ? `Crafted ${pretty(r.name)}!` : "The attempt failed. The materials were lost."));
    if (why) d.append(h("div", "why", why));
    else if (this.result) { /* shown above */ }
    else if (total < 100) d.append(h("div", "why", `${100 - total}% chance to fail: the whole batch and its materials would be lost.`));
    return d;
  }

  /** NPCScript.Craft: take gold and ingredients, use tool durability, one roll for the batch. */
  private craft(r: Recipe, q: number, go: HTMLElement): void {
    if (this.busy || q > this.maxCraft(r)) return;
    this.busy = true; this.result = "";
    const dur = Math.min(3000, 900 + q * 120);
    go.style.setProperty("--d", dur + "ms"); go.classList.add("run"); go.querySelector("span")!.textContent = "CRAFTING…";
    window.setTimeout(() => {
      if (!this.host.spend(r.gold * q)) { this.busy = false; this.render(); return; }
      for (const [n, c] of r.ing) this.have.set(n, (this.have.get(n) ?? 0) - c * q);
      for (const t of r.tools) this.toolDura.set(t, (this.toolDura.get(t) ?? 0) - q);
      const ok = Math.random() * 100 < r.chance + CRAFT_RATE;
      this.result = ok ? "ok" : "fail";
      if (ok) this.have.set(r.name, (this.have.get(r.name) ?? 0) + r.amount * q);
      const msg = ok ? `Crafted ${r.amount * q} ${pretty(r.name)} for ${fmt(r.gold * q)} gold.` : `Crafting ${pretty(r.name)} ×${q} failed.`;
      this.log.unshift([ok, msg]); this.host.say(msg);
      this.busy = false; this.render();
      if (ok) this.sparks();
    }, dur);
  }
  private sparks(): void {
    const res = this.el.querySelector<HTMLElement>(".res"), panel = this.el.querySelector<HTMLElement>(".panel");
    if (!res || !panel) return;
    const pr = panel.getBoundingClientRect(), rr = res.getBoundingClientRect(), k = pr.width / panel.offsetWidth || 1;
    const cx = (rr.left + rr.width / 2 - pr.left) / k, cy = (rr.top + rr.height / 2 - pr.top) / k;
    for (let i = 0; i < 28; i++) {
      const s = h("div", "spark"), a = Math.random() * Math.PI * 2, dd = 40 + Math.random() * 90;
      Object.assign(s.style, { left: cx + "px", top: cy + "px" });
      s.style.setProperty("--dx", Math.cos(a) * dd + "px"); s.style.setProperty("--dy", Math.sin(a) * dd + "px");
      panel.append(s);
    }
    window.setTimeout(() => { for (const s of panel.querySelectorAll(".spark")) s.remove(); }, 1100);
  }
}
