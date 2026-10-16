// Storage window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// StorageDialog around Crystal's account storage (AccountInfo.Storage = 80 slots shared by every character on the
// account; HasExpandedStorage adds a second 80-slot page until ExpandedStorageExpiryDate; Settings.RequireStoragePassword
// asks for the storage password first): a lock screen, capacity + page tabs (the expanded page with days left), search
// that highlights matches in place (slots never move), category chips, sort and auto-stack, your bag beside it (the
// shared bag, hudItems.ts: click to deposit, double-click storage to withdraw, or drag and drop / right-click either way,
// hudDrag.ts; DontStore items are refused), an item card with who stored it, and an activity log. Mock data only: the
// lock accepts any password and nothing typed is kept. Item Image numbers are from a copy of the live Server.MirDB (2026-10-15).

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { BAG_SLOTS, addToBag, bag, bagChanged, hasBind, info, onBag, weightOf, type Stack } from "./hudItems.ts";
import { bestCell, bindCell, host, registerHost, type DragHost } from "./hudDrag.ts";

type Cat = "Weapons" | "Armour" | "Jewellery" | "Potions" | "Scrolls" | "Materials" | "Books" | "Other";
interface Item { name: string; img: number; count: number; cat: Cat; weight: number; by: string; when: string; max: number }
const PAGE = 80;
const CHARS = ["GMRyu", "GMRyuTao", "GMRyuWiz"];
let seq = 0;
const it = (name: string, img: number, count: number, cat: Cat, weight = 1, max = 1): Item =>
  ({ name, img, count, cat, weight, max, by: CHARS[seq % 3]!, when: ["today", "yesterday", "3 days ago", "last week", "2 weeks ago"][seq++ % 5]! });
const STORED: (Item | null)[] = [
  it("SunPotion(M)", 312, 40, "Potions", 1, 100), it("SunPotion(M)", 312, 25, "Potions", 1, 100), it("(HP)DrugXL", 813, 60, "Potions", 1, 100), it("(MP)DrugXL", 814, 60, "Potions", 1, 100),
  it("TownTeleport", 402, 20, "Scrolls", 1, 50), it("RandomTeleport", 404, 30, "Scrolls", 1, 50), it("DungeonEscape", 408, 8, "Scrolls", 1, 50), it("BenedictionOil", 26, 3, "Scrolls", 1, 10),
  it("DragonSlayer", 57, 1, "Weapons", 32), it("SpiritBlade2", 54, 1, "Weapons", 18), it("MirSword2", 828, 1, "Weapons", 20), it("SteelAxe", 67, 1, "Weapons", 22),
  it("TarragonArmour(M)", 2276, 1, "Armour", 28), it("MirArmour(M)2", 595, 1, "Armour", 26), it("BlackTigerArmour(M)", 1030, 1, "Armour", 27), null,
  it("RedOrchidNecklace", 432, 1, "Jewellery"), it("NephriteNecklace(H)", 777, 1, "Jewellery"), it("RedJadeRing", 771, 1, "Jewellery"), it("MundaneRing", 521, 1, "Jewellery"),
  it("MirWheel3", 826, 1, "Jewellery"), it("RedJadeBracelet", 762, 1, "Jewellery"), it("SoulGem", 712, 4, "Materials", 1, 20), it("AccuracyGem", 734, 2, "Materials", 1, 20),
  it("MagicOrb", 701, 6, "Materials", 1, 20), it("AgilityOrb", 726, 3, "Materials", 1, 20), it("SilverOre", 285, 12, "Materials", 2, 1), it("Nephrite", 530, 5, "Materials", 2, 1),
  it("Timber", 368, 40, "Materials", 1, 50), it("BronzeChestnut", 484, 15, "Materials", 1, 50), it("BookofSpirit", 1180, 1, "Books"), it("Thrusting", 3640, 1, "Books"),
  it("LionRoar", 3640, 1, "Books"), it("GreatFireBall", 3641, 1, "Books"), it("BlackLeopard", 2042, 1, "Other", 0), it("Amulet", 270, 500, "Other", 1, 1000),
  it("GreenPoison", 259, 80, "Other", 1, 100), it("RedPoison", 258, 80, "Other", 1, 100), it("HealthStone(M)", 586, 2, "Other", 2, 1), it("Candle", 130, 1, "Other"),
  null, null, it("SunPotion(M)", 312, 12, "Potions", 1, 100), null,
];
while (STORED.length < PAGE) STORED.push(null);
const EXPANDED: (Item | null)[] = [it("WhiteTiger", 3757, 1, "Other", 0), it("RecallNecklace", 247, 1, "Jewellery"), it("DragonStaff", 58, 1, "Weapons", 20), it("HolyBloodSpear", 77, 1, "Weapons", 24),
  ...Array<null>(PAGE - 4).fill(null)];
