// NPC merchant mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// NPCGoodsDialog / NPC sell / repair pages: the merchant's greeting and your gold, Buy (category chips, search, rows with
// level requirement, price and stack size, a cart with quantities and a total), Sell (the shared bag, hudItems.ts: pick
// cells, half of UserItem.Price() each, DontSell items dimmed, a "select junk" shortcut; selling takes them out of the
// bag), Repair and Buy back (what you sold this visit, back into the bag for what you were paid). Prices are the items' ItemInfo.Price from a copy
// of the live Server.MirDB (2026-10-15); sell is half the price (UserItem.Price() / 2 for undamaged items). Gold is the
// HUD's (MerchantHost). Repair (2026-10-16, the only repair in the mock; it replaced the NPC item box's repair and
// special repair) lists your worn gear and the damaged items in your bag in one list: durability now, what the repair
// restores and, for a normal repair, the maximum it wears off (a normal repair that wears any asks first, with a
// "don't ask again" for the visit); per-item Repair, Repair equipped (the main button: what
// most players want), Repair all (worn gear and bag), and a Special repair switch
// (3x the price, the maximum kept). Rules and per-item state: hudRepair.ts (the game's RepairPrice / RepairItem), shared
// with the item tooltip and the HUD's durability strip. Mock only.

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { BAG_SLOTS, addToBag, bag, bagChanged, freeCells, hasBind, info, onBag, type Stack } from "./hudItems.ts";
import { wornGear } from "./hudCharacter.ts";
import { applyRepair, duraOf, itemPrice, repairCost, repairLoss, repairWhy } from "./hudRepair.ts";
import type { ConfirmOpts } from "./hudPopup.ts";

export interface MerchantHost {
  gold(): number; spend(n: number): boolean; earn(n: number): void; say(text: string): void; gearChanged(): void;
  /** A Yes / No box (hudPopup.ts): resolves "yes" / "no" / null, and whether "don't ask again" was ticked. */
  confirm(o: ConfirmOpts): Promise<{ answer: string; dontAsk: boolean } | null>;
}

