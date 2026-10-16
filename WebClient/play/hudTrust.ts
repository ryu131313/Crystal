// Trust merchant (market) mock for the scene lab's UI tester (window style from hudWindow.ts), an auction-house take on
// the desktop's TrustMerchantDialog on the server's rules (Globals + PlayerObject ConsignItem / MarketBuy / MarketSellNow
// / MarketGetBack): consign for ConsignmentCost 5,000 gold at a price of MinConsignment 5,000 to MaxConsignment
// 50,000,000, listed for ConsignmentLength 7 days; auctions cost AuctionCost 5,000 with a starting bid of MinStartingBid 0
// to MaxStartingBid 50,000, a bid must beat the current one, the outbid player is refunded by mail, the seller may Sell
// now to the current bid; sales pay the seller less Commission 5 %; sold and expired listings are collected from the
// merchant. Tabs: Browse (ItemType categories with counts, search, filters: usable by me, grade, buy now / auction,
// price range; sortable listing table with icons and the item tooltip, time left, price or bid; a detail pane with a
// 14-sale price history sparkline, Buy now with a confirm step and gold after, bids with +5 % / +10 % / +25 % quick bids,
// a watch star), My listings (collect sold / expired, Sell now, cancel, Collect all), Sell (pick from your bag, Consign
// or Auction, a suggested price from recent sales, fee, commission, what you receive, the server's limits), Watchlist
// and History. Items and their stats are real (play/lab-data/items.json); listings, sellers, prices, the price history
// and the market traffic are mock. Simulate > Trust merchant opens it.