/** ItemType -> storage category. */
const CAT_OF: Record<string, Cat> = { Weapon: "Weapons", Armour: "Armour", Helmet: "Armour", Boots: "Armour", Belt: "Armour", Necklace: "Jewellery", Bracelet: "Jewellery",
  Ring: "Jewellery", Potion: "Potions", Scroll: "Scrolls", Gem: "Materials", Ore: "Materials", CraftingMaterial: "Materials", Meat: "Materials", Book: "Books" };
/** A bag stack as a stored item (category, weight and stack size from items.json). */
const stored = (s: Stack): Item => {
  const i = info(s.name);
  return { name: s.name, img: s.img, count: s.count, cat: CAT_OF[i?.type ?? ""] ?? "Other", weight: i?.weight ?? 1, max: i?.stack ?? 1, by: "GMRyu", when: "just now" };
};
const CATS: Cat[] = ["Weapons", "Armour", "Jewellery", "Potions", "Scrolls", "Materials", "Books", "Other"];
const EXPANDED_DAYS = 23;

const CSS = `
.hudm .swin2 { --winh: 600px; width: 960px; }
.hudm .swin2 .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .swin2 .head { display: flex; align-items: center; gap: 14px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin2 .head .t b { display: block; font-size: 15px; color: #fff; } .hudm .swin2 .head .t span { font-size: 11px; color: var(--dim); }
.hudm .swin2 .cap { width: 170px; } .hudm .swin2 .cap .ch { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); margin-bottom: 3px; }
.hudm .swin2 .cap .ch b { color: var(--tx); }
.hudm .swin2 .bar { height: 6px; border-radius: 3px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .swin2 .bar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); box-shadow: 0 0 6px var(--glow); }
.hudm .swin2 .pages { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .swin2 .pages div { padding: 0 12px; height: 26px; display: flex; align-items: center; gap: 6px; cursor: pointer; font-size: 11px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .swin2 .pages div + div { border-left: 1px solid var(--fr); }
.hudm .swin2 .pages div.on { color: #fff; background: color-mix(in srgb, var(--ac) 26%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .swin2 .pages small { font-size: 9px; color: var(--acd); }
.hudm .swin2 .lockb { margin-left: auto; }
.hudm .swin2 .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin2 .sbody { flex: 1; min-height: 0; display: flex; }
.hudm .swin2 .store { flex: 1; min-width: 0; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.hudm .swin2 .tools { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.hudm .swin2 input.in, .hudm .swin2 select { height: 26px; box-sizing: border-box; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .swin2 input.in:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin2 input.in::placeholder { color: var(--dim); }
.hudm .swin2 .tools .in { width: 190px; }
.hudm .swin2 .tools .btn { height: 26px; }
.hudm .swin2 .chips { display: flex; gap: 4px; flex-wrap: wrap; }
.hudm .swin2 .chip { height: 20px; padding: 0 8px; display: inline-flex; align-items: center; gap: 4px; font-size: 10px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 10px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .swin2 .chip i { font-style: normal; opacity: .7; }
.hudm .swin2 .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .swin2 .grid { display: grid; grid-template-columns: repeat(10, 44px); gap: 4px; }
.hudm .swin2 .grid .eq.dim { opacity: .18; filter: grayscale(1); }
.hudm .swin2 .grid .eq.hit { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .swin2 .grid .eq.new { animation: hudm-snew 1.2s ease-out; }
@keyframes hudm-snew { 0% { box-shadow: 0 0 0 1px #000, 0 0 16px var(--ac); border-color: #fff; } }
.hudm .swin2 .hint { font-size: 10px; color: var(--dim); }
.hudm .swin2 .side { width: 300px; flex: none; display: flex; flex-direction: column; gap: 10px; padding: 10px 12px; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin2 .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; }
.hudm .swin2 .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; }
.hudm .swin2 .bag { display: grid; grid-template-columns: repeat(6, 44px); gap: 4px; }
.hudm .swin2 .card { padding: 10px 12px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .swin2 .card .nm { font-size: 13px; font-weight: 700; color: #fff; margin-bottom: 6px; }
.hudm .swin2 .card .ln { display: flex; justify-content: space-between; font-size: 11px; padding: 2px 0; }
.hudm .swin2 .card .ln span { color: var(--dim); } .hudm .swin2 .card .ln b { color: var(--tx); font-weight: 600; }
.hudm .swin2 .card .row { display: flex; gap: 6px; margin-top: 8px; }
.hudm .swin2 .card .row .btn { flex: 1; height: 24px; justify-content: center; }
.hudm .swin2 .log { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .swin2 .log div { display: flex; gap: 8px; padding: 4px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .swin2 .log div b { color: var(--acd); font-weight: 600; } .hudm .swin2 .log div span { margin-left: auto; color: var(--dim); white-space: nowrap; }
.hudm .swin2 .note { font-size: 11px; color: var(--ac); min-height: 14px; }
/* Lock screen */
.hudm .swin2 .lock { flex: 1; display: flex; align-items: center; justify-content: center; }
.hudm .swin2 .lock .card { width: 340px; padding: 24px; text-align: center; }
.hudm .swin2 .lock svg { width: 46px; height: 46px; fill: none; stroke: var(--ac); stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; filter: drop-shadow(0 0 8px var(--glow)); }
.hudm .swin2 .lock h3 { margin: 8px 0 4px; font-size: 17px; color: #fff; }
.hudm .swin2 .lock p { margin: 0 0 14px; font-size: 12px; color: var(--dim); }
.hudm .swin2 .lock .in { width: 100%; text-align: center; letter-spacing: .3em; height: 32px; }
.hudm .swin2 .lock .btn { width: 100%; height: 32px; justify-content: center; margin-top: 8px; }
.hudm .swin2 .lock .err { margin-top: 6px; min-height: 14px; font-size: 11px; color: #ff8a6a; }
.hudm .swin2 .lock .shake { animation: hudm-shake .3s linear; }
`;

