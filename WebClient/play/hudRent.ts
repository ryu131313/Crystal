// Item rental window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// ItemRentalDialog (the deal), ItemRentDialog (items you rented out) and ItemRentingDialog (the item you are renting), on
// the server's rules (PlayerObject.ItemRentalRequest / SetItemRentalFee / SetItemRentalPeriodLength / ItemRentalLockFee /
// ItemRentalLockItem / ConfirmItemRental, Envir.ReturnRentalItem): you face the other player; an owner may have at most 3
// items rented out; a player may rent only one item at a time; the owner puts the item and a period of 1 to 30 days and
// locks it; the renter pays the fee in gold and locks it; both confirm; the item goes to the renter bound with DontDrop,
// DontStore, DontSell, DontTrade, UnableToRent, DontUpgrade and UnableToDisassemble until the return date (or the renter's
// death), then it comes back to the owner by mail and can't be rented again for one more day; the owner gets the fee.
// Tabs: Rental desk (be the owner or the renter, a partner who answers, item from your bag, a day slider with the return
// date, a fee box with quick amounts and a per-day price, both locks, the confirm handshake and a signed contract),
// Rented out (3 slots, renters, time left, fee earned, returned and relock states), Renting (your rented item, its
// countdown and the restrictions) and a fast-forward button for the mock. Items are real (items.json); partners, prices
// and dates are mock. Simulate > Item rental.

import { findIn, GRADE, itemData, norm, type Item } from "./hudTooltip.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

export interface RentHost { gold(): number; spend(n: number): boolean; gain(n: number): void; say(text: string): void }
const MAX_OUT = 3, MIN_DAYS = 1, MAX_DAYS = 30, DAY = 864e5;
const BOUND = ["Can't drop", "Can't store", "Can't sell", "Can't trade", "Can't rent out", "Can't upgrade", "Can't disassemble"];
type Role = "Owner" | "Renter";
interface Out { name: string; renter: string; fee: number; until: number; status: "active" | "returned"; relock: number }
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");
const date = (t: number) => new Date(t).toLocaleDateString("en-GB", { weekday: "short", day: "numeric", month: "short" });
const left = (ms: number) => { if (ms <= 0) return "due"; const d = Math.floor(ms / DAY), hh = Math.floor((ms % DAY) / 36e5); return d ? `${d}d ${hh}h` : `${hh}h ${Math.floor((ms % 36e5) / 6e4)}m`; };