type Tab = "Buy" | "Sell" | "Repair" | "Buy back";
type Cat = "Potions" | "Scrolls" | "Weapons" | "Armour" | "Accessories" | "Lighting";
/** name, image, price (ItemInfo.Price), stack size, weight, required level, category, grade */
type Good = [string, number, number, number, number, number, Cat, string];
const GOODS: Good[] = [
  ["(HP)DrugSmall", 398, 40, 20, 1, 0, "Potions", ""], ["(MP)DrugSmall", 394, 40, 20, 1, 0, "Potions", ""], ["(HP)DrugMedium", 400, 110, 20, 2, 0, "Potions", ""], ["(MP)DrugMedium", 396, 110, 20, 2, 0, "Potions", ""],
  ["(HP)DrugLarge", 28, 225, 20, 3, 0, "Potions", ""], ["(MP)DrugLarge", 29, 225, 20, 3, 0, "Potions", ""], ["(HP)DrugXL", 813, 250, 20, 4, 0, "Potions", ""], ["(MP)DrugXL", 814, 250, 20, 4, 0, "Potions", ""],
  ["SunPotion", 16, 1000, 20, 2, 0, "Potions", ""], ["SunPotion(M)", 312, 2000, 20, 4, 0, "Potions", ""],
  ["TownTeleport", 402, 1000, 20, 1, 0, "Scrolls", ""], ["RandomTeleport", 404, 100, 20, 1, 0, "Scrolls", ""], ["DungeonEscape", 408, 100, 20, 1, 0, "Scrolls", ""], ["RepairOil", 3368, 500, 1, 1, 0, "Scrolls", ""],
  ["BenedictionOil", 26, 10000, 5, 1, 0, "Scrolls", ""], ["Amulet", 270, 25, 500, 1, 18, "Scrolls", ""], ["GreenPoison", 259, 40, 500, 1, 14, "Scrolls", ""], ["RedPoison", 258, 40, 500, 1, 14, "Scrolls", ""],
  ["WoodenSword", 30, 50, 1, 4, 1, "Weapons", "Common"], ["Dagger", 35, 500, 1, 5, 2, "Weapons", "Common"], ["BronzeSword", 31, 900, 1, 7, 5, "Weapons", "Common"], ["IronSword", 36, 1100, 1, 10, 10, "Weapons", "Common"],
  ["SteelAxe", 67, 4000, 1, 15, 16, "Weapons", "Common"], ["SpiritBlade2", 54, 30000, 1, 10, 20, "Weapons", "Rare"], ["MirSword2", 828, 1000000, 1, 15, 50, "Weapons", "Mythical"],
  ["BaseDress(M)", 60, 120, 1, 5, 1, "Armour", "Common"], ["LightArmour(M)", 61, 2500, 1, 8, 11, "Armour", "Common"], ["HeavyArmour(M)", 62, 10000, 1, 23, 22, "Armour", "Common"], ["BronzeHelmet", 100, 1000, 1, 4, 10, "Armour", "Common"],
  ["SpiritNecklace", 249, 10000, 1, 1, 15, "Accessories", "Rare"], ["SpiritBracelet", 213, 10000, 1, 1, 15, "Accessories", "Rare"], ["SpiritRing", 179, 10000, 1, 1, 15, "Accessories", "Rare"],
  ["Candle", 130, 10, 1, 1, 0, "Lighting", ""], ["Torch", 131, 500, 1, 3, 0, "Lighting", ""],
];
const CATS: Cat[] = ["Potions", "Scrolls", "Weapons", "Armour", "Accessories", "Lighting"];
const MY_LEVEL = 42;
const fmt = (n: number) => n.toLocaleString("en-GB");
/** What the merchant pays for a stack: half of UserItem.Price(). */
const sellPrice = (st: Stack) => Math.floor(itemPrice(st.name, st.count) / 2);
/** Junk for the "select junk" shortcut: Common (or ungraded) gear you've outgrown by 15+ levels, and candles. */
const isJunk = (st: Stack) => {
  const it = info(st.name);
  if (!it) return false;
  if (it.type === "Torch" && /Candle/i.test(st.name)) return true;
  const gear = ["Weapon", "Armour", "Helmet", "Necklace", "Bracelet", "Ring", "Belt", "Boots"].includes(it.type);
  return gear && (it.grade === "None" || it.grade === "Common") && it.reqType === "Level" && it.req <= MY_LEVEL - 15;
};
/** Durability in the client's units (thousandths shown as whole numbers). */
const k = (n: number) => (n / 1000).toFixed(n % 1000 ? 1 : 0);
/** A row of the Repair list: one per item name (the mock keeps durability per name, so both rings repair together). */
type RepRow = { name: string; img: number; where: string[]; count: number; worn: boolean };

