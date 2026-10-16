// Ported from play/hudTrade.ts: the new UI's trade window over the real models. Desktop counterpart: TradeDialog +
// GuestTradeDialog (Client/MirScenes/Dialogs) and GameScene.TradeRequest / TradeAccept / TradeGold / TradeItem /
// TradeConfirm / TradeCancel; the classic port is src/ui/tradeWindow.ts. The user's side (UserObject.Trade, TradeGoldAmount,
// TradeLocked) lives in Inventory, the partner's (GuestTradeDialog.GuestName / GuestGold / GuestItems) in the Trade model
// (src/game/trade.ts). Changes from the mock:
//  - the trade starts on the server: the incoming request (S.TradeRequest, Trade.requested) is a request toast with a
//    countdown that declines on its own (C.TradeReply false), wired at UI start by this entry's watch hook (TradeSession),
//    so it works while the window is closed; the window opens on S.TradeAccept. The Trade key (T) and the chat bar's
//    trade button send C.TradeRequest to the player you face (GameUi.command "trade" -> Trade.request);
//  - your offer is the Inventory trade grid: real item cells (grid "trade", core/itemCells.ts) and the window's own bag
//    (grid "inv"), so drag, pick and place go through core/drag.ts and the routes (C.DepositTradeItem /
//    C.RetrieveTradeItem / C.MoveItem Trade); a click on a bag item in the window offers it, a double-click on an offered
//    item takes it back. Nothing moves until the server answers (the model locks the cells). DontTrade items are refused;
//  - gold is ADD-ONLY: "Add gold" asks an amount (MirAmountBox, capped at Inventory.gold) and sends C.TradeGold, which
//    takes the gold from you at once (S.LoseGold) and cannot be lowered; cancelling the trade gives it back;
//  - Confirm = C.TradeConfirm (Inventory.toggleTradeLock; click again to undo). Any change on either side unlocks you
//    (the server unlocks both). When both are locked the server checks room / weight / gold; a refusal is
//    S.TradeCancel { Unlock = true } plus its chat line, shown here as the room-check note;
//  - the partner card: name, and class / level / guild from the partner's MapObj while in view (or the friends list);
//  - EVERY close path (the cross, Esc, Shift+Esc, Close all, log-out) cancels the trade with C.TradeCancel, like the
//    desktop's close button; S.TradeCancel closes the window (the server returns the items, mailing what no longer fits),
//    with the desktop's "must face" notice when the cancel did not come from you. S.TradeConfirm shows a summary of what
//    changed hands (from the offers as they stood when you confirmed: any later change would have unlocked you);
//  - HIDDEN (Crystal has no system for them): the partner's "Confirmed" pill (no packet; the server only posts a chat
//    line, PlayerWaitingConfirmTrade) and the trade chat thread (use whispers: the Whisper button prefills "/name ").
// Narrow layout (a layer below 1040 px): the window is 600 px wide either way; the bag grid scrolls.

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { BindMode, ObjectType, Stat } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import { BELT_IDX, TRADE_SLOTS } from "../../../game/inventory.ts";
import type { MapObj } from "../../../game/objects.ts";
import { itemWeight } from "../../../game/canWear.ts";
import {
  UiWindow, button, fmt, h, injectCss, setStyle, setText, svg, type Btn, type PopupPromise,
} from "../core/index.ts";
import { className, classTag } from "../core/format.ts";
import { classVar } from "../core/styles.ts";
import { routeDrop, type CellRef, type Verdict } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

/** The window's registry key (no menu entry and no hotkey: the server opens it; T sends a request instead). */
export const TRADE_WINDOW = "trade";
/** The partner's offer: read-only cells on a custom grid (index = GuestItems slot). */
const GUEST = "x:tradeGuest" as const;
/** How long the request toast waits before it declines on its own (the server keeps the invitation until answered). */
const REQUEST_SECONDS = 20;

/** ClientTextKeys (English defaults), as the classic tradeWindow.ts. */
const T = {
  DealCancelledMustFace: "Deal cancelled.\nTo deal correctly you must face the other party.",
  RoomCheck: "The trade could not go through: one of you lacks the bag space, carrying capacity or gold room for it (see the chat). Both confirmations were reset.",
};