const CSS = `
.hudm .rnwin { width: 980px; }
.hudm .rnwin .panel { height: 590px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .rnwin .tabs { display: flex; gap: 2px; align-items: center; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rnwin .tabs .t { padding: 7px 14px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .rnwin .tabs .t.on { color: var(--ac); border-bottom-color: var(--ac); } .hudm .rnwin .tabs .t b { margin-left: 5px; padding: 0 5px; border-radius: 7px; font-size: 9px; color: #000; background: var(--ac); }
.hudm .rnwin .tabs .sp { flex: 1; } .hudm .rnwin .tabs .gold { font-size: 12px; color: var(--dim); } .hudm .rnwin .tabs .gold b { color: #f2c94c; }
.hudm .rnwin .ff { margin-left: 10px; font-size: 10px; padding: 3px 9px; border-radius: 9px; cursor: pointer; border: 1px dashed var(--fr); color: var(--dim); } .hudm .rnwin .ff:hover { color: #fff; border-color: var(--ac); }
.hudm .rnwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .rnwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); display: flex; justify-content: space-between; align-items: center; }
.hudm .rnwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .rnwin .seg { display: flex; border-radius: 12px; overflow: hidden; border: 1px solid var(--fr); }
.hudm .rnwin .seg div { padding: 3px 12px; cursor: pointer; font-size: 11px; color: var(--dim); } .hudm .rnwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
.hudm .rnwin .desk { flex: 1; display: flex; flex-direction: column; gap: 10px; padding: 12px 16px; min-width: 0; }
.hudm .rnwin .top { display: flex; align-items: center; gap: 12px; }
.hudm .rnwin .who { display: flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 20px; border: 1px solid var(--fr); background: color-mix(in srgb, var(--frl) 5%, transparent); font-size: 12px; }
.hudm .rnwin .who .av { width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 9px; font-weight: 800; border: 1px solid var(--ac); }
.hudm .rnwin .who small { display: block; font-size: 10px; color: var(--dim); } .hudm .rnwin .who .ok { color: #6fe08a; }
.hudm .rnwin .sides { flex: 1; min-height: 0; display: grid; grid-template-columns: 1fr 60px 1fr; gap: 10px; }
.hudm .rnwin .side { position: relative; display: flex; flex-direction: column; gap: 10px; padding: 12px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); transition: all .3s; }
.hudm .rnwin .side.lock { border-color: #6fe08a; box-shadow: inset 0 0 0 1px rgba(111,224,138,.3), 0 0 14px rgba(111,224,138,.15); }
.hudm .rnwin .side.conf { border-color: #f2c94c; box-shadow: inset 0 0 0 1px rgba(242,201,76,.4), 0 0 18px rgba(242,201,76,.25); }
.hudm .rnwin .side h4 { margin: 0; font-size: 12px; letter-spacing: .12em; text-transform: uppercase; color: var(--ac); display: flex; justify-content: space-between; }
.hudm .rnwin .side h4 span { font-size: 10px; letter-spacing: 0; text-transform: none; color: var(--dim); }
.hudm .rnwin .item { display: flex; gap: 10px; align-items: center; } .hudm .rnwin .item .eq { width: 56px; height: 56px; } .hudm .rnwin .item b { font-size: 15px; } .hudm .rnwin .item small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rnwin .bag { display: grid; grid-template-columns: repeat(7, 38px); gap: 3px; } .hudm .rnwin .bag .eq { width: 38px; height: 38px; } .hudm .rnwin .bag .eq.no { opacity: .3; }
.hudm .rnwin input[type=range] { width: 100%; accent-color: var(--ac); }
.hudm .rnwin .days { display: flex; justify-content: space-between; align-items: baseline; font-size: 11px; color: var(--dim); } .hudm .rnwin .days b { font-size: 22px; color: #fff; }
.hudm .rnwin .cal { display: grid; grid-template-columns: repeat(15, 1fr); gap: 2px; } .hudm .rnwin .cal i { height: 8px; border-radius: 2px; background: #05070c; } .hudm .rnwin .cal i.on { background: var(--ac); box-shadow: 0 0 4px var(--glow); }
.hudm .rnwin .fee { font-size: 26px; font-weight: 800; color: #f2c94c; text-shadow: 0 0 10px rgba(242,201,76,.4); font-variant-numeric: tabular-nums; } .hudm .rnwin .fee small { font-size: 11px; font-weight: 400; color: var(--dim); text-shadow: none; }
.hudm .rnwin .qb { display: flex; gap: 4px; flex-wrap: wrap; } .hudm .rnwin .qb div { padding: 3px 8px; border-radius: 4px; cursor: pointer; font-size: 11px; border: 1px solid var(--fr); } .hudm .rnwin .qb div:hover { border-color: #f2c94c; color: #fff; }
.hudm .rnwin .mid { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; font-size: 24px; color: var(--ac); }
.hudm .rnwin .mid i { font-style: normal; animation: hudm-rnswap 2s ease-in-out infinite; } @keyframes hudm-rnswap { 50% { transform: scale(1.15); text-shadow: 0 0 10px var(--glow); } }
.hudm .rnwin .rnrow { display: flex; gap: 6px; } .hudm .rnwin .rnrow .btn { flex: 1; }
.hudm .rnwin .btn.lk { color: #6fe08a; border-color: #4a9a5a; } .hudm .rnwin .btn.cf { color: #000; border-color: #f2c94c; background: linear-gradient(180deg, #ffe48a, #d9a520); font-weight: 800; }
.hudm .rnwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .rnwin .tag { position: absolute; right: 10px; top: 10px; font-size: 9px; font-weight: 800; letter-spacing: .1em; padding: 2px 8px; border-radius: 8px; }
.hudm .rnwin .tag.l { color: #000; background: #6fe08a; } .hudm .rnwin .tag.c { color: #000; background: #f2c94c; }
.hudm .rnwin .msg { font-size: 12px; color: var(--dim); min-height: 16px; } .hudm .rnwin .msg b { color: var(--tx); } .hudm .rnwin .msg.err { color: #ff8a6a; }
.hudm .rnwin .rules { width: 230px; flex: none; padding: 12px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 6px; font-size: 11px; color: var(--dim); overflow-y: auto; }
.hudm .rnwin .rules div { padding: 6px 8px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); line-height: 1.35; } .hudm .rnwin .rules b { color: var(--tx); }
.hudm .rnwin .contract { position: absolute; inset: 0; z-index: 9; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.6); animation: hudm-rnfade .3s; }
@keyframes hudm-rnfade { from { opacity: 0; } }
.hudm .rnwin .paper { position: relative; width: 380px; padding: 22px 26px; border-radius: 6px; color: #3a2a10; font-family: Georgia, serif; background: linear-gradient(180deg, #f4e6c4, #e2cc98); box-shadow: 0 20px 50px rgba(0,0,0,.7); }
.hudm .rnwin .paper h3 { margin: 0 0 8px; text-align: center; letter-spacing: .2em; font-size: 15px; } .hudm .rnwin .paper p { margin: 4px 0; font-size: 13px; }
.hudm .rnwin .stamp { position: absolute; right: 22px; bottom: 16px; padding: 4px 12px; border: 3px solid #b0261a; border-radius: 6px; color: #b0261a; font: 800 16px sans-serif; letter-spacing: .12em; transform: rotate(-12deg) scale(2); opacity: 0;
  animation: hudm-rnstamp .45s .35s cubic-bezier(.2,1.6,.4,1) forwards; } @keyframes hudm-rnstamp { to { transform: rotate(-12deg) scale(1); opacity: .9; } }
.hudm .rnwin .list { flex: 1; padding: 14px 18px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .rnwin .cap { display: flex; gap: 6px; align-items: center; font-size: 12px; color: var(--dim); } .hudm .rnwin .cap i { width: 46px; height: 8px; border-radius: 4px; background: #05070c; border: 1px solid var(--fr); } .hudm .rnwin .cap i.on { background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .rnwin .card { display: grid; grid-template-columns: 56px 1fr 220px 150px; gap: 14px; align-items: center; padding: 10px 12px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rnwin .card .eq { width: 52px; height: 52px; } .hudm .rnwin .card b { font-size: 14px; } .hudm .rnwin .card small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rnwin .tl { font-size: 11px; color: var(--dim); } .hudm .rnwin .tl .bar { height: 6px; margin-top: 4px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .rnwin .tl .bar i { display: block; height: 100%; background: var(--ac); }
.hudm .rnwin .tl .bar.low i { background: #ff9a3c; } .hudm .rnwin .earn { text-align: right; } .hudm .rnwin .earn b { color: #f2c94c; font-size: 15px; }
.hudm .rnwin .card.empty { grid-template-columns: 1fr; text-align: center; border-style: dashed; color: var(--dim); font-size: 12px; padding: 18px; }
.hudm .rnwin .card.ret { opacity: .75; } .hudm .rnwin .st { font-size: 10px; font-weight: 800; letter-spacing: .08em; padding: 1px 7px; border-radius: 7px; margin-left: 6px; }
.hudm .rnwin .st.a { color: var(--ac); border: 1px solid var(--ac); } .hudm .rnwin .st.r { color: #ff9a3c; border: 1px solid #ff9a3c; }
.hudm .rnwin .big { display: flex; gap: 18px; align-items: center; padding: 16px; border-radius: var(--rad); border: 1px solid var(--ac); background: radial-gradient(ellipse at 20% 50%, color-mix(in srgb, var(--ac) 18%, transparent), transparent 60%); }
.hudm .rnwin .big .eq { width: 80px; height: 80px; } .hudm .rnwin .big h3 { margin: 0; font-size: 20px; } .hudm .rnwin .big small { font-size: 12px; color: var(--dim); }
.hudm .rnwin .count { margin-left: auto; text-align: center; } .hudm .rnwin .count b { display: block; font-size: 28px; color: #fff; font-variant-numeric: tabular-nums; } .hudm .rnwin .count small { font-size: 10px; letter-spacing: .12em; }
.hudm .rnwin .chips { display: flex; flex-wrap: wrap; gap: 5px; } .hudm .rnwin .chips span { font-size: 11px; padding: 2px 9px; border-radius: 9px; color: #ffb3a6; background: rgba(255,90,80,.1); border: 1px solid rgba(255,90,80,.35); }
.hudm .rnwin .warnbox { padding: 10px 12px; border-radius: var(--rad); font-size: 12px; line-height: 1.45; border: 1px solid #c45; background: rgba(200,60,80,.1); }
`;