const CSS = `
.hudm .nwin { --winh: 560px; width: 900px; }
.hudm .nwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .nwin .head { display: flex; align-items: center; gap: 14px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 140% at 0% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .nwin .npc { width: 44px; height: 44px; border-radius: 50%; flex: none; display: flex; align-items: center; justify-content: center; font-size: 18px;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .nwin .say b { display: block; font-size: 15px; color: #fff; } .hudm .nwin .say span { font-size: 12px; font-style: italic; color: var(--dim); }
.hudm .nwin .wallet { margin-left: auto; display: flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 16px; font-size: 13px; font-weight: 700; color: #f2c94c;
  background: rgba(242, 201, 76, .08); border: 1px solid rgba(242, 201, 76, .4); font-variant-numeric: tabular-nums; }
.hudm .nwin .coin { width: 14px; height: 14px; border-radius: 50%; background: radial-gradient(circle at 35% 35%, #ffe9a0, #c9921f); box-shadow: 0 0 6px rgba(242, 201, 76, .5); }
.hudm .nwin .tabs { display: flex; gap: 2px; padding: 8px 14px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .nwin .tabs div { padding: 7px 16px; cursor: pointer; font-size: 12px; font-weight: 600; color: var(--dim); border-radius: var(--rad) var(--rad) 0 0; border: 1px solid transparent; border-bottom: 0; }
.hudm .nwin .tabs div:hover { color: var(--tx); }
.hudm .nwin .tabs div.on { color: #fff; background: color-mix(in srgb, var(--ac) 16%, transparent); border-color: color-mix(in srgb, var(--ac) 50%, transparent); box-shadow: inset 0 2px 0 var(--ac); }
.hudm .nwin .tabs i { font-style: normal; margin-left: 5px; font-size: 10px; padding: 0 5px; border-radius: 7px; background: var(--ac); color: #fff; }
.hudm .nwin .nbody { flex: 1; min-height: 0; display: flex; }
.hudm .nwin .nmain { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 10px 12px; gap: 8px; min-height: 0; }
.hudm .nwin .tools { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.hudm .nwin input.in { height: 26px; box-sizing: border-box; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx); width: 180px;
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .nwin input.in:focus { border-color: var(--ac); }
.hudm .nwin input.in::placeholder { color: var(--dim); }
.hudm .nwin .chip { height: 22px; padding: 0 9px; display: inline-flex; align-items: center; font-size: 11px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 11px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .nwin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .nwin .list { flex: 1; min-height: 0; overflow-y: auto; display: grid; grid-template-columns: 1fr 1fr; gap: 5px; align-content: start; padding-right: 3px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .nwin .gd { display: flex; align-items: center; gap: 9px; padding: 5px 8px 5px 5px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .nwin .gd:hover { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 9%, transparent); }
.hudm .nwin .gd .eq { width: 40px; height: 40px; }
.hudm .nwin .gd .t { flex: 1; min-width: 0; }
.hudm .nwin .gd .t b { display: block; font-size: 12px; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .nwin .gd .t b.Rare { color: #6fb6ff; } .hudm .nwin .gd .t b.Mythical { color: #c88cff; }
.hudm .nwin .gd .t span { font-size: 10px; color: var(--dim); }
.hudm .nwin .gd .t span.bad { color: #ff7a6a; }
.hudm .nwin .gd .pr { text-align: right; font-size: 12px; font-weight: 700; color: #f2c94c; white-space: nowrap; font-variant-numeric: tabular-nums; }
.hudm .nwin .gd .pr small { display: block; font-size: 9px; font-weight: 600; color: var(--dim); }
.hudm .nwin .gd.cant .pr { color: #ff7a6a; }
.hudm .nwin .side { width: 280px; flex: none; display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .nwin .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .nwin .cart { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .nwin .ci { display: flex; align-items: center; gap: 7px; padding: 4px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .nwin .ci .eq { width: 34px; height: 34px; }
.hudm .nwin .ci .t { flex: 1; min-width: 0; font-size: 11px; } .hudm .nwin .ci .t b { display: block; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .nwin .ci .t span { color: #f2c94c; font-variant-numeric: tabular-nums; }
.hudm .nwin .qty { display: flex; align-items: center; border: 1px solid var(--fr); border-radius: 12px; overflow: hidden; }
.hudm .nwin .qty div { width: 20px; height: 20px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-weight: 700; color: var(--tx); }
.hudm .nwin .qty div:hover { background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .nwin .qty span { min-width: 26px; text-align: center; font-size: 11px; font-weight: 700; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .nwin .x { cursor: pointer; color: var(--dim); padding: 0 3px; } .hudm .nwin .x:hover { color: #ff7a6a; }
.hudm .nwin .tot { display: flex; justify-content: space-between; align-items: baseline; padding-top: 8px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 12px; color: var(--dim); }
.hudm .nwin .tot b { font-size: 16px; color: #f2c94c; font-variant-numeric: tabular-nums; } .hudm .nwin .tot b.bad { color: #ff7a6a; }
.hudm .nwin .side .btn { height: 30px; justify-content: center; }
.hudm .nwin .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .nwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .nwin .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .nwin .empty { padding: 26px 8px; text-align: center; color: var(--dim); font-size: 12px; }
.hudm .nwin .bag { display: grid; grid-template-columns: repeat(8, 44px); gap: 4px; }
.hudm .nwin .bag .eq.no { opacity: .3; filter: grayscale(1); cursor: default; }
.hudm .nwin .bag .eq.pick { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .nwin .bag .eq.junk::after { content: "junk"; position: absolute; left: 0; right: 0; bottom: 0; z-index: 3; font-size: 8px; font-weight: 700; text-align: center; color: #fff; background: rgba(120, 120, 130, .7); }
.hudm .nwin .rp { display: flex; align-items: center; gap: 10px; padding: 6px 8px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .nwin .rp .eq { width: 40px; height: 40px; }
.hudm .nwin .rp .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .nwin .rp .t b { color: var(--tx); } .hudm .nwin .rp .t small { color: var(--dim); }
.hudm .nwin .dura { position: relative; height: 6px; margin-top: 4px; border-radius: 3px; background: #04060a; overflow: hidden; }
.hudm .nwin .dura i { position: absolute; left: 0; top: 0; bottom: 0; }
.hudm .nwin .dura u { position: absolute; top: 0; bottom: 0; background: repeating-linear-gradient(135deg, rgba(111,224,138,.6) 0 4px, rgba(111,224,138,.25) 4px 8px); }
.hudm .nwin .dura em { position: absolute; top: 0; bottom: 0; background: rgba(255,90,74,.55); }
.hudm .nwin .dura s { position: absolute; top: 0; bottom: 0; right: 0; background: repeating-linear-gradient(90deg, rgba(255,255,255,.06) 0 2px, transparent 2px 4px); }
.hudm .nwin .rp .t small.loss { color: #ff8a7a; }
.hudm .nwin .rp .t small.kept { color: #6fe08a; }
.hudm .nwin .rp.no { opacity: .55; }
.hudm .nwin .rp .why { width: 120px; text-align: right; font-size: 11px; color: var(--dim); }
.hudm .nwin .rlist { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; padding-right: 3px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .nwin .rlist .lbl { padding: 6px 2px 2px; }
.hudm .nwin .rsum { font-size: 11px; color: var(--dim); line-height: 1.5; }
.hudm .nwin .rsum b { color: var(--tx); font-variant-numeric: tabular-nums; } .hudm .nwin .rsum b.bad { color: #ff8a7a; }
.hudm .nwin .side .empty.l { padding: 4px 0; text-align: left; line-height: 1.5; }
.hudm .nwin .side .sp { flex: 1; }
.hudm .nwin .rp .cost { width: 90px; text-align: right; font-size: 12px; font-weight: 700; color: #f2c94c; font-variant-numeric: tabular-nums; }
.hudm .nwin .rp .btn { height: 24px; padding: 0 10px; }
.hudm .nwin .opt { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .nwin .tog { width: 34px; height: 18px; border-radius: 9px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); }
.hudm .nwin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 12px; height: 12px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .nwin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); } .hudm .nwin .tog.on::after { left: 18px; background: #fff; }
`;