export class StorageMock extends HudWindow {
  private pages: (Item | null)[][] = [STORED.map((x) => x && { ...x }), EXPANDED.map((x) => x && { ...x })];
  private page = 0;
  private locked = true;
  private query = "";
  private cat: Cat | "All" = "All";
  private sel: { where: "s" | "b"; i: number } | null = null;
  private fresh = -1;
  private note = "";
  private log: [string, string, string][] = [["GMRyuTao", "deposited Timber x40", "yesterday"], ["GMRyu", "withdrew SunPotion(M) x20", "2 days ago"], ["GMRyuWiz", "deposited GreatFireBall", "last week"]];

  constructor() {
    super("swin2", 32, 30);
    injectCss("hudm-storage-css", CSS);
    const me: DragHost = registerHost({
      id: "store", size: PAGE, live: () => this.open && !this.locked,
      get: (i) => { const x = this.pages[this.page]![i]; return x ? { name: x.name, img: x.img, count: x.count } : null; },
      set: (i, st) => {
        const page = this.pages[this.page]!, old = page[i];
        page[i] = !st ? null : old && old.name === st.name ? { ...old, count: st.count } : stored(st);
        if (st && (!old || old.name !== st.name || st.count > old.count)) this.fresh = i;
        this.render();
      },
      accepts: (_i, st) => hasBind(st.name, "DontStore") ? "Can't be stored" : "",
      verb: () => "Store",
      quick: (i) => { const st = me.get(i); return st ? bestCell(host("bag"), st, me) : null; },
      after: (st, from, to) => {
        const what = to === me && from !== me ? "deposited" : from === me && to !== me ? "withdrew" : "moved";
        this.log.unshift(["GMRyu", `${what} ${st.name}${st.count > 1 ? ` x${st.count}` : ""}`, "just now"]);
        this.note = what === "deposited" ? `Stored ${st.name}.` : what === "withdrew" ? `Took ${st.name}.` : `Moved ${st.name}.`;
        this.sel = null;
        this.render();
      },
    });
    onBag(() => { if (this.open && !this.locked) this.render(); });
  }