export class RentMock extends HudWindow {
  private items = new Map<string, Item>();
  private tab: "Desk" | "Out" | "In" = "Desk";
  private role: Role = "Owner";
  private bag = ["DragonSlayer", "HeavenSword", "TitanArmour(M)", "GoldDragonRing", "SpiritHelmet", "BengalTiger", "TarragonNecklace", "SealedHero", "SoulRing", "HelmetOfHero"];
  private offer: string | null = null;
  private days = 7;
  private fee = 0;
  private lockItem = false; private lockFee = false; private confA = false; private confB = false;
  private partner = "Seren";
  private msg: [string, string] = ["", ""];
  private out: Out[] = [{ name: "GoldDragonRing", renter: "Kaelith", fee: 60000, until: Date.now() + 3.4 * DAY, status: "active", relock: 0 }];
  private renting: { name: string; owner: string; until: number } | null = { name: "BlackTigerArmour(M)", owner: "Garrick", until: Date.now() + 5.2 * DAY };
  private clock = 0; // fast-forward offset (ms)
  private signed: string | null = null;

  constructor(private readonly host: RentHost) {
    super("rnwin", 20, 24);
    injectCss("hudm-rent-css", CSS);
    void itemData().then((d) => { if (d) for (const i of d.items) this.items.set(norm(i.name), i); if (this.open) this.render(); });
  }
  protected override shown(): void { this.render(); }
  private now(): number { return Date.now() + this.clock; }
  private item(n: string): Item | undefined { return findIn(this.items, n); }
  private canRent(n: string): string {
    const it = this.item(n);
    if (!it) return "Unknown item";
    if (it.bind.includes("UnableToRent")) return "This item can't be rented out";
    if (this.out.some((o) => o.name === n && o.status === "returned" && o.relock > this.now())) return "Returned recently: locked from renting for a day";
    if (this.out.some((o) => o.name === n && o.status === "active")) return "Already rented out";
    return "";
  }