const dontTrade = (_item: UserItem, info: ItemInfo | undefined): string => info && (info.Bind & BindMode.DontTrade) ? "Can't be traded" : "";

// — The trade session (DOM-free apart from the popups it asks) —

/** One side of a finished trade: the items and gold as they stood when you confirmed. */
export interface TradeOffer { readonly items: readonly (UserItem | null)[]; readonly gold: number }
export interface TradeResult { readonly partner: string; readonly gave: TradeOffer; readonly got: TradeOffer }

/** The trade outside the window: the request toast, whether a trade is open (S.TradeAccept until S.TradeConfirm /
 *  S.TradeCancel), the room-check note, the result. One per UI runtime, built by the watch hook at UI start. */
export class TradeSession {
  /** Any session state changed (start, end, note). */
  readonly changed = new Emitter<void>();
  private _active = false;
  private _partner = "";
  private _note = "";
  private _result: TradeResult | null = null;
  private selfCancel = false;
  private mine: TradeOffer = { items: [], gold: 0 };
  private guest: TradeOffer = { items: [], gold: 0 };
  private prompt: { p: PopupPromise<boolean>; settled: boolean } | null = null;

  constructor(private readonly host: ModernUiHost) {
    const m = host.ctx.models, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(m.trade.requested.on(({ name }) => this.request(name)));
    on(m.trade.accepted.on(({ name }) => this.start(name)));
    on(m.trade.changed.on(() => this.snap()));
    on(m.inventory.subscribe(() => this.snap()));
    on(host.packets.on((p) => this.packet(p)));
  }

  /** A trade is open (the window cancels it when it closes). */
  get active(): boolean { return this._active; }
  /** S.TradeAccept's name (kept after the end for the summary). */
  get partner(): string { return this._partner; }
  /** The last room-check refusal ("" = none). */
  get note(): string { return this._note; }
  /** The finished trade's summary (null while trading or after a cancel). */
  get result(): TradeResult | null { return this._result; }

  /** GameScene.TradeRequest: a toast (Accept / Decline) that declines when its bar runs out. */
  private request(name: string): void {
    this.closePrompt();
    const entry = { p: this.host.rt.popups.request({ from: name, tag: "Trade", text: "wants to trade with you.", seconds: REQUEST_SECONDS }), settled: false };
    this.prompt = entry;
    void entry.p.then((yes) => {
      if (this.prompt === entry) this.prompt = null;
      if (entry.settled || this.host.rt.signal.aborted) return; // replaced / the trade started / log-out: no answer
      this.host.ctx.models.trade.reply(yes);
    });
  }

  private closePrompt(): void {
    if (!this.prompt) return;
    this.prompt.settled = true;
    this.prompt.p.close();
    this.prompt = null;
  }

  /** GameScene.TradeAccept: the trade is open (TradeDialog.TradeAccept shows the dialogs). */
  private start(name: string): void {
    this.closePrompt();
    this._active = true;
    this._partner = name;
    this._note = "";
    this._result = null;
    this.selfCancel = false;
    this.mine = { items: [], gold: 0 };
    this.guest = { items: [], gold: 0 };
    this.changed.emit();
    this.host.openWindow(TRADE_WINDOW, true);
  }

  /** While you are confirmed the offers are final (any change unlocks you): keep them for the summary. */
  private snap(): void {
    const m = this.host.ctx.models;
    if (!this._active || !m.inventory.tradeLocked) return;
    this.mine = { items: m.inventory.trade.slice(), gold: m.inventory.tradeGold };
    this.guest = { items: m.trade.guestItems.slice(), gold: m.trade.guestGold };
  }

