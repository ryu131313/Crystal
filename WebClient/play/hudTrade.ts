// Trade window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// TradeDialog / GuestTradeDialog, keeping Crystal's rules (PlayerObject trade): 10 item slots and gold per side; Confirm
// locks your side; any change (yours or theirs) unlocks both and the confirmations start again; the trade goes through
// only when both sides have confirmed, and only if each side has room and can carry what it gets. Around that: an
// incoming request toast (Accept / Decline), the partner's card and a three-step progress strip, both offers side by
// side with estimated worth and a fairness meter, your bag to click items in from (the shared bag, hudItems.ts; drag and
// drop or right-click works too, hudDrag.ts; DontTrade items cannot be traded; an offered item leaves the bag, like the
// server's trade slots, and comes back when the trade is cancelled), quick gold buttons, the bag space / weight after
// the trade, a trade chat with a log of every change, a simulated partner (adds items, answers, confirms after you), and
// a summary when it completes (their items land in your bag). Worth = ItemInfo.Price x count. Mock data; item Image
// numbers are from a copy of the live Server.MirDB (2026-10-15).

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { BAG_SLOTS, WEIGHT_MAX, addToBag, bag, bagChanged, bagWeight, hasBind, info, onBag, weightOf, type Stack } from "./hudItems.ts";
import { bestCell, bindCell, host, registerHost, type DragHost } from "./hudDrag.ts";

/** name, Items.Lib image, count, worth (gold, mock), weight, bound */
type Item = { name: string; img: number; count: number; worth: number; weight: number; bound?: boolean };
const SLOTS = 10;
const MY_GOLD = 1_240_000;
const it = (name: string, img: number, count: number, worth: number, weight: number, bound = false): Item => ({ name, img, count, worth, weight, bound });
/** A bag stack as a trade item: worth = ItemInfo.Price x count, weight = UserItem.Weight, bound = DontTrade. */
const offerOf = (s: Stack): Item => ({ name: s.name, img: s.img, count: s.count, worth: (info(s.name)?.price ?? 0) * s.count, weight: weightOf(s), bound: hasBind(s.name, "DontTrade") });
const PARTNER = { name: "Seren", cls: "Archer", level: 31, guild: "GameMaster" };
/** What the partner puts up: at the start, after your first item, and after their reply. */
const THEIR_START: Item[] = [it("SunPotion(M)", 312, 20, 120000, 20), it("WhiteGoldBracelet", 766, 1, 95000, 1)];
const THEIR_LATER: Item = it("WhiteGoldRing", 773, 1, 80000, 1);
const THEIR_GOLD = 150_000;
const REPLIES = ["Sounds fair to me!", "Can you add a bit more gold?", "Ooh, nice ring.", "Deal if you throw in some pots.", "Ok, confirming when you are."];

type Step = "offer" | "confirm" | "done";
const fmt = (n: number) => n.toLocaleString("en-GB");
const worth = (items: (Item | null)[], gold: number) => items.reduce((s, x) => s + (x?.worth ?? 0), 0) + gold;