  private render(): void {
    const active = this.out.filter((o) => o.status === "active").length;
    const tabs = h("div", "tabs");
    for (const [t, l, b] of [["Desk", "Rental desk", ""], ["Out", "Rented out", `${active}/${MAX_OUT}`], ["In", "Renting", this.renting ? "1" : ""]] as const) {
      const e = h("div", "t" + (this.tab === t ? " on" : ""), l, ...(b ? [h("b", "", b)] : [])); e.addEventListener("click", () => { this.tab = t; this.render(); }); tabs.append(e);
    }
    const ff = h("span", "ff", "⏩ +1 day (mock)"); ff.title = "Fast-forward time to see rentals end";
    ff.addEventListener("click", () => { this.clock += DAY; this.tick(); this.render(); });
    tabs.append(h("span", "sp"), h("span", "gold", "Gold ", h("b", "", fmt(this.host.gold()))), ff);
    const body = this.tab === "Desk" ? this.desk() : this.tab === "Out" ? this.outTab() : this.inTab();
    const panel = h("div", "panel", this.titleBar("ITEM RENTAL", "Close"), tabs, body);
    if (this.signed) panel.append(this.contract());
    this.el.replaceChildren(panel);
  }

  /** Rentals that reached their return date come back by mail (Envir.ReturnRentalItem) and stay locked a day. */
  private tick(): void {
    const t = this.now();
    for (const o of this.out) if (o.status === "active" && o.until <= t) { o.status = "returned"; o.relock = o.until + DAY; this.bag.push(o.name); this.host.say(`${o.name} came back from ${o.renter} by mail.`); }
    if (this.renting && this.renting.until <= t) { this.host.say(`${this.renting.name} went back to ${this.renting.owner}.`); this.renting = null; }
  }