import { findIn, GRADE, itemData, norm, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface TrustHost { gold(): number; spend(n: number): boolean; gain(n: number): void; level(): number; cls(): string; say(text: string): void }
const COST = 5000, MIN_CONSIGN = 5000, MAX_CONSIGN = 50_000_000, MIN_BID = 0, MAX_BID = 50_000, COMMISSION = 0.05, DAYS = 7;
type Kind = "Consign" | "Auction";
interface Listing { id: number; name: string; count: number; seller: string; kind: Kind; price: number; bid: number; bids: number; bidder: string; ends: number; status: "active" | "sold" | "expired" }
type Tab = "Browse" | "My listings" | "Sell" | "Watchlist" | "History";
const SELLERS = ["Garrick", "Elowen", "Brisk", "Tamsin", "Odo", "Mirelle", "Kaelith", "Aldric", "Pip", "Nyra", "Talia", "Corin"];
const BAG = ["SoulRing", "GoldDragonRing", "BugNecklace", "MirNecklace", "SmashWheel", "CrystalArmour(M)", "HeavenSword", "HealthStone(M)", "(HP)DrugXL", "RedOrchidRing", "SpiritHelmet", "TarragonBoots"];
const TYPES = ["Weapon", "Armour", "Helmet", "Necklace", "Bracelet", "Ring", "Belt", "Boots", "Stone", "Torch", "Amulet", "Potion", "Scroll", "Mount", "Other"];
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");
const hash = (s: string, salt = 0) => { let x = 2166136261 ^ salt; for (const c of s) x = Math.imul(x ^ c.charCodeAt(0), 16777619); return ((x >>> 0) % 10000) / 10000; };
const left = (ms: number) => {
  if (ms <= 0) return "Ended";
  const m = Math.floor(ms / 60000), d = Math.floor(m / 1440), hh = Math.floor((m % 1440) / 60);
  return d ? `${d}d ${hh}h` : hh ? `${hh}h ${m % 60}m` : `${m}m ${Math.floor(ms / 1000) % 60}s`;
};

const CSS = `
.hudm .tmwin { width: 1000px; }
.hudm .tmwin .panel { height: 620px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .tmwin .top { display: flex; align-items: center; gap: 4px; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .tmwin .top .t { padding: 7px 14px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .tmwin .top .t.on { color: var(--ac); border-bottom-color: var(--ac); } .hudm .tmwin .top .t b { margin-left: 5px; padding: 0 5px; border-radius: 7px; font-size: 9px; color: #000; background: #f2c94c; }
.hudm .tmwin .top .sp { flex: 1; } .hudm .tmwin .top .gold { font-size: 12px; color: var(--dim); } .hudm .tmwin .top .gold b { color: #f2c94c; font-size: 14px; }
.hudm .tmwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .tmwin .cats { width: 170px; flex: none; overflow-y: auto; padding: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 2px; scrollbar-width: thin; }
.hudm .tmwin .cats input[type=text] { height: 26px; box-sizing: border-box; padding: 0 9px; margin-bottom: 6px; border-radius: 13px; border: 1px solid var(--fr); background: #05070c; color: var(--tx); font-size: 12px; outline: none; }
.hudm .tmwin .cats input[type=text]:focus { border-color: var(--ac); }
.hudm .tmwin .ct { display: flex; justify-content: space-between; padding: 4px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.hudm .tmwin .ct:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); } .hudm .tmwin .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .tmwin .ct small { color: var(--dim); font-size: 10px; } .hudm .tmwin .ct.zero { opacity: .4; }
.hudm .tmwin .fl { margin-top: 8px; display: flex; flex-direction: column; gap: 6px; font-size: 11px; color: var(--dim); }
.hudm .tmwin .fl .hd { font-size: 9px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; }
.hudm .tmwin .fl label { display: flex; align-items: center; gap: 6px; cursor: pointer; color: var(--tx); } .hudm .tmwin .fl input[type=checkbox] { accent-color: var(--ac); margin: 0; }
.hudm .tmwin .seg { display: flex; border-radius: 9px; overflow: hidden; border: 1px solid var(--fr); }
.hudm .tmwin .seg div { flex: 1; text-align: center; padding: 2px 4px; cursor: pointer; font-size: 10px; color: var(--dim); } .hudm .tmwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
.hudm .tmwin .gr { display: flex; gap: 3px; } .hudm .tmwin .gr div { flex: 1; height: 16px; border-radius: 3px; cursor: pointer; opacity: .35; border: 1px solid #000; } .hudm .tmwin .gr div.on { opacity: 1; box-shadow: 0 0 6px currentColor; }
.hudm .tmwin .list { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .tmwin .lh, .hudm .tmwin .lr { display: grid; grid-template-columns: 34px 1fr 44px 74px 70px 110px; gap: 8px; align-items: center; padding: 0 10px; }
.hudm .tmwin .lh { height: 28px; font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); border-bottom: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .tmwin .lh span { cursor: pointer; user-select: none; } .hudm .tmwin .lh span.on { color: var(--ac); } .hudm .tmwin .lh span:last-child { text-align: right; }
.hudm .tmwin .rows { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .tmwin .lr { min-height: 40px; cursor: pointer; font-size: 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 20%, transparent); }
.hudm .tmwin .lr:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); } .hudm .tmwin .lr.sel { background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .tmwin .lr .eq { width: 32px; height: 32px; } .hudm .tmwin .lr .nm { min-width: 0; } .hudm .tmwin .lr .nm b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .tmwin .lr .nm small { font-size: 10px; color: var(--dim); } .hudm .tmwin .lr .lv.no { color: #ff7a6a; } .hudm .tmwin .lr .tl { font-size: 11px; color: var(--dim); font-variant-numeric: tabular-nums; } .hudm .tmwin .lr .tl.soon { color: #ff9a3c; }
.hudm .tmwin .kd { font-size: 9px; font-weight: 800; padding: 1px 6px; border-radius: 6px; } .hudm .tmwin .kd.Consign { color: #6fe08a; border: 1px solid #4a9a5a; } .hudm .tmwin .kd.Auction { color: #f2c94c; border: 1px solid #a88a2a; }
.hudm .tmwin .pr { text-align: right; font-variant-numeric: tabular-nums; } .hudm .tmwin .pr b { color: #f2c94c; } .hudm .tmwin .pr small { display: block; font-size: 10px; color: var(--dim); } .hudm .tmwin .pr small.me { color: #6fe08a; }
.hudm .tmwin .star { cursor: pointer; color: var(--dim); margin-left: 4px; } .hudm .tmwin .star.on { color: #f2c94c; text-shadow: 0 0 6px rgba(242,201,76,.6); }
.hudm .tmwin .none { padding: 40px; text-align: center; color: var(--dim); }
.hudm .tmwin .det { width: 290px; flex: none; overflow-y: auto; padding: 12px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px; }
.hudm .tmwin .det > * { flex-shrink: 0; }
.hudm .tmwin .dh { display: flex; gap: 10px; align-items: center; } .hudm .tmwin .dh .eq { width: 52px; height: 52px; } .hudm .tmwin .dh b { display: block; font-size: 15px; } .hudm .tmwin .dh small { color: var(--dim); font-size: 11px; }
.hudm .tmwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; }
.hudm .tmwin .kv { display: flex; justify-content: space-between; padding: 2px 0; } .hudm .tmwin .kv span { color: var(--dim); } .hudm .tmwin .kv b { color: #fff; } .hudm .tmwin .kv b.g { color: #f2c94c; } .hudm .tmwin .kv b.r { color: #ff7a6a; } .hudm .tmwin .kv b.ok { color: #6fe08a; }
.hudm .tmwin .big { font-size: 24px; font-weight: 800; color: #f2c94c; text-shadow: 0 0 10px rgba(242,201,76,.4); font-variant-numeric: tabular-nums; }
.hudm .tmwin .spark { width: 100%; height: 54px; } .hudm .tmwin .spark path.l { fill: none; stroke: var(--ac); stroke-width: 1.6; filter: drop-shadow(0 0 3px var(--glow)); } .hudm .tmwin .spark path.a { fill: color-mix(in srgb, var(--ac) 18%, transparent); }
.hudm .tmwin .spark circle { fill: #f2c94c; }
.hudm .tmwin .row { display: flex; gap: 6px; } .hudm .tmwin .row .btn { flex: 1; }
.hudm .tmwin .btn.tmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); height: 30px; font-size: 13px; }
.hudm .tmwin .btn.gold { color: #000; border-color: #f2c94c; background: linear-gradient(180deg, #ffe48a, #d9a520); font-weight: 800; height: 30px; font-size: 13px; }
.hudm .tmwin .btn.dis { opacity: .4; pointer-events: none; } .hudm .tmwin .btn.dng:hover { border-color: #e05555; }
.hudm .tmwin input[type=number] { width: 100%; height: 30px; box-sizing: border-box; padding: 0 10px; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #05070c; color: #f2c94c; font-size: 15px; font-weight: 700; outline: none; }
.hudm .tmwin input[type=number]:focus { border-color: var(--ac); }
.hudm .tmwin .qb { display: flex; gap: 4px; } .hudm .tmwin .qb div { flex: 1; text-align: center; padding: 3px; border-radius: 4px; cursor: pointer; font-size: 10px; border: 1px solid var(--fr); }
.hudm .tmwin .qb div:hover { border-color: var(--ac); color: #fff; }
.hudm .tmwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; } .hudm .tmwin .note.err { color: #ff7a6a; } .hudm .tmwin .note.ok { color: #6fe08a; }
.hudm .tmwin .confirm { border-color: #f2c94c; background: rgba(242,201,76,.08); }
.hudm .tmwin .mine { flex: 1; min-width: 0; overflow-y: auto; padding: 10px 14px; display: flex; flex-direction: column; gap: 6px; }
.hudm .tmwin .ml { display: grid; grid-template-columns: 40px 1fr 80px 100px 220px; gap: 10px; align-items: center; padding: 6px 10px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); font-size: 12px; }
.hudm .tmwin .ml .eq { width: 36px; height: 36px; } .hudm .tmwin .ml .st { font-size: 10px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; }
.hudm .tmwin .ml .st.active { color: var(--ac); } .hudm .tmwin .ml .st.sold { color: #6fe08a; } .hudm .tmwin .ml .st.expired { color: #ff9a3c; }
.hudm .tmwin .ml.sold { border-color: #4a9a5a; background: rgba(80,200,110,.06); } .hudm .tmwin .ml .acts { display: flex; gap: 4px; justify-content: flex-end; } .hudm .tmwin .ml .acts .btn { white-space: nowrap; }
.hudm .tmwin .sell { flex: 1; min-width: 0; display: flex; }
.hudm .tmwin .sell .bag { width: 300px; flex: none; padding: 12px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .tmwin .sell .grid { display: grid; grid-template-columns: repeat(6, 40px); gap: 4px; } .hudm .tmwin .sell .grid .eq { width: 40px; height: 40px; }
.hudm .tmwin .sell .form { flex: 1; padding: 14px 18px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .tmwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .tmwin .kindc { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.hudm .tmwin .kindc div { padding: 10px; border-radius: var(--rad); cursor: pointer; border: 1px solid var(--fr); font-size: 11px; color: var(--dim); } .hudm .tmwin .kindc b { display: block; font-size: 14px; color: #fff; }
.hudm .tmwin .kindc div.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 0 0 12px color-mix(in srgb, var(--ac) 15%, transparent); }
.hudm .tmwin .hist { flex: 1; overflow-y: auto; padding: 10px 14px; font-size: 12px; } .hudm .tmwin .hist div { padding: 5px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .tmwin .hist small { color: var(--dim); margin-right: 8px; font-variant-numeric: tabular-nums; } .hudm .tmwin .hist b { color: #f2c94c; }
.hudm .tmwin .toast { position: absolute; left: 50%; bottom: 20px; z-index: 9; translate: -50% 0; padding: 8px 18px; border-radius: var(--rad); font-size: 13px; font-weight: 700; color: #fff;
  background: color-mix(in srgb, var(--ac) 40%, var(--pb)); border: 1px solid var(--ac); box-shadow: 0 0 20px var(--glow); animation: hudm-tmtoast 2.6s ease-out forwards; pointer-events: none; }
@keyframes hudm-tmtoast { 0% { opacity: 0; translate: -50% 10px; } 10%, 80% { opacity: 1; translate: -50% 0; } 100% { opacity: 0; } }
`;

export class TrustMock extends HudWindow {
  private items = new Map<string, Item>();
  private list: Listing[] = [];
  private nextId = 1;
  private tab: Tab = "Browse";
  private cat = "All";
  private q = "";
  private usable = false;
  private kinds: "All" | Kind = "All";
  private grades = new Set(["Common", "Rare", "Legendary", "Mythical", "Heroic", "None"]);
  private sort: [string, number] = ["time", 1];
  private sel: number | null = null;
  private confirm = false;
  private bidIn = "";
  private watch = new Set<number>();
  private hist: [number, string][] = [];
  private bag = [...BAG];
  private sellPick: string | null = null;
  private sellKind: Kind = "Consign";
  private sellPrice = "";
  private note: [string, string] = ["", ""];
  private rowsEl: HTMLElement | null = null;

  constructor(private readonly host: TrustHost) {
    super("tmwin", 12, 20);
    injectCss("hudm-trust-css", CSS);
    void itemData().then((d) => { if (!d) return; for (const i of d.items) this.items.set(norm(i.name), i); this.seed(); if (this.open) this.render(); });
    window.setInterval(() => this.tick(), 1000);
    this.el.addEventListener("mouseenter", () => { this.hovering = true; });
    this.el.addEventListener("mouseleave", () => { this.hovering = false; });
  }
  /** Opens at the trust merchant (lab Simulate button). */
  openAtNpc(): void { this.tab = "Browse"; this.toggle(true); }
  /** Opens Browse searching for an item (crafting's "find on market"). */
  search(name: string): void { this.tab = "Browse"; this.cat = "All"; this.q = name; this.sel = null; this.toggle(true); this.render(); }
  protected override shown(): void { this.render(); }

  private item(n: string): Item | undefined { return findIn(this.items, n); }
  /** Mock market value: ItemInfo.Price scaled by grade and a per-item factor (prices floor at the consignment minimum). */
  private value(n: string): number {
    const it = this.item(n); if (!it) return 10000;
    const g = { Rare: 3, Legendary: 8, Mythical: 14, Heroic: 25 }[it.grade] ?? 1;
    return Math.max(MIN_CONSIGN, Math.round((it.price || 2000) * g * (1.5 + hash(n) * 3) / 100) * 100);
  }
  private seed(): void {
    const now = Date.now();
    const names = [...this.items.keys()].map((k) => this.items.get(k)!.name).filter((n) => !["SealedHero"].includes(n));
    for (let i = 0; i < 64; i++) {
      const n = names[Math.floor(hash("l" + i, 3) * names.length)]!;
      const it = this.item(n)!;
      const auction = hash("a" + i) < 0.32;
      const v = this.value(n) * (0.75 + hash("p" + i) * 0.6);
      const count = it.stack > 1 ? 1 + Math.floor(hash("c" + i) * Math.min(it.stack, 40)) : 1;
      const start = Math.min(MAX_BID, Math.round(v * 0.02 / 100) * 100);
      const bids = auction ? Math.floor(hash("b" + i) * 6) : 0;
      this.list.push({ id: this.nextId++, name: n, count, seller: SELLERS[i % SELLERS.length]!, kind: auction ? "Auction" : "Consign", price: auction ? start : Math.round(v * count / 100) * 100,
        bid: auction && bids ? Math.round(start * (1 + bids * 0.4) + 500) : 0, bids, bidder: bids ? SELLERS[(i + 3) % SELLERS.length]! : "",
        ends: now + (auction && i % 5 === 0 ? (2 + i % 9) * 60000 : Math.floor(hash("t" + i) * DAYS * 864e5)), status: "active" });
    }
    // Your own listings: one consign that sold, one active auction with a bid, one expired.
    const mk = (name: string, kind: Kind, price: number, status: Listing["status"], bid = 0, endsIn = 3 * 864e5) =>
      this.list.push({ id: this.nextId++, name, count: 1, seller: "GMRyu", kind, price, bid, bids: bid ? 2 : 0, bidder: bid ? "Garrick" : "", ends: now + endsIn, status });
    mk("WhiteGoldRing", "Consign", 85000, "sold"); mk("SpiritNecklace", "Auction", 20000, "active", 34000, 40 * 60000); mk("BrassHelmet", "Consign", 12000, "expired", 0, -1);
    this.log("Market opened. 3 of your listings need attention.");
  }
  private log(t: string): void { this.hist.unshift([Date.now(), t]); if (this.hist.length > 60) this.hist.pop(); }
  private toast(t: string): void { this.el.querySelector(".panel")?.append(h("div", "toast", t)); window.setTimeout(() => this.el.querySelector(".toast")?.remove(), 2600); }

  /** Live market: countdowns, auctions ending, other players buying, bidding and outbidding you. */
  private hovering = false;
  private tick(): void {
    const now = Date.now();
    let changed = false;
    for (const l of this.list) if (l.status === "active" && l.ends <= now) {
      if (l.kind === "Auction" && l.bid && l.bidder === "GMRyu") { this.host.say(`You won ${l.name} for ${fmt(l.bid)} gold (sent by mail).`); this.log(`Won ${l.name} for ${fmt(l.bid)}`); }
      l.status = l.seller === "GMRyu" ? (l.bid ? "sold" : "expired") : "expired";
      changed = true;
    }
    if (Math.random() < 0.12) {
      const act = this.list.filter((l) => l.status === "active" && l.seller !== "GMRyu");
      const l = act[Math.floor(Math.random() * act.length)];
      if (l && l.kind === "Auction") {
        changed = true;
        if (l.bidder === "GMRyu") { this.host.say(`You were outbid on ${l.name}; your ${fmt(l.bid)} gold is refunded by mail.`); this.log(`Outbid on ${l.name}`); this.toast(`Outbid on ${l.name}!`); }
        l.bid = Math.max(l.price, l.bid) + Math.max(100, Math.round(Math.max(l.price, l.bid) * 0.08 / 100) * 100); l.bids++; l.bidder = SELLERS[Math.floor(Math.random() * SELLERS.length)]!;
      }
    }
    if (Math.random() < 0.05) {
      const mine = this.list.find((l) => l.seller === "GMRyu" && l.status === "active" && l.kind === "Consign");
      if (mine) { changed = true; mine.status = "sold"; this.host.say(`Your ${mine.name} sold for ${fmt(mine.price)} gold. Collect it from the trust merchant.`); this.log(`Sold ${mine.name} for ${fmt(mine.price)}`); this.toast(`${mine.name} sold!`); }
    }
    if (!this.open) return;
    // Redraw on market events only while the pointer is elsewhere (a redraw under the pointer would drop the item
    // tooltip and a half-typed bid); countdowns tick in place.
    if (changed && !this.hovering && !this.confirm && document.activeElement?.closest(".tmwin") == null) this.render();
    else for (const e of this.el.querySelectorAll<HTMLElement>("[data-ends]")) e.textContent = left(+e.dataset.ends! - now);
  }

  private render(): void {
    const scroll = this.rowsEl?.scrollTop ?? 0;
    const pending = this.list.filter((l) => l.seller === "GMRyu" && l.status !== "active").length;
    const top = h("div", "top");
    for (const t of ["Browse", "My listings", "Sell", "Watchlist", "History"] as Tab[]) {
      const e = h("div", "t" + (this.tab === t ? " on" : ""), t, ...(t === "My listings" && pending ? [h("b", "", String(pending))] : t === "Watchlist" && this.watch.size ? [h("b", "", String(this.watch.size))] : []));
      e.addEventListener("click", () => { this.tab = t; this.confirm = false; this.render(); });
      top.append(e);
    }
    top.append(h("span", "sp"), h("span", "gold", "Gold ", h("b", "", fmt(this.host.gold()))));
    let body: HTMLElement;
    if (this.tab === "Browse" || this.tab === "Watchlist") body = h("div", "body", ...(this.tab === "Browse" ? [this.cats()] : []), this.table(), this.detail());
    else if (this.tab === "My listings") body = h("div", "body", this.mine());
    else if (this.tab === "Sell") body = h("div", "body", this.sell());
    else { const hs = h("div", "hist"); for (const [t, s] of this.hist) hs.append(h("div", "", h("small", "", new Date(t).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" })), s)); body = h("div", "body", hs); }
    this.el.replaceChildren(h("div", "panel", this.titleBar("TRUST MERCHANT", "Close"), top, body));
    if (this.rowsEl) this.rowsEl.scrollTop = scroll;
  }

  // — Browse —
  private typeOf(it: Item | undefined): string { return it && TYPES.includes(it.type) ? it.type : "Other"; }
  private visible(): Listing[] {
    const q = this.q.trim().toLowerCase();
    let ls = this.list.filter((l) => l.status === "active" && l.seller !== "GMRyu");
    if (this.tab === "Watchlist") return this.list.filter((l) => this.watch.has(l.id));
    ls = ls.filter((l) => {
      const it = this.item(l.name);
      if (this.cat !== "All" && this.typeOf(it) !== this.cat) return false;
      if (q && !l.name.toLowerCase().includes(q)) return false;
      if (this.kinds !== "All" && l.kind !== this.kinds) return false;
      if (it && !this.grades.has(it.grade)) return false;
      if (this.usable && it && ((it.reqType === "Level" && it.req > this.host.level()) || (it.reqClass !== "None" && !(it.reqClass === "WarWizTao" ? ["Warrior", "Wizard", "Taoist"] : it.reqClass.split(/,\s*/)).includes(this.host.cls())))) return false;
      return true;
    });
    const [k, dir] = this.sort;
    const val = (l: Listing) => k === "name" ? l.name : k === "lv" ? this.item(l.name)?.req ?? 0 : k === "price" ? (l.kind === "Auction" ? Math.max(l.bid, l.price) : l.price) : l.ends;
    return ls.sort((a, b) => { const x = val(a), y = val(b); return (x < y ? -1 : x > y ? 1 : 0) * dir; });
  }
  private cats(): HTMLElement {
    const c = h("div", "cats");
    const s = h("input"); s.type = "text"; s.placeholder = "Search items…"; s.value = this.q;
    s.addEventListener("keydown", (e) => e.stopPropagation());
    s.addEventListener("input", () => { this.q = s.value; this.render(); this.el.querySelector<HTMLInputElement>(".cats input[type=text]")?.focus(); });
    c.append(s);
    const active = this.list.filter((l) => l.status === "active" && l.seller !== "GMRyu");
    for (const t of ["All", ...TYPES]) {
      const n = t === "All" ? active.length : active.filter((l) => this.typeOf(this.item(l.name)) === t).length;
      const e = h("div", "ct" + (this.cat === t ? " on" : "") + (n ? "" : " zero"), h("span", "", t), h("small", "", String(n)));
      e.addEventListener("click", () => { this.cat = t; this.sel = null; this.render(); });
      c.append(e);
    }
    const fl = h("div", "fl", h("div", "hd", "Filters"));
    const u = h("input"); u.type = "checkbox"; u.checked = this.usable; u.addEventListener("change", () => { this.usable = u.checked; this.render(); });
    fl.append(h("label", "", u, "Usable by me"));
    const seg = h("div", "seg");
    for (const k of ["All", "Consign", "Auction"] as const) { const d = h("div", this.kinds === k ? "on" : "", k === "Consign" ? "Buy now" : k); d.addEventListener("click", () => { this.kinds = k; this.render(); }); seg.append(d); }
    const gr = h("div", "gr");
    for (const g of ["Common", "Rare", "Legendary", "Mythical"]) { const d = h("div", this.grades.has(g) ? "on" : ""); d.style.background = GRADE[g]!; d.style.color = GRADE[g]!; d.title = g; d.addEventListener("click", () => { if (!this.grades.delete(g)) this.grades.add(g); this.render(); }); gr.append(d); }
    fl.append(h("div", "hd", "Listing"), seg, h("div", "hd", "Grade"), gr);
    c.append(fl);
    return c;
  }
  private table(): HTMLElement {
    const head = h("div", "lh", h("span", ""));
    for (const [k, l] of [["name", "Item"], ["lv", "Lv"], ["", "Type"], ["time", "Time left"], ["price", "Price / bid"]] as const) {
      const s = h("span", this.sort[0] === k ? "on" : "", l + (this.sort[0] === k ? (this.sort[1] > 0 ? " ▲" : " ▼") : ""));
      if (k) s.addEventListener("click", () => { this.sort = [k, this.sort[0] === k ? -this.sort[1] : 1]; this.render(); });
      head.append(s);
    }
    const rows = h("div", "rows");
    this.rowsEl = rows;
    const vis = this.visible();
    const now = Date.now();
    for (const l of vis) {
      const it = this.item(l.name);
      const lvOk = !it || it.reqType !== "Level" || it.req <= this.host.level();
      const price = l.kind === "Auction" ? Math.max(l.bid, l.price) : l.price;
      const sub = l.kind === "Auction" ? (l.bidder === "GMRyu" ? h("small", "me", "Your bid") : h("small", "", l.bids ? `${l.bids} bid${l.bids > 1 ? "s" : ""}` : "No bids")) : l.count > 1 ? h("small", "", `${fmt(price / l.count)} each`) : h("small", "", "Buy now");
      const tl = h("span", "tl" + (l.ends - now < 3600000 ? " soon" : ""), left(l.ends - now)); tl.dataset.ends = String(l.ends);
      const nm = h("div", "nm", h("b", "", l.name + (l.count > 1 ? ` ×${l.count}` : "")), h("small", "", `by ${l.seller}`));
      (nm.firstChild as HTMLElement).style.color = GRADE[it?.grade ?? "Common"]!;
      const star = h("span", "star" + (this.watch.has(l.id) ? " on" : ""), "★"); star.addEventListener("click", (e) => { e.stopPropagation(); if (!this.watch.delete(l.id)) this.watch.add(l.id); this.render(); });
      (nm.lastChild as HTMLElement).append(star);
      const r = h("div", "lr" + (this.sel === l.id ? " sel" : ""), itemCell(this.el, l.name, it?.img ?? -1, l.count), nm, h("span", "lv" + (lvOk ? "" : " no"), it?.req ? String(it.req) : "—"),
        h("span", "", h("span", "kd " + l.kind, l.kind === "Consign" ? "BUY NOW" : "AUCTION")), tl, h("div", "pr", h("b", "", fmt(price)), sub));
      r.addEventListener("click", () => { this.sel = l.id; this.confirm = false; this.bidIn = ""; this.note = ["", ""]; this.render(); });
      rows.append(r);
    }
    if (!vis.length) rows.append(h("div", "none", this.tab === "Watchlist" ? "Star a listing to watch it here." : "No listings match. Try another category or filter."));
    return h("div", "list", head, rows);
  }
  private detail(): HTMLElement {
    const d = h("div", "det");
    const l = this.list.find((x) => x.id === this.sel);
    if (!l) { d.append(h("div", "note", "Pick a listing to see its price history and to buy or bid.")); return d; }
    const it = this.item(l.name);
    const nm = h("b", "", l.name); nm.style.color = GRADE[it?.grade ?? "Common"]!;
    d.append(h("div", "dh", itemCell(this.el, l.name, it?.img ?? -1, l.count), h("div", "", nm, h("small", "", `${it?.grade ?? ""} ${it?.type ?? ""} · seller ${l.seller}`))));
    // Price history (mock): 14 sales around the item's value.
    const v = this.value(l.name), pts = Array.from({ length: 14 }, (_, i) => v * (0.7 + hash(l.name, 10 + i) * 0.6) * (0.9 + i * 0.012));
    const lo = Math.min(...pts), hi = Math.max(...pts), avg = pts.reduce((a, b) => a + b, 0) / pts.length;
    const W = 266, H = 54, x = (i: number) => i / 13 * (W - 6) + 3, y = (p: number) => H - 4 - (p - lo) / Math.max(1, hi - lo) * (H - 10);
    const line = pts.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(p).toFixed(1)}`).join("");
    const spark = h("div", "");
    spark.innerHTML = `<svg class="spark" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none"><path class="a" d="${line}L${x(13)},${H}L${x(0)},${H}Z"/><path class="l" d="${line}"/><circle cx="${x(13)}" cy="${y(pts[13]!)}" r="3"/></svg>`;
    d.append(h("div", "box", h("div", "kv", h("span", "", "Recent sales (14)"), h("b", "g", `avg ${fmt(avg)}`)), spark, h("div", "kv", h("span", "", `low ${fmt(lo)}`), h("span", "", `high ${fmt(hi)}`))));
    const gold = this.host.gold();
    const box = h("div", "box" + (this.confirm ? " confirm" : ""));
    if (l.status !== "active") box.append(h("div", "note", "This listing has ended."));
    else if (l.kind === "Consign") {
      const after = gold - l.price;
      box.append(h("div", "big", fmt(l.price)), h("div", "kv", h("span", "", "Time left"), h("b", "", left(l.ends - Date.now()))), h("div", "kv", h("span", "", "Gold after"), h("b", after < 0 ? "r" : "ok", fmt(after))));
      if (!this.confirm) { const b = h("div", "btn gold" + (after < 0 ? " dis" : ""), after < 0 ? "Not enough gold" : "Buy now"); b.addEventListener("click", () => { this.confirm = true; this.render(); }); box.append(h("div", "row", b)); }
      else {
        const yes = h("div", "btn gold", `Confirm ${fmt(l.price)}`); yes.addEventListener("click", () => this.buy(l));
        const no = h("div", "btn", "Cancel"); no.addEventListener("click", () => { this.confirm = false; this.render(); });
        box.append(h("div", "note", `Buy ${l.name}${l.count > 1 ? " ×" + l.count : ""} from ${l.seller}?`), h("div", "row", yes, no));
      }
    } else {
      const cur = Math.max(l.bid, l.price), min = l.bid ? l.bid + 1 : l.price;
      box.append(h("div", "big", fmt(cur)), h("div", "kv", h("span", "", l.bid ? `Current bid (${l.bids})` : "Starting bid"), h("b", l.bidder === "GMRyu" ? "ok" : "", l.bidder === "GMRyu" ? "You lead" : l.bidder || "—")));
      const tl = h("b", "", left(l.ends - Date.now())); tl.dataset.ends = String(l.ends);
      box.append(h("div", "kv", h("span", "", "Ends in"), tl));
      const inp = h("input"); inp.type = "number"; inp.min = String(min); inp.placeholder = `${fmt(min)} or more`; inp.value = this.bidIn;
      inp.addEventListener("keydown", (e) => e.stopPropagation());
      inp.addEventListener("input", () => { this.bidIn = inp.value; });
      const qb = h("div", "qb");
      for (const p of [5, 10, 25]) { const q = h("div", "", `+${p}%`); q.addEventListener("click", () => { this.bidIn = String(Math.max(min, Math.ceil(cur * (1 + p / 100) / 100) * 100)); this.render(); }); qb.append(q); }
      const b = h("div", "btn tmain" + (l.bidder === "GMRyu" ? " dis" : ""), l.bidder === "GMRyu" ? "You are the top bidder" : "Place bid");
      b.addEventListener("click", () => this.bid(l, +this.bidIn));
      box.append(inp, qb, h("div", "row", b), h("div", "note", "If someone outbids you, your gold comes back by mail."));
    }
    if (this.note[0]) box.append(h("div", "note " + this.note[1], this.note[0]));
    const star = h("div", "btn", this.watch.has(l.id) ? "★ Watching" : "☆ Watch"); star.addEventListener("click", () => { if (!this.watch.delete(l.id)) this.watch.add(l.id); this.render(); });
    d.append(box, h("div", "row", star));
    return d;
  }
  private buy(l: Listing): void {
    if (!this.host.spend(l.price)) { this.note = ["Not enough gold.", "err"]; this.confirm = false; this.render(); return; }
    l.status = "sold"; this.confirm = false; this.sel = null;
    this.host.say(`You bought ${l.name}${l.count > 1 ? " ×" + l.count : ""} from ${l.seller} for ${fmt(l.price)} gold.`);
    this.log(`Bought ${l.name} for ${fmt(l.price)}`); this.render(); this.toast(`Bought ${l.name}!`);
  }
  private bid(l: Listing, amount: number): void {
    const min = l.bid ? l.bid + 1 : l.price;
    if (!amount || amount < min) { this.note = [`Your bid must be at least ${fmt(min)}.`, "err"]; this.render(); return; }
    if (!this.host.spend(amount)) { this.note = ["Not enough gold.", "err"]; this.render(); return; }
    if (l.bidder && l.bidder !== "GMRyu") this.log(`${l.bidder} was outbid on ${l.name} (refunded by mail)`);
    l.bid = amount; l.bids++; l.bidder = "GMRyu"; this.bidIn = "";
    this.note = [`You bid ${fmt(amount)} on ${l.name}.`, "ok"];
    this.log(`Bid ${fmt(amount)} on ${l.name}`); this.render();
  }

  // — My listings —
  private mine(): HTMLElement {
    const m = h("div", "mine");
    const ls = this.list.filter((l) => l.seller === "GMRyu");
    const done = ls.filter((l) => l.status !== "active");
    const all = h("div", "btn tmain" + (done.length ? "" : " dis"), `Collect all (${done.length})`); all.addEventListener("click", () => { for (const l of done) this.collect(l); this.render(); });
    m.append(h("div", "row", h("div", "note", `${ls.length} listings · sold items pay the price less ${COMMISSION * 100}% commission · expired items come back to your bag`), all));
    for (const l of ls) {
      const it = this.item(l.name);
      const acts = h("div", "acts");
      const b = (t: string, cls: string, fn: () => void) => { const e = h("div", "btn " + cls, t); e.addEventListener("click", fn); acts.append(e); };
      if (l.status === "sold") b(`Collect ${fmt((l.kind === "Auction" ? l.bid : l.price) * (1 - COMMISSION))}`, "gold", () => { this.collect(l); this.render(); });
      if (l.status === "expired") b("Take back", "", () => { this.collect(l); this.render(); });
      if (l.status === "active" && l.kind === "Auction" && l.bid > l.price) b(`Sell now ${fmt(l.bid)}`, "tmain", () => { l.status = "sold"; this.log(`Sold ${l.name} now to ${l.bidder} for ${fmt(l.bid)}`); this.render(); });
      if (l.status === "active") b("Cancel", "dng", () => { l.status = "expired"; this.render(); });
      const tl = h("span", "", l.status === "active" ? left(l.ends - Date.now()) : ""); if (l.status === "active") tl.dataset.ends = String(l.ends);
      m.append(h("div", "ml " + l.status, itemCell(this.el, l.name, it?.img ?? -1), h("div", "", h("b", "", l.name), h("div", "note", `${l.kind === "Auction" ? `Auction · start ${fmt(l.price)}${l.bid ? ` · bid ${fmt(l.bid)} by ${l.bidder}` : ""}` : `Buy now ${fmt(l.price)}`}`)),
        h("span", "st " + l.status, l.status), tl, acts));
    }
    if (!ls.length) m.append(h("div", "none", "You have nothing listed. Use the Sell tab."));
    return m;
  }
  private collect(l: Listing): void {
    const i = this.list.indexOf(l);
    if (i < 0) return;
    if (l.status === "sold") { const g = Math.round((l.kind === "Auction" ? l.bid : l.price) * (1 - COMMISSION)); this.host.gain(g); this.host.say(`Collected ${fmt(g)} gold for ${l.name} (${COMMISSION * 100}% commission).`); this.log(`Collected ${fmt(g)} for ${l.name}`); }
    else { this.bag.push(l.name); this.log(`Took back ${l.name}`); }
    this.list.splice(i, 1);
  }

  // — Sell —
  private sell(): HTMLElement {
    const grid = h("div", "grid");
    for (const n of this.bag) { const it = this.item(n); const c = itemCell(this.el, n, it?.img ?? -1); if (this.sellPick === n) c.classList.add("sel"); c.addEventListener("click", () => { this.sellPick = n; this.sellPrice = ""; this.note = ["", ""]; this.render(); }); grid.append(c); }
    const bag = h("div", "bag", h("div", "lbl", "Your bag · pick an item"), grid);
    const form = h("div", "form");
    const n = this.sellPick;
    if (!n) form.append(h("div", "note", "Pick an item from your bag to list it."));
    else {
      const it = this.item(n);
      const v = this.value(n);
      const nm = h("b", "", n); nm.style.color = GRADE[it?.grade ?? "Common"]!;
      form.append(h("div", "dh", itemCell(this.el, n, it?.img ?? -1), h("div", "", nm, h("small", "", `Recent sales average ${fmt(v)} gold`))));
      const kc = h("div", "kindc");
      for (const [k, t, s] of [["Consign", "Buy now", `Fixed price ${fmt(MIN_CONSIGN)} – ${fmt(MAX_CONSIGN)}, ${DAYS} days`], ["Auction", "Auction", `Starting bid ${fmt(MIN_BID)} – ${fmt(MAX_BID)}, highest bid wins`]] as const) {
        const d = h("div", this.sellKind === k ? "on" : "", h("b", "", t), s); d.addEventListener("click", () => { this.sellKind = k; this.sellPrice = ""; this.render(); }); kc.append(d);
      }
      const suggest = this.sellKind === "Consign" ? Math.max(MIN_CONSIGN, Math.round(v * 0.95 / 100) * 100) : Math.min(MAX_BID, Math.round(v * 0.02 / 100) * 100);
      const inp = h("input"); inp.type = "number"; inp.placeholder = `Suggested ${fmt(suggest)}`; inp.value = this.sellPrice;
      inp.addEventListener("keydown", (e) => e.stopPropagation());
      inp.addEventListener("input", () => { this.sellPrice = inp.value; const p = +inp.value || 0; const r = this.el.querySelector(".recv"); if (r) r.textContent = fmt(p * (1 - COMMISSION)); });
      const use = h("div", "btn", `Use ${fmt(suggest)}`); use.addEventListener("click", () => { this.sellPrice = String(suggest); this.render(); });
      const p = +this.sellPrice || 0;
      const sum = h("div", "box",
        h("div", "kv", h("span", "", "Listing fee"), h("b", "g", fmt(COST))),
        h("div", "kv", h("span", "", `Commission (${COMMISSION * 100}%) on sale`), h("b", "", fmt(p * COMMISSION))),
        h("div", "kv", h("span", "", this.sellKind === "Auction" ? "You receive at the start bid" : "You receive"), h("b", "ok recv", fmt(p * (1 - COMMISSION)))),
        h("div", "kv", h("span", "", "Listed for"), h("b", "", `${DAYS} days`)));
      const go = h("div", "btn tmain", `List for ${fmt(COST)} gold`); go.addEventListener("click", () => this.listItem(n, +this.sellPrice));
      form.append(h("div", "lbl", "How to sell"), kc, h("div", "lbl", this.sellKind === "Consign" ? "Price" : "Starting bid"), h("div", "row", inp, use), sum, h("div", "row", go));
    }
    if (this.note[0]) form.append(h("div", "note " + this.note[1], this.note[0]));
    return h("div", "sell", bag, form);
  }
  /** PlayerObject.ConsignItem's checks. */
  private listItem(n: string, price: number): void {
    const err = (t: string) => { this.note = [t, "err"]; this.render(); };
    if (this.sellKind === "Consign" && (price < MIN_CONSIGN || price > MAX_CONSIGN)) return err(`The price must be between ${fmt(MIN_CONSIGN)} and ${fmt(MAX_CONSIGN)} gold.`);
    if (this.sellKind === "Auction" && (price < MIN_BID || price > MAX_BID)) return err(`The starting bid must be between ${fmt(MIN_BID)} and ${fmt(MAX_BID)} gold.`);
    if (!this.host.spend(COST)) return err(`You need ${fmt(COST)} gold for the listing fee.`);
    this.bag.splice(this.bag.indexOf(n), 1);
    this.list.push({ id: this.nextId++, name: n, count: 1, seller: "GMRyu", kind: this.sellKind, price, bid: 0, bids: 0, bidder: "", ends: Date.now() + DAYS * 864e5, status: "active" });
    this.log(`Listed ${n} (${this.sellKind === "Consign" ? "buy now" : "auction"}) at ${fmt(price)}`);
    this.sellPick = null; this.sellPrice = ""; this.note = [`${n} is listed for ${DAYS} days.`, "ok"];
    this.render(); this.toast(`${n} listed!`);
  }
}