const CSS = `
.hudm .twin { --winh: 630px; width: 960px; }
.hudm .twin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .twin .top { display: flex; align-items: center; gap: 14px; padding: 12px 16px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 140% at 0% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .twin .av { width: 42px; height: 42px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .twin .who b { display: block; font-size: 16px; color: #fff; }
.hudm .twin .who span { font-size: 11px; color: var(--dim); }
.hudm .twin .steps { margin-left: auto; display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .twin .steps .st { display: flex; align-items: center; gap: 6px; }
.hudm .twin .steps .st i { width: 20px; height: 20px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-style: normal; font-size: 10px; font-weight: 700;
  border: 1px solid var(--fr); color: var(--dim); }
.hudm .twin .steps .st.on { color: #fff; } .hudm .twin .steps .st.on i { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 40%, transparent); box-shadow: 0 0 8px var(--glow); }
.hudm .twin .steps .st.ok i { border-color: var(--ac); color: var(--ac); }
.hudm .twin .steps .ln { width: 24px; height: 1px; background: color-mix(in srgb, var(--fr) 80%, transparent); }
.hudm .twin .tmain { flex: 1; min-height: 0; display: flex; gap: 0; }
.hudm .twin .offers { flex: 1; min-width: 0; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; padding: 12px 14px; gap: 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .twin .pair { display: grid; grid-template-columns: 1fr 64px 1fr; align-items: stretch; }
.hudm .twin .side { position: relative; border-radius: var(--rad); padding: 10px 12px; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  transition: border-color .2s, box-shadow .2s; }
.hudm .twin .side.locked { border-color: var(--ac); box-shadow: 0 0 0 1px var(--ac), 0 0 calc(14px * var(--gl)) var(--glow), inset 0 0 20px color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .twin .side.locked .grid { opacity: .85; }
.hudm .twin .sh { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
.hudm .twin .sh b { font-size: 12px; color: #fff; }
.hudm .twin .sh .pill { margin-left: auto; font-size: 9px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; padding: 2px 7px; border-radius: 8px; color: var(--dim);
  border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .twin .side.locked .sh .pill { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .twin .grid { display: grid; grid-template-columns: repeat(5, 44px); gap: 4px; justify-content: center; }
.hudm .twin .grid .eq.new { animation: hudm-tnew 1.2s ease-out; }
@keyframes hudm-tnew { 0% { box-shadow: 0 0 0 1px #000, 0 0 16px var(--ac); border-color: #fff; } }
.hudm .twin .gold { display: flex; align-items: center; gap: 6px; margin-top: 8px; }
.hudm .twin .gold .coin { width: 16px; height: 16px; border-radius: 50%; flex: none; background: radial-gradient(circle at 35% 35%, #ffe9a0, #c9921f); box-shadow: 0 0 6px rgba(242, 201, 76, .5); }
.hudm .twin .gold input { flex: 1; min-width: 0; height: 26px; box-sizing: border-box; padding: 0 8px; font: 600 12px "Segoe UI", system-ui, sans-serif; color: #f2c94c; text-align: right;
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; font-variant-numeric: tabular-nums; }
.hudm .twin .gold input:focus { border-color: var(--ac); }
.hudm .twin .gold input:disabled { opacity: .7; }
.hudm .twin .gold .val { flex: 1; text-align: right; font-size: 13px; font-weight: 700; color: #f2c94c; font-variant-numeric: tabular-nums; }
.hudm .twin .gold .btn { height: 22px; padding: 0 6px; font-size: 10px; }
.hudm .twin .est { margin-top: 6px; display: flex; justify-content: space-between; font-size: 11px; color: var(--dim); }
.hudm .twin .est b { color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .twin .mid { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; }
.hudm .twin .swap { width: 38px; height: 38px; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: var(--ac); border: 1px solid var(--ac);
  background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .twin .swap svg { width: 20px; height: 20px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .twin .fair { width: 100%; }
.hudm .twin .fair .meter { position: relative; height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--fr) 60%, #000); overflow: hidden; }
.hudm .twin .fair .meter i { position: absolute; top: 0; bottom: 0; left: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .twin .fair .meter::after { content: ""; position: absolute; left: 50%; top: -2px; bottom: -2px; width: 1px; background: #fff; }
.hudm .twin .fair span { display: block; margin-top: 4px; text-align: center; font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--dim); }
.hudm .twin .bag { flex: none; border-radius: var(--rad); padding: 10px 12px; background: color-mix(in srgb, var(--frl) 4%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .twin .bag .grid { grid-template-columns: repeat(10, 44px); justify-content: start; }
.hudm .twin .bag .eq.bound::before { content: ""; position: absolute; inset: 0; z-index: 3; background: repeating-linear-gradient(135deg, transparent 0 6px, rgba(255, 80, 80, .18) 6px 8px); }
.hudm .twin .lbl { display: flex; flex-wrap: wrap; gap: 2px 12px; justify-content: space-between; margin-bottom: 7px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .twin .lbl b { letter-spacing: 0; font-weight: 600; color: var(--tx); text-transform: none; }
.hudm .twin .lbl b.bad { color: #ff7a6a; }
/* Chat / log */
.hudm .twin .chat { width: 250px; flex: none; display: flex; flex-direction: column; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); padding: 12px; gap: 8px; }
.hudm .twin .log { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 5px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .twin .log .sys { font-size: 11px; color: var(--dim); text-align: center; }
.hudm .twin .log .sys b { color: var(--acd); font-weight: 600; }
.hudm .twin .log .sys.warn { color: #ffb08a; }
.hudm .twin .log .msg { max-width: 86%; padding: 4px 9px; border-radius: 10px; font-size: 12px; align-self: flex-start; background: color-mix(in srgb, var(--frl) 12%, transparent); }
.hudm .twin .log .msg.me { align-self: flex-end; color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .twin .log .typing { font-size: 11px; color: var(--dim); font-style: italic; }
.hudm .twin .chat input { width: 100%; box-sizing: border-box; height: 26px; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .twin .chat input:focus { border-color: var(--ac); }
/* Footer */
.hudm .twin .foot { flex: none; display: flex; align-items: center; gap: 8px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .twin .foot .note { font-size: 11px; color: var(--dim); }
.hudm .twin .foot .note b { color: var(--ac); }
.hudm .twin .foot .sp { flex: 1; }
.hudm .twin .foot .btn { height: 30px; padding: 0 16px; }
.hudm .twin .foot .btn.main { min-width: 170px; justify-content: center; color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .twin .foot .btn.dis { opacity: .45; pointer-events: none; }
.hudm .twin .foot .btn.warn { border-color: #b33; color: #fbb; }
/* Summary */
.hudm .twin .done { position: absolute; inset: 31px 0 0; z-index: 5; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.6); border-radius: 0 0 var(--rad) var(--rad); }
.hudm .twin .done .card { width: 460px; padding: 18px 20px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac); box-shadow: 0 0 30px var(--glow); text-align: center; }
.hudm .twin .done h3 { margin: 0 0 4px; font-size: 18px; color: #fff; }
.hudm .twin .done .sub { font-size: 11px; color: var(--dim); margin-bottom: 12px; }
.hudm .twin .done .cols { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; text-align: left; }
.hudm .twin .done .cols div { font-size: 11px; color: var(--dim); }
.hudm .twin .done .cols b { display: block; margin-bottom: 4px; font-size: 10px; letter-spacing: .12em; text-transform: uppercase; color: var(--acd); }
.hudm .twin .done .row { display: flex; gap: 10px; justify-content: center; margin-top: 16px; }
/* Request toast */
.hudm .treq { position: absolute; left: 50%; top: 90px; transform: translateX(-50%); display: flex; align-items: center; gap: 12px; padding: 10px 14px; pointer-events: auto;
  border-radius: var(--rad); background: var(--page, var(--pb)); border: 1px solid var(--ac); box-shadow: 0 0 0 1px #000, 0 0 20px var(--glow); color: var(--tx); font-size: 12px;
  background: linear-gradient(180deg, var(--pa), var(--pb)); animation: hudm-treq .3s ease-out; }
@keyframes hudm-treq { from { opacity: 0; transform: translate(-50%, -10px); } }
.hudm .treq b { color: #fff; }
.hudm .treq .btn { height: 26px; padding: 0 12px; }
.hudm .treq .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); }
.hudm .treq .bar { position: absolute; left: 0; bottom: 0; height: 2px; background: var(--ac); animation: hudm-treqbar 15s linear forwards; }
@keyframes hudm-treqbar { from { width: 100%; } to { width: 0; } }
`;