  // — Desk —
  private reset(): void { this.offer = null; this.fee = 0; this.lockItem = this.lockFee = this.confA = this.confB = false; this.msg = ["", ""]; }
  private desk(): HTMLElement {
    const owner = this.role === "Owner";
    const seg = h("div", "seg");
    for (const r of ["Owner", "Renter"] as Role[]) { const d = h("div", this.role === r ? "on" : "", r === "Owner" ? "I rent out" : "I rent"); d.addEventListener("click", () => { if (this.role === "Renter" && this.fee) this.host.gain(this.fee); /* the fee you paid in comes back */ this.role = r; this.partner = r === "Owner" ? "Seren" : "Garrick"; this.reset(); if (r === "Renter") { this.offer = "HeavenSword"; this.days = 5; this.lockItem = true; } this.render(); }); seg.append(d); }
    // An owner may have MAX_OUT items out; a renter may rent one item at a time.
    const partnerCan = owner ? this.out.filter((o) => o.status === "active").length < MAX_OUT : !this.renting;
    const who = h("div", "who", h("div", "av", this.partner.slice(0, 2).toUpperCase()), h("div", "", h("b", "", this.partner), h("small", "", h("span", "ok", "✓ facing you"), " · in range")));
    const top = h("div", "top", h("div", "lbl", "Deal with"), who, h("span", "", ""), seg);
    // Owner side
    const os = h("div", "side" + (this.confA && owner || this.confB && !owner ? " conf" : this.lockItem ? " lock" : ""));
    os.append(h("h4", "", owner ? "You lend" : `${this.partner} lends`, h("span", "", "item + period")));
    if (this.lockItem) os.append(h("span", "tag l", "LOCKED"));
    const it = this.offer ? this.item(this.offer) : undefined;
    const nm = h("b", "", this.offer ?? "Pick an item"); if (it) nm.style.color = GRADE[it.grade] ?? "";
    os.append(h("div", "item", itemCell(this.el, this.offer ?? "", it?.img ?? -1), h("div", "", nm, h("small", "", it ? `${it.grade} ${it.type}` : owner ? "from your bag below" : ""))));
    if (owner && !this.lockItem) {
      const bag = h("div", "bag");
      for (const n of this.bag) {
        const why = this.canRent(n), i = this.item(n);
        const c = itemCell(this.el, why ? `${n}: ${why}` : n, i?.img ?? -1); if (why) c.classList.add("no");
        c.addEventListener("click", () => { if (!why) { this.offer = n; this.render(); } else { this.msg = [why, "err"]; this.render(); } });
        bag.append(c);
      }
      os.append(bag);
    }
    const range = h("input"); range.type = "range"; range.min = String(MIN_DAYS); range.max = String(MAX_DAYS); range.value = String(this.days); range.disabled = this.lockItem || !owner;
    range.addEventListener("input", () => { this.days = +range.value; const d = this.el.querySelector(".days b"); if (d) d.textContent = `${this.days} day${this.days > 1 ? "s" : ""}`; const r = this.el.querySelector(".days span"); if (r) r.textContent = `back ${date(this.now() + this.days * DAY)}`; this.calendar(); });
    range.addEventListener("change", () => this.render());
    const cal = h("div", "cal"); for (let i = 0; i < MAX_DAYS; i++) cal.append(h("i", i < this.days ? "on" : ""));
    os.append(h("div", "days", h("b", "", `${this.days} day${this.days > 1 ? "s" : ""}`), h("span", "", `back ${date(this.now() + this.days * DAY)}`)), range, cal);
    const lockI = h("div", "btn lk" + (this.offer && !this.lockItem && owner ? "" : " dis"), this.lockItem ? "Item locked" : "Lock item");
    lockI.addEventListener("click", () => { this.lockItem = true; this.partnerAct(); this.render(); });
    if (owner) os.append(h("div", "rnrow", lockI));
    // Renter side
    const rs = h("div", "side" + (this.confB && owner || this.confA && !owner ? " conf" : this.lockFee ? " lock" : ""));
    rs.append(h("h4", "", owner ? `${this.partner} pays` : "You pay", h("span", "", "rental fee")));
    if (this.lockFee) rs.append(h("span", "tag l", "LOCKED"));
    rs.append(h("div", "fee", fmt(this.fee), h("small", "", " gold"), h("br"), h("small", "", this.fee ? `${fmt(this.fee / this.days)} a day` : owner ? "waiting for an offer…" : "add gold below")));
    if (!owner) {
      const qb = h("div", "qb");
      for (const a of [1000, 5000, 10000, 50000]) { const d = h("div", "", `+${fmt(a)}`); d.addEventListener("click", () => { if (this.lockFee) return; if (!this.host.spend(a)) { this.msg = ["Not enough gold.", "err"]; } else this.fee += a; this.render(); }); qb.append(d); }
      const lockF = h("div", "btn lk" + (this.fee && !this.lockFee ? "" : " dis"), this.lockFee ? "Fee locked" : "Lock fee");
      lockF.addEventListener("click", () => { this.lockFee = true; this.partnerAct(); this.render(); });
      rs.append(qb, h("div", "msg", "The fee leaves your gold as you add it (SetItemRentalFee) and is refunded if the deal is cancelled."), h("div", "rnrow", lockF));
    }
    // Confirm
    const both = this.lockItem && this.lockFee;
    const meConf = owner ? this.confA : this.confB, themConf = owner ? this.confB : this.confA;
    const conf = h("div", "btn cf" + (both && !meConf && partnerCan ? "" : " dis"), meConf ? (themConf ? "Done" : `Waiting for ${this.partner}…`) : "Confirm rental");
    conf.addEventListener("click", () => { if (!both || meConf || !partnerCan) return; if (owner) this.confA = true; else this.confB = true; this.partnerAct(); this.render(); });
    const cancel = h("div", "btn", "Cancel"); cancel.addEventListener("click", () => { if (!owner && this.fee) this.host.gain(this.fee); this.reset(); if (!owner) { this.offer = "HeavenSword"; this.days = 5; this.lockItem = true; } this.msg = ["Rental cancelled.", ""]; this.render(); });
    const sides = h("div", "sides", os, h("div", "mid", h("i", "", "⇄"), h("span", "", "")), rs);
    const notes: string[] = [];
    if (owner && this.out.filter((o) => o.status === "active").length >= MAX_OUT) notes.push(`You already have ${MAX_OUT} items rented out.`);
    if (!owner && this.renting) notes.push(`You are already renting ${this.renting.name}. One rented item at a time.`);
    const desk = h("div", "desk", top, sides, h("div", "rnrow", conf, cancel), h("div", "msg" + (this.msg[1] ? " " + this.msg[1] : ""), notes[0] ?? this.msg[0]));
    return h("div", "body", desk, this.rules());
  }
  private calendar(): void { this.el.querySelectorAll(".cal i").forEach((e, i) => e.classList.toggle("on", i < this.days)); }