  /** The storage NPC was clicked: opens locked (RequireStoragePassword). */
  openAtNpc(): void { this.locked = true; this.toggle(true); }

  protected override shown(): void { this.render(); }

  private render(): void {
    const panel = h("div", "panel", this.titleBar("STORAGE", "Close"));
    if (this.locked) panel.append(this.lockScreen());
    else panel.append(this.head(), h("div", "sbody", this.storeView(), this.sideView()));
    this.el.replaceChildren(panel);
  }

  private lockScreen(): HTMLElement {
    const icon = '<svg viewBox="0 0 24 24"><path d="M6 11h12v10H6zM8 11V7a4 4 0 018 0v4M12 15v2"/></svg>';
    const pass = h("input", "in");
    pass.type = "password"; pass.placeholder = "••••"; pass.autocomplete = "off";
    const err = h("div", "err");
    const card = h("div", "card");
    const go = () => {
      if (pass.value.length < 4) { err.textContent = "At least 4 characters."; card.classList.remove("shake"); void card.offsetWidth; card.classList.add("shake"); return; }
      pass.value = ""; // nothing typed is kept
      this.locked = false; this.note = "Storage unlocked."; this.render();
    };
    pass.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") go(); });
    const btn = h("div", "btn main", "Unlock");
    btn.addEventListener("click", go);
    card.innerHTML = icon;
    card.append(h("h3", "", "Storage is locked"), h("p", "", "Enter your storage password. It protects the storage every character on your account shares."), pass, btn, err,
      h("div", "hint", "Mock: any password of 4+ characters opens it; nothing typed is kept."));
    setTimeout(() => pass.focus({ preventScroll: true }), 0);
    return h("div", "lock", card);
  }

  private used(p: number): number { return this.pages[p]!.filter(Boolean).length; }

  private head(): HTMLElement {
    const total = this.used(0) + this.used(1);
    const bar = h("div", "bar", h("i"));
    (bar.firstChild as HTMLElement).style.width = (total / (PAGE * 2) * 100) + "%";
    const pages = h("div", "pages");
    ([["Page 1", ""], ["Page 2", `expanded · ${EXPANDED_DAYS} days left`]] as const).forEach(([t, sub], i) => {
      const p = h("div", i === this.page ? "on" : "", t, h("span", "", `${this.used(i)}/${PAGE}`), sub ? h("small", "", sub) : "");
      p.addEventListener("click", () => { this.page = i; this.sel = null; this.render(); });
      pages.append(p);
    });
    const lock = h("div", "btn lockb", "Lock storage");
    lock.addEventListener("click", () => { this.locked = true; this.render(); });
    return h("div", "head", h("div", "t", h("b", "", "Account storage"), h("span", "", `Shared by ${CHARS.join(", ")}`)),
      h("div", "cap", h("div", "ch", h("span", "", "Used"), h("b", "", `${total} / ${PAGE * 2}`)), bar), pages, lock);
  }