export class MerchantMock extends HudWindow {
  private tab: Tab = "Buy";
  private cat: Cat | "All" = "All";
  private query = "";
  private cart = new Map<string, number>();
  /** Bag stacks picked to sell (the stack objects in the shared bag; dropped when they leave it). */
  private sellPick = new Set<Stack>();
  /** Sold this visit, with what the merchant paid (buy back costs the same). */
  private sold: { st: Stack; paid: number }[] = [];
  /** "Don't ask again" on the normal repair warning, for this visit. */
  private skipWearWarning = false;
  private special = false;
  private note = "";

  constructor(private readonly host: MerchantHost) {
    super("nwin", 62, 40);
    injectCss("hudm-merchant-css", CSS);
    // Sell and Repair show the bag: keep them current while open (picks that left the bag are dropped).
    onBag(() => {
      for (const st of this.sellPick) if (!bag.includes(st)) this.sellPick.delete(st);
      if (this.open && (this.tab === "Sell" || this.tab === "Repair" || this.tab === "Buy back")) this.render();
    });
  }
  private get gold(): number { return this.host.gold(); }

  /** Clicked the merchant NPC (`special`: open Repair with special repair on, from the NPC's special repair line). */
  openAtNpc(tab: Tab = "Buy", special?: boolean): void {
    if (this.el.hidden) { this.sold = []; this.sellPick.clear(); this.skipWearWarning = false; } // a new visit
    this.tab = tab; this.cart.clear(); this.note = "";
    if (special !== undefined) this.special = special;
    this.toggle(true); this.render();
  }
  protected override shown(): void { this.render(); }

  private render(): void {
    const tabs = h("div", "tabs");
    for (const t of ["Buy", "Sell", "Repair", "Buy back"] as Tab[]) {
      const n = t === "Buy" ? this.cart.size : t === "Sell" ? this.sellPick.size : t === "Buy back" ? this.sold.length : this.repairRows().filter((r) => this.rowCost(r) > 0).length;
      const d = h("div", t === this.tab ? "on" : "", t, n ? h("i", "", String(n)) : "");
      d.addEventListener("click", () => { this.tab = t; this.note = ""; this.render(); });
      tabs.append(d);
    }
    const head = h("div", "head", h("div", "npc", "⚖"), h("div", "say", h("b", "", "Merchant John · Bichon Province"), h("span", "", "“Fresh potions and honest prices, traveller. What'll it be?”")),
      h("div", "wallet", h("span", "coin"), fmt(this.gold)));
    const body = h("div", "nbody");
    ({ Buy: () => this.buy(body), Sell: () => this.sell(body), Repair: () => this.repair(body), "Buy back": () => this.buyBack(body) })[this.tab]();
    this.el.replaceChildren(h("div", "panel", this.titleBar("MERCHANT", "Close"), head, tabs, body));
  }