export class TradeMock extends HudWindow {
  readonly request: HTMLElement;
  /** Your 10 trade slots (null = empty); what's here has left the bag. */
  private mine: (Item | null)[] = Array<Item | null>(SLOTS).fill(null);
  private theirs: Item[] = [];
  private myGold = 0;
  private theirGold = 0;
  private myLock = false;
  private theirLock = false;
  private completed = false;
  private log: (["sys" | "warn" | "me" | "them", string])[] = [];
  private typing = false;
  private freshTheirs = -1;
  private laterAdded = false;
  private timers: number[] = [];
  private body!: HTMLElement;
  private chatLog!: HTMLElement;
  private foot!: HTMLElement;
  private top!: HTMLElement;

  constructor() {
    super("twin", 62, 40);
    injectCss("hudm-trade-css", CSS);
    this.request = h("div", "treq");
    this.request.hidden = true;
    this.build();
    const me: DragHost = registerHost({
      id: "trade", size: SLOTS, live: () => this.open && !this.completed,
      get: (i) => { const x = this.mine[i]; return x ? { name: x.name, img: x.img, count: x.count } : null; },
      set: (i, st) => { this.mine[i] = st ? offerOf(st) : null; },
      accepts: (_i, st) => this.completed ? "The trade is over" : hasBind(st.name, "DontTrade") ? "Bound: can't be traded" : "",
      verb: () => "Offer",
      quick: (i) => { const st = me.get(i); return st ? bestCell(host("bag"), st, me) : null; },
      after: (st, from, to) => {
        const n = `${st.name}${st.count > 1 ? ` x${st.count}` : ""}`;
        if (to === me && from !== me) this.changed(`added ${n}`, "me");
        else if (from === me && to !== me) this.changed(`took back ${n}`, "me");
        this.render();
      },
    });
    onBag(() => { if (this.open) this.render(); });
  }