  private matches(x: Item): boolean {
    return (this.cat === "All" || x.cat === this.cat) && (!this.query || x.name.toLowerCase().includes(this.query) || x.cat.toLowerCase().includes(this.query));
  }

  private storeView(): HTMLElement {
    const slots = this.pages[this.page]!;
    const search = h("input", "in");
    search.placeholder = "Find an item…";
    search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim().toLowerCase(); this.paintGrid(grid); });
    const sort = h("select");
    for (const s of ["Sort: type", "Sort: name", "Sort: count"]) sort.append(Object.assign(h("option", "", s), { value: s }));
    sort.addEventListener("change", () => {
      const items = slots.filter(Boolean) as Item[];
      const key = sort.value;
      items.sort((a, b) => key.endsWith("name") ? a.name.localeCompare(b.name) : key.endsWith("count") ? b.count - a.count : CATS.indexOf(a.cat) - CATS.indexOf(b.cat) || a.name.localeCompare(b.name));
      this.pages[this.page] = [...items, ...Array<null>(PAGE - items.length).fill(null)];
      this.note = "Sorted."; this.sel = null; this.render();
    });
    const stack = h("div", "btn", "Auto-stack");
    stack.title = "Merge partial stacks of the same item";
    stack.addEventListener("click", () => {
      let merged = 0;
      const page = this.pages[this.page]!;
      for (let i = 0; i < page.length; i++) {
        const a = page[i]; if (!a || a.max <= 1) continue;
        for (let j = i + 1; j < page.length; j++) {
          const b = page[j]; if (!b || b.name !== a.name || a.count >= a.max) continue;
          const take = Math.min(b.count, a.max - a.count);
          a.count += take; b.count -= take; merged++;
          if (!b.count) page[j] = null;
        }
      }
      this.note = merged ? `Merged ${merged} stack${merged === 1 ? "" : "s"}.` : "Nothing to merge.";
      this.render();
    });
    const chips = h("div", "chips");
    for (const c of ["All", ...CATS] as (Cat | "All")[]) {
      const n = c === "All" ? slots.filter(Boolean).length : slots.filter((x) => x?.cat === c).length;
      const ch = h("div", "chip" + (this.cat === c ? " on" : ""), c, h("i", "", String(n)));
      ch.addEventListener("click", () => { this.cat = c; this.render(); });
      chips.append(ch);
    }
    const grid = h("div", "grid");
    this.paintGrid(grid);
    return h("div", "store", h("div", "tools", search, sort, stack), chips, grid,
      h("div", "hint", "Click an item to see it · double-click to withdraw it to your bag · search and filters highlight in place, so nothing moves."));
  }

  private paintGrid(grid: HTMLElement): void {
    const slots = this.pages[this.page]!;
    const filtering = this.query !== "" || this.cat !== "All";
    grid.replaceChildren();
    slots.forEach((x, i) => {
      const cell = x ? itemCell(this.el, `${x.name}${x.count > 1 ? ` (${x.count})` : ""}`, x.img, x.count) : itemCell(this.el, "", -1);
      if (x && filtering) cell.classList.add(this.matches(x) ? "hit" : "dim");
      if (i === this.fresh) { cell.classList.add("new"); }
      bindCell(cell, "store", i);
      if (this.sel?.where === "s" && this.sel.i === i) cell.classList.add("sel");
      if (x) {
        cell.addEventListener("click", () => { this.sel = { where: "s", i }; this.renderSideOnly(); for (const c of grid.querySelectorAll(".eq.sel")) c.classList.remove("sel"); cell.classList.add("sel"); });
        cell.addEventListener("dblclick", () => this.withdraw(i));
      }
      grid.append(cell);
    });
    this.fresh = -1;
  }

  private sideEl: HTMLElement | null = null;
  private renderSideOnly(): void { const s = this.sideView(); this.sideEl?.replaceWith(s); }

  private sideView(): HTMLElement {
    const bagGrid = h("div", "bag");
    bag.forEach((x, i) => {
      const c = x ? itemCell(this.el, `${x.name}${x.count > 1 ? ` (${x.count})` : ""} · click to deposit`, x.img, x.count) : itemCell(this.el, "", -1);
      if (x) c.addEventListener("click", () => this.deposit(i));
      bindCell(c, "bag", i);
      bagGrid.append(c);
    });
    const bagN = bag.filter(Boolean).length;
    const card = h("div", "card");
    const x = this.sel?.where === "s" ? this.pages[this.page]![this.sel.i] : null;
    if (x) {
      const ln = (k: string, v: string) => h("div", "ln", h("span", "", k), h("b", "", v));
      const w = h("div", "btn main", "Withdraw"); w.addEventListener("click", () => this.withdraw(this.sel!.i));
      card.append(h("div", "nm", x.name), ln("Type", x.cat), ln("Amount", x.max > 1 ? `${x.count} / ${x.max}` : String(x.count)), ln("Weight", String(weightOf(x))),
        ln("Stored by", x.by), ln("When", x.when), h("div", "row", w));
    } else card.append(h("div", "hint", "Pick an item in storage to see who stored it and when."));
    const log = h("div", "log");
    for (const [who, what, when] of this.log) log.append(h("div", "", h("b", "", who), what, h("span", "", when)));
    this.sideEl = h("div", "side", h("div", "", h("div", "lbl", "Your bag", h("b", "", `${bagN} / ${BAG_SLOTS}`)), bagGrid), h("div", "note", this.note), card,
      h("div", "lbl", "Activity", h("b", "", "")), log);
    return this.sideEl;
  }

  private deposit(i: number): void {
    const b = bag[i];
    if (!b) return;
    if (hasBind(b.name, "DontStore")) { this.note = `${b.name} can't be stored.`; this.render(); return; }
    const x = stored(b);
    const page = this.pages[this.page]!;
    // Top up a matching stack first, then the first empty slot (like the server's merge on store).
    const stack = x.max > 1 ? page.findIndex((s) => s && s.name === x.name && s.count < s.max) : -1;
    if (stack >= 0) {
      const s = page[stack]!, take = Math.min(x.count, s.max - s.count);
      s.count += take; x.count -= take; this.fresh = stack;
      if (x.count) { b.count = x.count; this.note = `Topped up ${x.name}; ${x.count} left in your bag.`; bagChanged(); return; }
    } else {
      const free = page.indexOf(null);
      if (free < 0) { this.note = "This page is full."; this.render(); return; }
      page[free] = x; this.fresh = free;
    }
    bag[i] = null;
    this.log.unshift(["GMRyu", `deposited ${b.name}${b.count > 1 ? ` x${b.count}` : ""}`, "just now"]);
    this.note = `Stored ${b.name}.`;
    bagChanged();
  }

  private withdraw(i: number): void {
    const page = this.pages[this.page]!;
    const x = page[i];
    if (!x) return;
    const left = addToBag({ name: x.name, img: x.img, count: x.count }), took = x.count - left;
    if (!took) { this.note = "Your bag is full."; this.render(); return; }
    page[i] = left ? { ...x, count: left } : null;
    this.log.unshift(["GMRyu", `withdrew ${x.name}${took > 1 ? ` x${took}` : ""}`, "just now"]);
    this.note = left ? `Took ${took}; your bag is full.` : `Took ${x.name}.`; this.sel = null;
    bagChanged();
  }
}