  /** The ends of a trade (the models have reset both sides already). */
  private packet(p: ServerPacket): void {
    switch (p.type) {
      case "TradeAccept": case "TradeItem": case "TradeGold":
        if (this._note) { this._note = ""; this.changed.emit(); }
        return;
      case "TradeConfirm":
        if (!this._active) return;
        this._active = false;
        this._note = "";
        this._result = { partner: this._partner, gave: this.mine, got: this.guest };
        this.changed.emit();
        return;
      case "TradeCancel": {
        if (p.Unlock) { this._note = T.RoomCheck; this.changed.emit(); return; } // the room check failed: both unlocked
        const wasActive = this._active, self = this.selfCancel;
        this._active = false;
        this.selfCancel = false;
        this._note = "";
        this._result = null;
        this.host.openWindow(TRADE_WINDOW, false);
        this.changed.emit();
        if (wasActive && !self) void this.host.rt.popups.confirm({ title: "Trade", text: T.DealCancelledMustFace, kind: "info", buttons: "ok" });
        return;
      }
    }
  }

  /** Every close path of the window: an open trade is cancelled (TradeDialog's close button, C.TradeCancel). */
  windowClosed(): void {
    this._result = null;
    if (!this._active) return;
    this._active = false;
    this.selfCancel = true;
    // Log-out: the server cancels the trade itself when the player leaves (PlayerObject.StopGame).
    if (this.host.ctx.signal.aborted) return;
    try { this.host.ctx.models.trade.cancel(); } catch (err) { console.error(err); }
  }
}

/** The runtime's trade session (built on first use). */
export function tradeSession(host: ModernUiHost): TradeSession {
  return host.rt.service("tradeSession", () => new TradeSession(host));
}

// — The window —