  private offered(): Item[] { return this.mine.filter((x): x is Item => !!x); }

  /** Puts what you offered back in the bag (a cancelled or restarted trade); what no longer fits is mailed, like the
   *  server's TradeCancel (GainItemMail). */
  private returnOffer(): void {
    let any = false;
    const mailed: string[] = [];
    for (const x of this.offered()) { const left = addToBag({ name: x.name, img: x.img, count: x.count }); if (left) mailed.push(`${x.name}${left > 1 ? ` x${left}` : ""}`); any = true; }
    this.mine.fill(null);
    if (any) bagChanged();
    if (mailed.length) this.notify(`Your bag is full: ${mailed.join(", ")} was sent to your mailbox.`);
  }

  /** A short notice in the request toast's place (the trade window may be closing). */
  private notify(text: string): void {
    const n = h("div", "", text);
    this.request.replaceChildren(n);
    this.request.hidden = false;
    window.setTimeout(() => { if (this.request.firstChild === n) this.request.hidden = true; }, 4000);
  }

  /** Offers bag cell i: moves the stack into the first free trade slot. */
  private offer(i: number): void {
    const st = bag[i];
    if (!st || this.completed) return;
    if (hasBind(st.name, "DontTrade")) { this.log.push(["warn", `${st.name} is bound and cannot be traded.`]); this.renderChat(); return; }
    const slot = this.mine.indexOf(null);
    if (slot < 0) { this.log.push(["warn", "All 10 trade slots are full."]); this.renderChat(); return; }
    this.mine[slot] = offerOf(st);
    bag[i] = null;
    this.changed(`added ${st.name}${st.count > 1 ? ` x${st.count}` : ""}`, "me");
    bagChanged();
  }

  /** The partner asks to trade: a toast with Accept / Decline (it lapses after 15 s). */
  ask(): void {
    if (this.open) return;
    const accept = h("div", "btn main", "Accept"), decline = h("div", "btn", "Decline");
    const close = () => { this.request.hidden = true; window.clearTimeout(lapse); };
    accept.addEventListener("click", () => { close(); this.start(); });
    decline.addEventListener("click", close);
    this.request.replaceChildren(h("div", "av", "ARC"), h("div", "", h("b", "", PARTNER.name), " wants to trade with you"), accept, decline, h("div", "bar"));
    this.request.hidden = false;
    const lapse = window.setTimeout(close, 15000);
  }

  /** A fresh trade with the partner's opening offer. */
  start(): void {
    for (const t of this.timers) window.clearTimeout(t);
    this.timers = [];
    if (!this.completed) this.returnOffer();
    this.mine = Array<Item | null>(SLOTS).fill(null); this.myGold = 0; this.myLock = this.theirLock = this.completed = false; this.laterAdded = false;
    this.theirs = THEIR_START.map((x) => ({ ...x })); this.theirGold = THEIR_GOLD;
    this.log = [["sys", `Trading with ${PARTNER.name}.`], ["sys", `${PARTNER.name} offered ${THEIR_START.map((x) => `${x.name}${x.count > 1 ? ` x${x.count}` : ""}`).join(", ")} and ${fmt(THEIR_GOLD)} gold.`], ["them", "Hey! Got anything good?"]];
    this.toggle(true);
  }

  protected override shown(): void { if (!this.log.length) this.start(); else this.render(); }