  /** The simulated partner: offers a fee once the item is locked, locks and confirms after you. */
  private partnerAct(): void {
    const owner = this.role === "Owner";
    window.setTimeout(() => {
      if (owner) {
        if (this.lockItem && !this.fee) { const v = this.offer ? this.value(this.offer) : 10000; this.fee = Math.round(v * this.days / 100) * 100; this.msg = [`${this.partner} offers ${fmt(this.fee)} gold for ${this.days} days.`, ""]; }
        if (this.lockItem && this.fee && !this.lockFee) { this.lockFee = true; }
        if (this.confA && !this.confB) { this.confB = true; this.finish(); return; }
      } else {
        if (this.confB && !this.confA) { this.confA = true; this.finish(); return; }
      }
      if (this.open) this.render();
    }, 900);
  }
  private value(n: string): number { const it = this.item(n); return Math.max(1500, Math.round((it?.price ?? 5000) * ({ Rare: 0.4, Legendary: 1, Mythical: 1.6 }[it?.grade ?? ""] ?? 0.15) / 10)); }
  private finish(): void {
    const n = this.offer!, until = this.now() + this.days * DAY;
    if (this.role === "Owner") {
      this.bag.splice(this.bag.indexOf(n), 1);
      this.out.push({ name: n, renter: this.partner, fee: this.fee, until, status: "active", relock: 0 });
      this.host.gain(this.fee);
      this.host.say(`${this.partner} rented ${n} until ${date(until)}. You received ${fmt(this.fee)} gold.`);
    } else {
      this.renting = { name: n, owner: this.partner, until };
      this.host.say(`You have rented ${n} from ${this.partner} until ${date(until)}.`);
    }
    this.signed = `${n}|${this.role === "Owner" ? "GMRyu" : this.partner}|${this.role === "Owner" ? this.partner : "GMRyu"}|${this.days}|${this.fee}|${until}`;
    this.reset();
    if (this.role === "Renter") { this.offer = null; this.lockItem = false; }
    this.render();
    window.setTimeout(() => { this.signed = null; this.tab = this.role === "Owner" ? "Out" : "In"; if (this.open) this.render(); }, 2600);
  }
  private contract(): HTMLElement {
    const [n, owner, renter, days, fee, until] = this.signed!.split("|");
    return h("div", "contract", h("div", "paper", h("h3", "", "RENTAL CONTRACT"), h("p", "", `${owner} lends `, h("b", "", n!), ` to ${renter}`),
      h("p", "", `for ${days} days, until ${date(+until!)},`), h("p", "", `for ${fmt(+fee!)} gold paid in full.`), h("p", "", "The item returns to its owner at the end, or if the renter dies."), h("div", "stamp", "SEALED")));
  }
  private rules(): HTMLElement {
    return h("div", "rules", h("div", "lbl", "Rental rules"),
      h("div", "", h("b", "", "Face to face. "), "Stand next to the other player, facing them."),
      h("div", "", h("b", "", `${MAX_OUT} out at most. `), "An owner can rent out three items at once."),
      h("div", "", h("b", "", "One at a time. "), "A player can rent only one item."),
      h("div", "", h("b", "", `${MIN_DAYS}–${MAX_DAYS} days. `), "The owner sets the period and locks the item; the renter pays and locks the fee; both confirm."),
      h("div", "", h("b", "", "Bound while rented: "), BOUND.join(", ").toLowerCase(), "."),
      h("div", "", h("b", "", "Coming back. "), "At the end, or if the renter dies, the item is mailed back and can't be rented again for a day."));
  }