const CSS = `
.hudm .trdw { width: 600px; }
.hudm .trdw .panel { display: flex; flex-direction: column; }
.hudm .trdw .top { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 140% at 0% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .trdw .av { width: 40px; height: 40px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 800;
  color: var(--pcls, var(--tx)); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); box-shadow: 0 0 var(--glr) var(--glow); }
.hudm .trdw .who { min-width: 0; } .hudm .trdw .who b { display: block; font-size: 15px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .trdw .who span { font-size: 11px; color: var(--dim); }
.hudm .trdw .steps { margin-left: auto; display: flex; align-items: center; gap: 6px; font-size: 10px; color: var(--dim); }
.hudm .trdw .steps .st { display: flex; align-items: center; gap: 5px; }
.hudm .trdw .steps .st i { width: 18px; height: 18px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-style: normal; font-size: 9px; font-weight: 700;
  border: 1px solid var(--fr); color: var(--dim); }
.hudm .trdw .steps .st.on { color: #fff; } .hudm .trdw .steps .st.on i { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .trdw .steps .st.ok i { border-color: var(--ac); color: var(--ac); }
.hudm .trdw .steps .ln { width: 14px; height: 1px; background: color-mix(in srgb, var(--fr) 80%, transparent); }
.hudm .trdw .offers { display: flex; flex-direction: column; gap: 10px; padding: 12px 14px; }
.hudm .trdw .pair { display: grid; grid-template-columns: 1fr 52px 1fr; align-items: stretch; }
.hudm .trdw .side { position: relative; border-radius: var(--rad); padding: 9px 10px; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .trdw .side.locked { border-color: var(--ac); box-shadow: 0 0 0 1px var(--ac), inset 0 0 20px color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .trdw .sh { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; min-width: 0; }
.hudm .trdw .sh b { font-size: 12px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .trdw .sh .pill { margin-left: auto; flex: none; font-size: 9px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; padding: 2px 7px; border-radius: 8px; color: var(--dim);
  border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .trdw .side.locked .sh .pill { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .trdw .grid { display: grid; grid-template-columns: repeat(5, 40px); gap: 4px; justify-content: center; }
.hudm .trdw .gold { display: flex; align-items: center; gap: 6px; margin-top: 8px; min-height: 22px; }
.hudm .trdw .gold .coin { width: 14px; height: 14px; border-radius: 50%; flex: none; background: radial-gradient(circle at 35% 35%, #ffe9a0, #c9921f); }
.hudm .trdw .gold .val { flex: 1; text-align: right; font-size: 13px; font-weight: 700; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .trdw .est { margin-top: 6px; display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); }
.hudm .trdw .est b { color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .trdw .mid { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; padding: 0 4px; }
.hudm .trdw .swap { width: 34px; height: 34px; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: var(--ac); border: 1px solid var(--ac);
  background: color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .trdw .fair { width: 100%; }
.hudm .trdw .fair .meter { position: relative; height: 5px; border-radius: 3px; background: color-mix(in srgb, var(--fr) 60%, #000); overflow: hidden; }
.hudm .trdw .fair .meter i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(.5); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .trdw .fair .meter::after { content: ""; position: absolute; left: 50%; top: -2px; bottom: -2px; width: 1px; background: #fff; }
.hudm .trdw .fair span { display: block; margin-top: 4px; text-align: center; font-size: 8px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; color: var(--dim); }
.hudm .trdw .bag { border-radius: var(--rad); padding: 9px 10px; background: color-mix(in srgb, var(--frl) 4%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .trdw .bag .lbl { display: flex; flex-wrap: wrap; gap: 2px 12px; justify-content: space-between; margin-bottom: 7px; }
.hudm .trdw .bag .lbl b { letter-spacing: 0; font-weight: 600; color: var(--tx); text-transform: none; }
.hudm .trdw .bag .lbl b.bad { color: var(--bad-soft); }
.hudm .trdw .bag .grid { grid-template-columns: repeat(12, 40px); justify-content: start; max-height: 132px; overflow-y: auto; scrollbar-width: thin; padding-right: 2px; }
.hudm .trdw .bag .icell.bound::after { content: ""; position: absolute; inset: 0; z-index: 3; pointer-events: none; background: repeating-linear-gradient(135deg, transparent 0 6px, rgba(255, 80, 80, .18) 6px 8px); }
.hudm .trdw .foot { display: flex; align-items: center; gap: 8px; padding: 10px 14px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .trdw .foot .note { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); line-height: 1.35; }
.hudm .trdw .foot .note.warn { color: var(--warn); }
.hudm .trdw .foot .btn { height: 30px; padding: 0 14px; }
.hudm .trdw .foot .btn.main { min-width: 150px; }
/* Summary */
.hudm .trdw .done { position: absolute; inset: 30px 0 0; z-index: 5; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.65); border-radius: 0 0 var(--rad) var(--rad); }
.hudm .trdw .done .card { width: 420px; max-width: calc(100% - 32px); padding: 18px 20px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac); box-shadow: 0 0 24px var(--glow); text-align: center; }
.hudm .trdw .done h3 { margin: 0 0 4px; font-size: 17px; color: #fff; }
.hudm .trdw .done .sub { font-size: 11px; color: var(--dim); margin-bottom: 12px; }
.hudm .trdw .done .cols { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; text-align: left; }
.hudm .trdw .done .cols > div { font-size: 11px; color: var(--tx); }
.hudm .trdw .done .cols > div > div { padding: 1px 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .trdw .done .cols b { display: block; margin-bottom: 4px; font-size: 10px; letter-spacing: .12em; text-transform: uppercase; color: var(--acd); }
.hudm .trdw .done .row { display: flex; justify-content: center; margin-top: 16px; }
.hudm.narrow .trdw .bag .grid { grid-template-columns: repeat(10, 40px); }
`;

interface SideEls {
  readonly box: HTMLElement; readonly title: HTMLElement; readonly pill: HTMLElement;
  readonly gold: HTMLElement; readonly slots: HTMLElement; readonly worth: HTMLElement;
}

class TradeWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  /** The bag cell pressed in this window (a click on it offers); cleared by any other press. */
  private bagPress = "";
  private resultShown: TradeResult | null = null;
  private note = "";

  // — DOM, built once —
  private readonly av = h("div", "av");
  private readonly avTag = h("span");
  private readonly whoName = h("b");
  private readonly whoSub = h("span");
  private readonly steps: HTMLElement[] = [];
  private readonly mine: SideEls;
  private readonly theirs: SideEls;
  private readonly meterFill = h("i");
  private readonly verdict = h("span");
  private readonly addGoldBtn: Btn;
  private readonly bagGrid = h("div", "grid");
  private readonly bagSlots = h("b");
  private readonly bagWeight = h("b");
  private bagCells: ItemCell[] = [];
  private readonly noteEl = h("div", "note");
  private readonly confirmBtn: Btn;
  private readonly cancelBtn: Btn;
  private readonly whisperBtn: Btn;
  private readonly done = h("div", "done");

  constructor(private readonly ui: ModernUiHost, private readonly session: TradeSession) {
    super(ui.rt, "trdw", 60, 40);
    injectCss("win-trade", CSS);
    this.m = ui.ctx.models;
    const sig = this.signal, m = this.m;

    // The partner's cells read the Trade model (read-only: no drops, no picks)
    const offTarget = ui.cells.addTarget(GUEST, { item: (i) => m.trade.guestItems[i] ?? null, accepts: () => null, drop: () => {} });
    sig.addEventListener("abort", offTarget, { once: true });

    // Top: partner card, steps, whisper
    this.av.append(this.avTag);
    this.whisperBtn = button("Whisper", () => { if (this.session.partner) this.ui.chat.prefill(`/${this.session.partner} `); }, { icon: "chat", title: "Talk to your partner (/name in the chat box)" });
    const step = (n: number, label: string) => { const el = h("div", "st", h("i", "", String(n)), label); this.steps.push(el); return el; };
    const top = h("div", "top", this.av, h("div", "who", this.whoName, this.whoSub),
      h("div", "steps", step(1, "Offer"), h("div", "ln"), step(2, "Confirm"), h("div", "ln"), step(3, "Done")), this.whisperBtn.el);

    // Offers
    this.addGoldBtn = button("Add gold", () => void this.addGold(), { icon: "coin", title: "Put up gold (taken at once; it can't be lowered, cancelling gives it back)" });
    const mineGrid = h("div", "grid");
    for (let i = 0; i < TRADE_SLOTS; i++) mineGrid.append(ui.cells.cell("trade", i, { size: 40, filter: dontTrade }).el);
    // Double-click an offered item: take it back (the first click picks it, the second puts it back, then this runs).
    mineGrid.addEventListener("dblclick", (e) => { const c = ui.cells.cellOf(e.target); if (c) this.takeBack(c.ref.index); }, { signal: sig });
    const theirGrid = h("div", "grid");
    for (let i = 0; i < TRADE_SLOTS; i++) theirGrid.append(ui.cells.cell(GUEST, i, { size: 40, readOnly: true }).el);
    this.mine = this.side("Your offer", mineGrid, this.addGoldBtn.el);
    this.theirs = this.side("Their offer", theirGrid, null);
    const mid = h("div", "mid", h("div", "swap", svg("swap")), h("div", "fair", h("div", "meter", this.meterFill), this.verdict));
    mid.title = "Estimated worth at shop prices (ItemInfo.Price x count, plus gold)";

    // Your bag: a click offers the item, a drag places it (DontTrade items are striped and refused)
    const bag = h("div", "bag", h("div", "lbl", h("span", "", "Your bag · click or drag an item to offer it"),
      h("span", "", "After the trade: ", this.bagSlots, " · ", this.bagWeight)), this.bagGrid);
    ui.rt.root.addEventListener("pointerdown", () => { this.bagPress = ""; }, { capture: true, signal: sig });
    this.bagGrid.addEventListener("pointerdown", (e) => {
      const c = e.button === 0 ? ui.cells.cellOf(e.target) : undefined;
      this.bagPress = c ? `${c.ref.grid}:${c.ref.index}` : "";
    }, { capture: true, signal: sig });
    const offClick = ui.drag.addClickHook((ref) => {
      if (!this.open || !this.session.active || this.bagPress !== `${ref.grid}:${ref.index}`) return false;
      this.bagPress = "";
      this.offer(ref.index);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    // Footer
    this.cancelBtn = button("Cancel trade", () => this.toggle(false), { title: "Cancel the trade (closing the window does the same)" });
    this.confirmBtn = button("Confirm trade", () => this.confirm(), { cls: "main" });
    const foot = h("div", "foot", this.noteEl, this.cancelBtn.el, this.confirmBtn.el);

    this.done.hidden = true;
    this.el.append(h("div", "panel", this.titleBar("TRADE", "Cancel the trade"), top,
      h("div", "offers", h("div", "pair", this.mine.box, mid, this.theirs.box), bag), foot, this.done));

    // Models: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(m.inventory.subscribe(() => this.invalidate()));
    on(m.trade.changed.on(() => { this.ui.cells.refresh(); this.invalidate(); }));
    on(session.changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }));
    // The partner's MapObj (class / level) can come and go: refresh the card now and then while shown.
    this.everyShown(1000, () => this.renderPartner());
  }

  private side(title: string, grid: HTMLElement, goldControl: HTMLElement | null): SideEls {
    const t = h("b", "", title), pill = h("span", "pill"), gold = h("span", "val"), slots = h("span"), worth = h("b");
    const box = h("div", "side", h("div", "sh", t, pill), grid, h("div", "gold", h("span", "coin"), goldControl, gold),
      h("div", "est", slots, h("span", "", "Worth ~", worth)));
    return { box, title: t, pill, gold, slots, worth };
  }

  protected override shown(): void { this.note = ""; }

  /** Every close path (cross, Esc, Shift+Esc, Close all, log-out): an open trade is cancelled. */
  protected override closed(): void {
    this.bagPress = "";
    const pick = this.m.itemPick;
    if (pick.cell?.grid === "trade") pick.clear();
    this.session.windowClosed();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const s = this.session, inv = this.m.inventory, trade = this.m.trade;
    const active = s.active, result = s.result;
    // Offers (during a trade: the live models; after it: the summary card over them)
    const myItems = inv.trade, theirItems = trade.guestItems;
    this.paintSide(this.mine, myItems, inv.tradeGold, inv.tradeLocked && active, inv.tradeLocked ? "Confirmed" : "Not confirmed");
    setText(this.theirs.title, s.partner ? `${s.partner}'s offer` : "Their offer");
    this.paintSide(this.theirs, theirItems, trade.guestGold, false, "");
    this.theirs.pill.hidden = true; // no packet tells the partner's lock (hidden, see the header)
    const mineW = this.worth(myItems, inv.tradeGold), theirW = this.worth(theirItems, trade.guestGold), total = mineW + theirW;
    const share = total ? theirW / total : 0.5;
    setStyle(this.meterFill, "transform", `scaleX(${share.toFixed(3)})`);
    setText(this.verdict, !total ? "Empty" : Math.abs(share - 0.5) < 0.08 ? "Fair" : share > 0.5 ? "Your favour" : "Their favour");

    // Steps: offer -> you confirmed -> done
    const step = result ? 2 : inv.tradeLocked ? 1 : 0;
    this.steps.forEach((el, i) => {
      el.classList.toggle("on", i === step);
      el.classList.toggle("ok", i < step);
      setText(el.firstChild as HTMLElement, i < step ? "✓" : String(i + 1));
    });

    this.renderPartner();
    this.renderBag();

    // Footer
    this.addGoldBtn.setDisabled(!active || inv.gold <= 0, !active ? "The trade is over" : "You have no gold");
    this.cancelBtn.el.hidden = !active;
    this.confirmBtn.el.hidden = !active;
    this.confirmBtn.setLabel(inv.tradeLocked ? "Confirmed · undo" : "Confirm trade");
    this.confirmBtn.el.classList.toggle("main", !inv.tradeLocked);
    const warn = s.note || this.note;
    setText(this.noteEl, warn || (!active ? (result ? "" : "The trade is over.")
      : inv.tradeLocked ? `Waiting for ${s.partner} to confirm. Changing anything resets both confirmations.`
        : "Both of you must confirm for the trade to go through. Gold you add can't be taken back out."));
    this.noteEl.classList.toggle("warn", !!warn);

    // Summary (built once per result)
    if (result !== this.resultShown) {
      this.resultShown = result;
      this.done.hidden = !result;
      if (result) this.buildSummary(result);
      else this.done.replaceChildren();
    }
  }

  private paintSide(e: SideEls, items: readonly (UserItem | null)[], gold: number, locked: boolean, pill: string): void {
    e.box.classList.toggle("locked", locked);
    setText(e.pill, pill);
    setText(e.gold, `${fmt(gold)} gold`);
    let n = 0;
    for (const x of items) if (x) n++;
    setText(e.slots, `${n} / ${TRADE_SLOTS} slots`);
    setText(e.worth, fmt(this.worth(items, gold)));
  }

  /** Estimated worth: ItemInfo.Price x count (unknown infos count 0) plus gold. */
  private worth(items: readonly (UserItem | null)[], gold: number): number {
    let w = gold;
    for (const x of items) if (x) w += (this.m.itemInfos.get(x.ItemIndex)?.Price ?? 0) * x.Count;
    return w;
  }

  private partnerObj(name: string): MapObj | null {
    if (!name) return null;
    for (const ob of this.ui.ctx.world.objects.values()) if (ob.race === ObjectType.Player && !ob.ownerName && ob.name === name) return ob;
    return null;
  }

  /** The partner card: class / level / guild from the MapObj in view, else the friends list. */
  private renderPartner(): void {
    const name = this.session.partner;
    const ob = this.partnerObj(name);
    const friend = this.m.friends.list.find((f) => f.Name === name);
    const cls = ob ? ob.look.class : friend ? friend.Class : -1;
    const level = ob?.level || friend?.Level || 0;
    const guild = ob?.guildName || friend?.GuildName || "";
    setText(this.avTag, cls >= 0 ? classTag(cls) : "?");
    if (cls >= 0) this.av.style.setProperty("--pcls", classVar(cls)); else this.av.style.removeProperty("--pcls");
    setText(this.whoName, name || "Trade");
    const bits = [cls >= 0 ? `${className(cls)}${level ? ` ${level}` : ""}` : "", guild, friend ? "friend" : "", ob ? "" : "not in view"].filter(Boolean);
    setText(this.whoSub, bits.join(" · "));
    this.whisperBtn.el.hidden = !name;
  }

  /** The bag beside the offers (Inventory.inventory[BELT_IDX..]); cells rebuilt only when the bag size changed. */
  private renderBag(): void {
    const inv = this.m.inventory, bag = inv.inventory;
    const n = Math.max(0, bag.length - BELT_IDX);
    if (this.bagCells.length !== n) {
      for (const c of this.bagCells) c.dispose();
      this.bagCells = [];
      for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 40, signal: this.signal }));
      this.bagGrid.replaceChildren(...this.bagCells.map((c) => c.el));
    }
    for (const c of this.bagCells) {
      const item = c.item;
      c.el.classList.toggle("bound", !!item && !!dontTrade(item, this.m.itemInfos.get(item.ItemIndex)));
    }
    // After the trade (a client-side estimate; the server decides when both have confirmed): free cells of the whole
    // inventory (FreeSpace counts the belt too) and bag weight, with the partner's items in and yours out.
    let free = 0;
    for (const x of bag) if (!x) free++;
    let gotItems = 0, gotWeight = 0; // offered items have left the bag already (the server moved them to the trade grid)
    for (const x of this.m.trade.guestItems) {
      if (!x) continue;
      gotItems++;
      const info = this.m.itemInfos.get(x.ItemIndex);
      if (info) gotWeight += itemWeight(x, info);
    }
    const freeAfter = free - gotItems;
    setText(this.bagSlots, `${Math.max(0, freeAfter)} free ${freeAfter === 1 ? "cell" : "cells"}`);
    this.bagSlots.classList.toggle("bad", freeAfter < 0);
    const st = this.ui.ctx.player.stats;
    const max = st?.stats[Stat.BagWeight] ?? 0, weight = (st?.currentBagWeight ?? 0) + gotWeight;
    setText(this.bagWeight, max > 0 ? `${fmt(weight)} / ${fmt(max)} weight` : "");
    this.bagWeight.classList.toggle("bad", max > 0 && weight > max);
  }

  private buildSummary(r: TradeResult): void {
    const list = (o: TradeOffer) => {
      const rows: HTMLElement[] = [];
      for (const x of o.items) {
        if (!x) continue;
        const info = this.m.itemInfos.get(x.ItemIndex);
        rows.push(h("div", "", `${info?.Name ?? "Item"}${x.Count > 1 ? ` x${fmt(x.Count)}` : ""}`));
      }
      if (o.gold) rows.push(h("div", "gold", `${fmt(o.gold)} gold`));
      if (!rows.length) rows.push(h("div", "", "Nothing"));
      return rows;
    };
    const close = button("Close", () => this.toggle(false), { cls: "main" });
    this.done.replaceChildren(h("div", "card", h("h3", "", "Trade complete"), h("div", "sub", `with ${r.partner}`),
      h("div", "cols", h("div", "", h("b", "", "You received"), ...list(r.got)), h("div", "", h("b", "", "You gave"), ...list(r.gave))),
      h("div", "row", close.el)));
  }

  // — Actions (all through the routes / models: nothing moves until the server answers) —

  private say(text: string): void {
    this.note = text;
    this.invalidate();
  }

  /** A click on a bag cell in this window: onto a matching offered stack with room, else the first free trade slot. */
  private offer(bagIndex: number): void {
    const inv = this.m.inventory, item = inv.inventory[bagIndex];
    if (!item) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    if (dontTrade(item, info)) { this.ui.drag.toast(`${info?.Name ?? "This item"} can't be traded.`); return; }
    let to = -1;
    for (let i = 0; i < inv.trade.length && to < 0; i++) {
      const t = inv.trade[i];
      if (t && t.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && t.Count < info.StackSize) to = i;
    }
    if (to < 0) to = inv.trade.findIndex((x) => !x);
    if (to < 0) { this.say("All 10 trade slots are full."); return; }
    const from: CellRef = { grid: "inv", index: bagIndex }, dest: CellRef = { grid: "trade", index: to };
    this.run(routeDrop(this.ui.cells.deps, from, dest), [from, dest]);
  }

  /** A double-click on an offered item: onto a matching bag stack with room, else the first free bag cell. */
  private takeBack(index: number): void {
    const inv = this.m.inventory, item = inv.trade[index];
    if (!item || !this.session.active) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    let to = -1;
    for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) {
      const b = inv.inventory[i];
      if (b && b.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && b.Count < info.StackSize) to = i;
    }
    if (to < 0) for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) if (!inv.inventory[i]) to = i;
    if (to < 0) { this.say("Your bag is full."); return; }
    const from: CellRef = { grid: "trade", index }, dest: CellRef = { grid: "inv", index: to };
    this.run(routeDrop(this.ui.cells.deps, from, dest), [from, dest]);
  }

  private run(v: Verdict, cells: CellRef[]): void {
    if (!v.ok || !v.run) { if (v.label) this.ui.drag.toast(v.label); return; }
    this.note = "";
    this.ui.drag.request(v.run, cells);
  }

  /** TradeDialog.GoldLabel: MirAmountBox up to the user's gold, then C.TradeGold (add-only: taken at once). */
  private async addGold(): Promise<void> {
    const inv = this.m.inventory;
    if (!this.session.active || inv.gold <= 0) return;
    const n = await this.ui.rt.popups.amount({
      title: "Add gold to the trade", gold: true, min: 1, max: inv.gold, value: 0,
      text: "The gold is taken from you now and can't be lowered. Cancelling the trade gives it back.",
    });
    if (n === null || n <= 0 || this.signal.aborted || !this.session.active) return;
    inv.offerGold(Math.min(n, inv.gold));
  }

  /** TradeDialog.ConfirmButton: C.TradeConfirm with the lock flipped (the server checks room when both have locked). */
  private confirm(): void {
    if (!this.session.active) return;
    this.note = "";
    this.m.inventory.toggleTradeLock();
  }
}

registerWindow(TRADE_WINDOW, (host) => new TradeWindow(host, tradeSession(host)), {
  binds: [], // no hotkey: T (KeybindOptions.Trade) sends a trade request (GameUi.command "trade")
  // The request toast and the S.TradeAccept open run from UI start (the window itself is built on the first trade).
  watch: (host) => { tradeSession(host); },
});