  private build(): void {
    this.top = h("div", "top");
    this.body = h("div", "offers");
    this.chatLog = h("div", "log");
    const input = h("input");
    input.placeholder = `Say something to ${PARTNER.name}…`;
    input.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key !== "Enter" || !input.value.trim()) return;
      this.log.push(["me", input.value.trim()]);
      input.value = "";
      this.reply();
      this.renderChat();
    });
    this.foot = h("div", "foot");
    this.el.append(h("div", "panel", this.titleBar("TRADE", "Cancel the trade"), this.top, h("div", "tmain", this.body, h("div", "chat", h("div", "lbl", "Trade chat"), this.chatLog, input)), this.foot));
  }

  /** Every way of closing the window (the cross, Esc, Shift+Esc, Close all) cancels the trade, like the desktop's. */
  override toggle(show = this.el.hidden): void {
    if (!show && this.open) this.abort();
    super.toggle(show);
  }

  private later(ms: number, fn: () => void): void { this.timers.push(window.setTimeout(() => { if (this.open && !this.completed) fn(); }, ms)); }

  private reply(): void {
    this.typing = true;
    this.later(1400, () => { this.typing = false; this.log.push(["them", REPLIES[Math.floor(Math.random() * REPLIES.length)]!]); this.renderChat(); });
  }

  /** Any change unlocks both sides (PlayerObject: TradeLocked = false on both). */
  private changed(what: string, who: "me" | "them"): void {
    if (this.myLock || this.theirLock) this.log.push(["warn", "The offer changed: both confirmations were reset."]);
    this.myLock = this.theirLock = false;
    this.log.push(["sys", who === "me" ? `You ${what}.` : `${PARTNER.name} ${what}.`]);
    // The partner answers your first item with one of their own.
    if (who === "me" && !this.laterAdded && this.offered().length) {
      this.laterAdded = true;
      this.later(2200, () => {
        this.theirs.push({ ...THEIR_LATER }); this.freshTheirs = this.theirs.length - 1;
        this.changed(`added ${THEIR_LATER.name}`, "them");
        this.log.push(["them", "Throwing in a ring too."]);
        this.render();
      });
    }
  }

  private confirm(): void {
    if (this.myLock) return;
    const room = this.roomCheck();
    if (room) { this.log.push(["warn", room]); this.render(); return; }
    this.myLock = true;
    this.log.push(["sys", `You confirmed. Waiting for ${PARTNER.name}…`]);
    this.render();
    if (!this.theirLock) this.later(1600, () => { this.theirLock = true; this.log.push(["sys", `${PARTNER.name} confirmed.`]); this.render(); this.later(700, () => this.finish()); });
    else this.later(500, () => this.finish());
  }

  private finish(): void {
    if (!this.myLock || !this.theirLock) return;
    // The bag may have changed since you confirmed: the server checks again when both have locked and, if you cannot
    // take it all, unlocks both sides (TradeCancel { Unlock = true }).
    const room = this.roomCheck();
    if (room) { this.myLock = this.theirLock = false; this.log.push(["warn", `${room} Both confirmations were reset.`]); this.render(); return; }
    this.completed = true;
    // Their items land in your bag; yours are gone.
    const mailed: string[] = [];
    for (const x of this.theirs) { const left = addToBag({ name: x.name, img: x.img, count: x.count }); if (left) mailed.push(x.name); }
    this.log.push(["sys", "Trade complete."]);
    if (mailed.length) this.log.push(["warn", `No room for ${mailed.join(", ")}: sent to your mailbox.`]);
    bagChanged();
    this.render();
  }

  private cancel(): void { this.toggle(false); }

  /** Ends the trade: your offer goes back to the bag and the partner's pending moves are dropped. */
  private abort(): void {
    if (!this.completed) this.returnOffer();
    for (const t of this.timers) window.clearTimeout(t);
    this.timers = [];
    this.typing = false;
    this.log = [];
  }

  /** Bag space and weight after the trade (the server refuses when either side cannot take it all). */
  private after(): { bag: number; weight: number } {
    // What you offered has already left the bag.
    const used = bag.filter(Boolean).length + this.theirs.length;
    const weight = bagWeight() + this.theirs.reduce((s, x) => s + x.weight, 0);
    return { bag: used, weight };
  }
  private roomCheck(): string {
    const a = this.after();
    if (a.bag > BAG_SLOTS) return "Not enough bag space for everything you would get.";
    if (a.weight > WEIGHT_MAX) return "You could not carry everything you would get.";
    if (this.myGold > MY_GOLD) return "You do not have that much gold.";
    return "";
  }

  private render(): void {
    this.renderTop();
    this.renderBody();
    this.renderChat();
    this.renderFoot();
  }

  private renderTop(): void {
    const step: Step = this.completed ? "done" : this.myLock || this.theirLock ? "confirm" : "offer";
    const st = (n: number, label: string, s: Step) => {
      const order: Step[] = ["offer", "confirm", "done"];
      const cls = s === step ? "on" : order.indexOf(s) < order.indexOf(step) ? "ok" : "";
      return h("div", "st " + cls, h("i", "", cls === "ok" ? "✓" : String(n)), label);
    };
    this.top.replaceChildren(h("div", "av", "ARC"), h("div", "who", h("b", "", PARTNER.name), h("span", "", `${PARTNER.cls} ${PARTNER.level} · ${PARTNER.guild} · friend`)),
      h("div", "steps", st(1, "Make offers", "offer"), h("div", "ln"), st(2, "Both confirm", "confirm"), h("div", "ln"), st(3, "Done", "done")));
  }

  private side(title: string, items: (Item | null)[], goldAmt: number, locked: boolean, mineSide: boolean): HTMLElement {
    const grid = h("div", "grid");
    for (let i = 0; i < SLOTS; i++) {
      const x = items[i];
      const cell = x ? itemCell(this.el, `${x.name}${x.count > 1 ? ` (${x.count})` : ""} · worth ~${fmt(x.worth)}${mineSide && !locked ? "\nClick to take it back" : ""}`, x.img, x.count) : itemCell(this.el, "", -1);
      if (x && mineSide && !this.completed) cell.addEventListener("click", () => {
        const left = addToBag({ name: x.name, img: x.img, count: x.count });
        if (left === x.count) { this.log.push(["warn", "Your bag is full."]); this.renderChat(); return; }
        // What did not fit stays offered.
        this.mine[i] = left ? { ...x, count: left, worth: Math.round(x.worth / x.count * left), weight: Math.round(x.weight / x.count * left) } : null;
        this.changed(`took back ${x.name}`, "me");
        bagChanged();
      });
      if (mineSide) bindCell(cell, "trade", i);
      if (x && !mineSide && i === this.freshTheirs) { cell.classList.add("new"); this.freshTheirs = -1; }
      grid.append(cell);
    }
    const gold = h("div", "gold", h("span", "coin"));
    if (mineSide) {
      const input = h("input");
      input.value = this.myGold ? fmt(this.myGold) : "";
      input.placeholder = "0";
      input.disabled = this.completed;
      input.addEventListener("keydown", (e) => e.stopPropagation());
      input.addEventListener("change", () => { this.setGold(Number(input.value.replace(/[^0-9]/g, "")) || 0); });
      gold.append(input);
      for (const [label, n] of [["+10k", 10_000], ["+100k", 100_000], ["+1M", 1_000_000]] as [string, number][]) {
        const b = h("div", "btn", label);
        b.addEventListener("click", () => this.setGold(this.myGold + n));
        gold.append(b);
      }
    } else gold.append(h("span", "val", `${fmt(goldAmt)} gold`));
    return h("div", "side" + (locked ? " locked" : ""), h("div", "sh", h("b", "", title), h("span", "pill", locked ? "✓ Confirmed" : "Not confirmed")), grid, gold,
      h("div", "est", h("span", "", `${items.filter(Boolean).length} / ${SLOTS} slots`), h("span", "", "Est. worth ", h("b", "", fmt(worth(items, goldAmt))))));
  }

  private setGold(n: number): void {
    const v = Math.max(0, Math.min(MY_GOLD, Math.floor(n)));
    if (v === this.myGold) { this.render(); return; }
    this.myGold = v;
    this.changed(`put up ${fmt(v)} gold`, "me");
    this.render();
  }

  private renderBody(): void {
    const mineW = worth(this.mine, this.myGold), theirW = worth(this.theirs, this.theirGold);
    const total = mineW + theirW;
    const share = total ? theirW / total : 0.5;
    const meter = h("div", "meter", h("i"));
    (meter.firstChild as HTMLElement).style.width = (share * 100).toFixed(0) + "%";
    const verdict = !total ? "Empty" : Math.abs(share - 0.5) < 0.08 ? "Fair" : share > 0.5 ? "In your favour" : `In ${PARTNER.name}'s favour`;
    const swap = h("div", "swap");
    swap.innerHTML = '<svg viewBox="0 0 24 24"><path d="M4 8h14M14 4l4 4-4 4M20 16H6M10 12l-4 4 4 4"/></svg>';
    const pair = h("div", "pair", this.side("Your offer", this.mine, this.myGold, this.myLock, true), h("div", "mid", swap, h("div", "fair", meter, h("span", "", verdict))),
      this.side(`${PARTNER.name}'s offer`, this.theirs, this.theirGold, this.theirLock, false));
    // Your bag (the shared one): click, drag or right-click to offer (bound items cannot be traded).
    const bagGrid = h("div", "grid");
    bag.forEach((x, i) => {
      const bound = !!x && hasBind(x.name, "DontTrade");
      const cell = x ? itemCell(this.el, `${x.name}${x.count > 1 ? ` (${x.count})` : ""}${bound ? " · bound, cannot be traded" : " · click to offer"}`, x.img, x.count) : itemCell(this.el, "", -1);
      if (bound) cell.classList.add("bound");
      if (x) cell.addEventListener("click", () => this.offer(i));
      bindCell(cell, "bag", i);
      bagGrid.append(cell);
    });
    const a = this.after();
    const bagBox = h("div", "bag", h("div", "lbl", h("span", "", "Your bag · click, drag or right-click an item to offer it"),
      h("span", "", "After the trade: ", h("b", a.bag > BAG_SLOTS ? "bad" : "", `${a.bag} / ${BAG_SLOTS} slots`), " · ", h("b", a.weight > WEIGHT_MAX ? "bad" : "", `${a.weight} / ${WEIGHT_MAX} weight`))), bagGrid);
    this.body.replaceChildren(pair, bagBox);
    if (this.completed) {
      const list = (all: (Item | null)[], gold: number) => { const items = all.filter((x): x is Item => !!x); return h("div", "", ...items.map((x) => h("div", "", `${x.name}${x.count > 1 ? ` x${x.count}` : ""}`)), gold ? h("div", "", `${fmt(gold)} gold`) : "", !items.length && !gold ? "Nothing" : ""); };
      const again = h("div", "btn main", "Trade again"); again.addEventListener("click", () => this.start());
      const close = h("div", "btn", "Close"); close.addEventListener("click", () => this.cancel());
      const old = this.el.querySelector(".done"); old?.remove();
      this.el.querySelector(".panel")!.append(h("div", "done", h("div", "card", h("h3", "", "Trade complete"), h("div", "sub", `with ${PARTNER.name}`),
        h("div", "cols", h("div", "", h("b", "", "You received"), list(this.theirs, this.theirGold)), h("div", "", h("b", "", "You gave"), list(this.mine, this.myGold))),
        h("div", "row", close, again))));
    } else this.el.querySelector(".done")?.remove();
  }

  private renderChat(): void {
    this.chatLog.replaceChildren();
    for (const [k, t] of this.log) this.chatLog.append(k === "sys" || k === "warn" ? h("div", "sys" + (k === "warn" ? " warn" : ""), t) : h("div", "msg" + (k === "me" ? " me" : ""), t));
    if (this.typing) this.chatLog.append(h("div", "typing", `${PARTNER.name} is typing…`));
    this.chatLog.scrollTop = this.chatLog.scrollHeight;
  }

  private renderFoot(): void {
    const cancel = h("div", "btn", "Cancel trade");
    cancel.addEventListener("click", () => this.cancel());
    const label = this.completed ? "Completed" : this.myLock ? (this.theirLock ? "Completing…" : `Waiting for ${PARTNER.name}…`) : "Confirm trade";
    const confirm = h("div", "btn main" + (this.myLock || this.completed ? " dis" : ""), label);
    confirm.addEventListener("click", () => this.confirm());
    const note = h("div", "note", this.myLock || this.theirLock ? "Changing anything resets both confirmations." : h("span", "", "Both sides must ", h("b", "", "confirm"), " for the trade to go through."));
    this.foot.replaceChildren(note, h("div", "sp"), cancel, confirm);
  }
}