  // — Buy —
  private buy(body: HTMLElement): void {
    const search = h("input", "in");
    search.placeholder = "Search goods…"; search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim().toLowerCase(); fill(); });
    const tools = h("div", "tools", search);
    for (const c of ["All", ...CATS] as (Cat | "All")[]) {
      const ch = h("div", "chip" + (this.cat === c ? " on" : ""), c);
      ch.addEventListener("click", () => { this.cat = c; this.render(); });
      tools.append(ch);
    }
    const list = h("div", "list");
    const fill = () => {
      list.replaceChildren();
      for (const g of GOODS) {
        const [name, img, price, stack, , lvl, cat, grade] = g;
        if ((this.cat !== "All" && cat !== this.cat) || (this.query && !name.toLowerCase().includes(this.query))) continue;
        const low = lvl > MY_LEVEL;
        const row = h("div", "gd" + (price > this.gold ? " cant" : ""), itemCell(this.el, name, img),
          h("div", "t", h("b", grade, name), h("span", low ? "bad" : "", `${lvl ? `Level ${lvl}` : "Any level"}${stack > 1 ? ` · stacks to ${stack}` : ""}`)),
          h("div", "pr", fmt(price), h("small", "", stack > 1 ? "each" : "")));
        row.title = "Click to add to the cart";
        row.addEventListener("click", () => { this.cart.set(name, Math.min(stack > 1 ? stack * 5 : 9, (this.cart.get(name) ?? 0) + (stack > 1 ? 10 : 1))); this.render(); });
        list.append(row);
      }
      if (!list.childElementCount) list.append(h("div", "empty", "Nothing matches."));
    };
    fill();
    // Cart
    const cart = h("div", "cart");
    let total = 0, weight = 0;
    for (const [name, n] of this.cart) {
      const g = GOODS.find((x) => x[0] === name)!;
      total += g[2] * n; weight += g[4] * (g[3] > 1 ? Math.ceil(n / g[3]) : n);
      const step = g[3] > 1 ? 10 : 1;
      const minus = h("div", "", "−"), plus = h("div", "", "+");
      minus.addEventListener("click", () => { const v = n - step; if (v <= 0) this.cart.delete(name); else this.cart.set(name, v); this.render(); });
      plus.addEventListener("click", () => { this.cart.set(name, Math.min(g[3] > 1 ? g[3] * 5 : 9, n + step)); this.render(); }); // the row click's cap
      const x = h("span", "x", "×"); x.addEventListener("click", () => { this.cart.delete(name); this.render(); });
      cart.append(h("div", "ci", itemCell(this.el, name, g[1]), h("div", "t", h("b", "", name), h("span", "", fmt(g[2] * n))), h("div", "qty", minus, h("span", "", String(n)), plus), x));
    }
    if (!this.cart.size) cart.append(h("div", "empty", "Click goods to add them here."));
    const can = total > 0 && total <= this.gold;
    const buy = h("div", "btn main" + (can ? "" : " dis"), total > this.gold ? "Not enough gold" : `Buy for ${fmt(total)}`);
    buy.addEventListener("click", () => { if (!can || !this.host.spend(total)) return; this.note = `Bought ${this.cart.size} kind${this.cart.size === 1 ? "" : "s"} of goods.`; this.cart.clear(); this.render(); });
    body.append(h("div", "nmain", tools, list), h("div", "side", h("div", "lbl", "Cart", h("span", "", `${weight} weight`)), cart,
      h("div", "tot", "Total", h("b", total > this.gold ? "bad" : "", fmt(total))), buy, h("div", "note", this.note)));
  }

  // — Sell —
  private sell(body: HTMLElement): void {
    const grid = h("div", "bag");
    for (let i = 0; i < BAG_SLOTS; i++) {
      const st = bag[i];
      if (!st) { grid.append(itemCell(this.el, "", -1)); continue; }
      const no = hasBind(st.name, "DontSell");
      const v = sellPrice(st);
      const c = itemCell(this.el, `${st.name}${st.count > 1 ? ` (${st.count})` : ""} · ${no ? "can't be sold" : `sells for ${fmt(v)}`}`, st.img, st.count);
      if (no) c.classList.add("no");
      else {
        if (this.sellPick.has(st)) c.classList.add("pick");
        if (isJunk(st)) c.classList.add("junk");
        c.addEventListener("click", () => { if (this.sellPick.has(st)) this.sellPick.delete(st); else this.sellPick.add(st); this.render(); });
      }
      grid.append(c);
    }
    const junk = h("div", "btn", "Select junk");
    junk.title = "Pick every item marked junk (Common gear you've outgrown by 15+ levels, candles)";
    junk.addEventListener("click", () => {
      let n = 0;
      for (const st of bag) if (st && isJunk(st) && !hasBind(st.name, "DontSell")) { this.sellPick.add(st); n++; }
      this.note = n ? "" : "Nothing in your bag counts as junk.";
      this.render();
    });
    const clear = h("div", "btn", "Clear"); clear.addEventListener("click", () => { this.sellPick.clear(); this.render(); });
    const list = h("div", "cart");
    let total = 0;
    for (const st of this.sellPick) {
      const v = sellPrice(st); total += v;
      const x = h("span", "x", "✕"); x.title = "Don't sell this";
      x.addEventListener("click", () => { this.sellPick.delete(st); this.render(); });
      list.append(h("div", "ci", itemCell(this.el, st.name, st.img, st.count), h("div", "t", h("b", "", st.name), h("span", "", `+${fmt(v)}`)), x));
    }
    if (!this.sellPick.size) list.append(h("div", "empty", "Click items in your bag to sell them."));
    const sell = h("div", "btn main" + (this.sellPick.size ? "" : " dis"), `Sell for ${fmt(total)}`);
    sell.addEventListener("click", () => {
      const picks = [...this.sellPick].filter((st) => bag.includes(st));
      if (!picks.length) return;
      let paid = 0;
      for (const st of picks) {
        const v = sellPrice(st);
        bag[bag.indexOf(st)] = null;
        this.sold.push({ st, paid: v }); paid += v;
      }
      this.host.earn(paid);
      this.sellPick.clear();
      this.note = `Sold ${picks.length} item${picks.length === 1 ? "" : "s"} for ${fmt(paid)}. Changed your mind? See Buy back.`;
      bagChanged(); // redraws this window too
    });
    body.append(h("div", "nmain", h("div", "tools", h("span", "lbl", "Your bag · the merchant pays half the price"), junk, clear), grid),
      h("div", "side", h("div", "lbl", "Selling", h("span", "", String(this.sellPick.size))), list, h("div", "tot", "You get", h("b", "", fmt(total))), sell, h("div", "note", this.note)));
  }

  // — Repair —
  /** Worn gear (every piece with durability) and the damaged items in your bag, one row per item name. */
  private repairRows(): RepRow[] {
    const rows = new Map<string, RepRow>();
    const add = (name: string, img: number, where: string, worn: boolean) => {
      const r = rows.get(name);
      if (r) { r.count++; r.where.push(where); r.worn ||= worn; return; }
      rows.set(name, { name, img: info(name)?.img ?? img, where: [where], count: 1, worn });
    };
    for (const g of wornGear()) {
      const it = info(g.name);
      if (it?.dura && it.type !== "Amulet") add(g.name, g.img, g.slot, true);
    }
    for (const st of bag) {
      if (!st) continue;
      const d = duraOf(st.name), it = info(st.name);
      if (!d || !it || it.type === "Amulet" || it.type === "Potion" || it.type === "Scroll") continue;
      if (d.cur < d.max || rows.has(st.name)) add(st.name, st.img, "Bag", false);
    }
    return [...rows.values()];
  }
  /** What repairing a row costs now (0 = nothing to do or the NPC won't). */
  private rowCost(r: RepRow): number {
    const d = duraOf(r.name);
    return !d || d.cur >= d.max || repairWhy(r.name, this.special) ? 0 : repairCost(r.name, this.special, r.count);
  }

  private repair(body: HTMLElement): void {
    const list = h("div", "rlist");
    const rows = this.repairRows();
    const todo: RepRow[] = [], wornTodo: RepRow[] = [];
    let total = 0, lost = 0, wornTotal = 0, wornLost = 0;
    const row = (r: RepRow) => {
      const it = info(r.name)!, d = duraOf(r.name)!, why = repairWhy(r.name, this.special);
      const loss = repairLoss(r.name, this.special), newMax = d.max - loss, cost = this.rowCost(r);
      if (cost) { todo.push(r); total += cost; lost += loss; }
      if (cost && r.worn) { wornTodo.push(r); wornTotal += cost; wornLost += loss; }
      // The bar runs to the item's base maximum: current durability, what the repair restores (striped green), what a
      // normal repair wears off the maximum (red), and maximum already lost to earlier repairs (hatched).
      const pct = (n: number) => (n / it.dura * 100).toFixed(1) + "%";
      const frac = d.cur / Math.max(1, d.max);
      const bar = h("div", "dura", h("i"), h("u"), h("em"), h("s"));
      const [fill, back, wear, gone] = [...bar.children] as HTMLElement[];
      fill!.style.width = pct(d.cur);
      fill!.style.background = frac > 0.6 ? "linear-gradient(90deg, #2f9e5a, #4fe07a)" : frac > 0.3 ? "linear-gradient(90deg, #b07a1a, #f2c94c)" : "linear-gradient(90deg, #8a1a1a, #ff5a4a)";
      if (cost) { Object.assign(back!.style, { left: pct(d.cur), width: pct(newMax - d.cur) }); Object.assign(wear!.style, { left: pct(newMax), width: pct(loss) }); }
      gone!.style.width = pct(it.dura - d.max);
      const where = r.where.length > 1 && r.where.every((w) => w === r.where[0]) ? `${r.where[0]} ×${r.where.length}` : r.where.join(" + ");
      const note = !cost ? null : loss ? h("small", "loss", ` · max −${(loss / 1000).toFixed(2).replace(/\.?0+$/, "")}`) : h("small", "kept", " · max kept");
      const t = h("div", "t", h("b", "", r.name), " ", h("small", "", `${where} · ${k(d.cur)} / ${k(d.max)}`), note ?? "", bar);
      let end: HTMLElement[];
      if (why) end = [h("div", "why", why)];
      else if (!cost) end = [h("div", "cost", "—"), h("div", "btn dis", "OK")];
      else {
        const btn = h("div", "btn" + (cost > this.gold ? " dis" : ""), "Repair");
        btn.title = r.count > 1 ? `Repairs all ${r.count} (the mock keeps durability per item name)` : "";
        btn.addEventListener("click", () => void this.doRepair([r], cost));
        end = [h("div", "cost", fmt(cost)), btn];
      }
      const el = h("div", "rp" + (why ? " no" : ""), itemCell(this.el, r.name, r.img, r.count > 1 ? r.count : undefined), t, ...end);
      if (why) el.title = why;
      return el;
    };
    const worn = rows.filter((r) => r.worn), inBag = rows.filter((r) => !r.worn);
    list.append(h("div", "lbl", "Equipped"), ...worn.map(row));
    list.append(h("div", "lbl", "In your bag", h("span", "", inBag.length ? "damaged items" : "")));
    if (inBag.length) list.append(...inBag.map(row));
    else list.append(h("div", "empty", "Nothing in your bag needs repairing."));

    const tog = h("div", "tog" + (this.special ? " on" : ""));
    tog.addEventListener("click", () => { this.special = !this.special; this.render(); });
    // Repair equipped is the main button; Repair all (worn gear and bag) only when the bag adds something to it.
    const eq = h("div", "btn main" + (wornTotal && wornTotal <= this.gold ? "" : " dis"),
      wornTotal ? (wornTotal > this.gold ? "Not enough gold" : `Repair equipped for ${fmt(wornTotal)}`) : "Equipped gear is in good shape");
    eq.addEventListener("click", () => void this.doRepair(wornTodo, wornTotal));
    const withBag = total > wornTotal;
    // With the worn gear already repaired, this button only covers the bag: say so.
    const bagOnly = !wornTotal;
    const all = h("div", "btn" + (total <= this.gold ? "" : " dis"),
      total > this.gold ? "Not enough gold for everything" : bagOnly ? `Repair bag items for ${fmt(total)}` : `Repair all, bag too, for ${fmt(total)}`);
    all.title = bagOnly ? "The damaged items in your bag" : "Your worn gear and the damaged items in your bag";
    all.addEventListener("click", () => void this.doRepair(todo, total));
    const explain = this.special
      ? "Special repair: three times the price, and the maximum durability stays where it is. Some items can't be special repaired."
      : "Normal repair: restores durability, but each repair wears the maximum down by a thirtieth of what was missing. Special repair keeps the maximum for three times the price.";
    const line = (n: number, where: string, l: number) => h("div", "", h("b", "", String(n)), ` ${where} to repair`,
      !this.special && l ? h("span", "", " · max lost: ", h("b", "bad", k(l))) : "");
    const sum = h("div", "rsum", line(wornTodo.length, wornTodo.length === 1 ? "worn item" : "worn items", wornLost),
      withBag ? line(todo.length - wornTodo.length, todo.length - wornTodo.length === 1 ? "bag item" : "bag items", lost - wornLost) : "");
    body.append(h("div", "nmain", list),
      h("div", "side", h("div", "opt", tog, "Special repair"), h("div", "empty l", explain), h("div", "sp"),
        sum, h("div", "tot", "Equipped", h("b", wornTotal > this.gold ? "bad" : "", fmt(wornTotal))), eq,
        withBag ? h("div", "tot", bagOnly ? "Bag items" : "With your bag", h("b", total > this.gold ? "bad" : "", fmt(total))) : "", withBag ? all : "", h("div", "note", this.note)));
  }

  /** Pays and repairs the rows (the server's RepairItem for each). A normal repair that wears the maximum asks first. */
  private async doRepair(rows: RepRow[], cost: number): Promise<void> {
    if (!rows.length || !cost) return;
    const wear = this.special ? [] : rows.map((r) => [r.name, repairLoss(r.name, false)] as const).filter(([, l]) => l > 0);
    if (wear.length && !this.skipWearWarning) {
      const lines = wear.slice(0, 8).map(([n, l]) => `${n}: max −${(l / 1000).toFixed(2).replace(/\.?0+$/, "")}`);
      if (wear.length > 8) lines.push(`…and ${wear.length - 8} more`);
      const r = await this.host.confirm({
        title: "Normal repair", kind: "warning", buttons: "yesno", dontAsk: true, labels: { yes: "Repair", no: "Cancel" },
        text: `${wear.length === 1 ? "This item" : "These items"} will lose maximum durability:\n${lines.join("\n")}\n\nSpecial repair keeps the maximum for three times the price.\nDo you want to continue?`,
      });
      if (r?.answer !== "yes") return;
      if (r.dontAsk) this.skipWearWarning = true;
    }
    if (!this.host.spend(cost)) { this.note = "You don't have enough gold."; this.render(); return; }
    for (const r of rows) applyRepair(r.name, this.special);
    const what = rows.length === 1 ? rows[0]!.name : `${rows.length} items`;
    this.note = `${what} repaired (−${fmt(cost)} gold).`;
    this.host.say(`${this.special ? "Special repaired" : "Repaired"} ${what} for ${fmt(cost)} gold.`);
    this.host.gearChanged();
    this.render();
  }

  // — Buy back —
  private buyBack(body: HTMLElement): void {
    const list = h("div", "cart");
    for (const b of this.sold) {
      const btn = h("div", "btn" + (b.paid <= this.gold ? "" : " dis"), `Buy back · ${fmt(b.paid)}`);
      btn.addEventListener("click", () => {
        if (!freeCells()) { this.note = "Your bag is full."; this.render(); return; }
        if (!this.host.spend(b.paid)) { this.note = "You don't have enough gold."; this.render(); return; }
        this.sold = this.sold.filter((x) => x !== b);
        addToBag({ ...b.st });
        this.note = `${b.st.name} is back in your bag.`;
        bagChanged();
      });
      list.append(h("div", "rp", itemCell(this.el, b.st.name, b.st.img, b.st.count), h("div", "t", h("b", "", b.st.name), " ", h("small", "", "sold this visit")), btn));
    }
    if (!this.sold.length) list.append(h("div", "empty", "Items you sell to this merchant can be bought back here until you leave."));
    body.append(h("div", "nmain", h("div", "lbl", "Sold this visit"), list), h("div", "side", h("div", "note", this.note)));
  }
}