  // — Rented out —
  private outTab(): HTMLElement {
    const t = this.now(), active = this.out.filter((o) => o.status === "active").length;
    const cap = h("div", "cap", "Rented out "); for (let i = 0; i < MAX_OUT; i++) cap.append(h("i", i < active ? "on" : "")); cap.append(`${active}/${MAX_OUT}`);
    const list = h("div", "list", cap);
    for (const o of this.out) {
      const it = this.item(o.name), nm = h("b", "", o.name); nm.style.color = GRADE[it?.grade ?? "Common"] ?? "";
      const span = o.until - (o.until - 30 * DAY), frac = Math.max(0, Math.min(1, (o.until - t) / Math.max(DAY, o.until - (o.until - 7 * DAY))));
      const bar = h("div", "bar" + (o.until - t < DAY ? " low" : ""), h("i")); (bar.firstChild as HTMLElement).style.width = frac * 100 + "%"; void span;
      const tl = o.status === "active" ? h("div", "tl", `Back in ${left(o.until - t)} · ${date(o.until)}`, bar)
        : h("div", "tl", `Returned by mail ${date(o.until)}`, h("br"), o.relock > t ? `Can rent again in ${left(o.relock - t)}` : "Can be rented again");
      list.append(h("div", "card" + (o.status === "returned" ? " ret" : ""), itemCell(this.el, o.name, it?.img ?? -1), h("div", "", nm, h("span", "st " + (o.status === "active" ? "a" : "r"), o.status === "active" ? "RENTED" : "RETURNED"), h("small", "", `to ${o.renter}`)),
        tl, h("div", "earn", h("b", "", `+${fmt(o.fee)}`), h("small", "", "gold earned"))));
    }
    for (let i = active; i < MAX_OUT; i++) { const e = h("div", "card empty", "Free rental slot · use the rental desk to lend an item"); e.addEventListener("click", () => { this.tab = "Desk"; this.role = "Owner"; this.render(); }); list.append(e); }
    return h("div", "body", list, this.rules());
  }

  // — Renting —
  private inTab(): HTMLElement {
    const list = h("div", "list");
    const r = this.renting;
    if (!r) { list.append(h("div", "card empty", "You are not renting anything. A player can rent one item at a time.")); return h("div", "body", list, this.rules()); }
    const it = this.item(r.name), nm = h("h3", "", r.name); nm.style.color = GRADE[it?.grade ?? "Common"] ?? "";
    list.append(h("div", "big", itemCell(this.el, r.name, it?.img ?? -1), h("div", "", nm, h("small", "", `Rented from ${r.owner} · back ${date(r.until)}`)),
      h("div", "count", h("b", "", left(r.until - this.now())), h("small", "", "LEFT"))));
    list.append(h("div", "lbl", "While you rent it"), (() => { const c = h("div", "chips"); for (const b of BOUND) c.append(h("span", "", b)); return c; })(),
      h("div", "warnbox", `It goes back to ${r.owner} on ${date(r.until)}, or straight away if you die. Wear it, fight with it, but you can't keep it.`));
    return h("div", "body", list, this.rules());
  }
}
