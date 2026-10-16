// Ported from play/hudRent.ts: the new UI's item rental window over the real models: ItemChanges (the user's side of a
// deal: the lender's Renting cell, the period, the borrower's fee and the locks) and Rental (src/game/rental.ts: the
// partner's side, the confirm switch and the rented-items list). Desktop counterparts: ItemRentalDialog (the list),
// ItemRentingDialog / GuestItemRentingDialog (the item + period), ItemRentDialog / GuestItemRentDialog (the fee) and
// GameScene's rental handlers; the classic port is src/ui/rentalWindow.ts (its rules are kept).
//
// Server rules (PlayerObject.ItemRentalRequest ... ConfirmItemRental, Envir.ReturnRentalItem): a rental is always started
// by the LENDER, facing the borrower (C.ItemRentalRequest pairs you with the player you face: you lend, they borrow);
// a lender may have at most 3 items rented out and a player may rent one item at a time; the lender puts the item, sets
// 1 to 30 days and locks it; the borrower adds the fee in gold (each amount leaves the bag at once and comes back if the
// deal is cancelled) and locks it; then only the lender confirms. Changes from the mock:
//  - opens on the Rental model's events (S.ItemRentalRequest: Rental.opened) on the Deal tab; there is no "I rent" role
//    switch (your side comes from the server); closing the window on any path while a deal is open cancels it
//    (C.CancelItemRental), and the server's cancel / confirm (Rental.closed) ends the Deal tab;
//  - every action goes through the existing requests: the Renting cell is the real "renting" grid (drag a bag item in,
//    or click one in the deal's bag strip: C.DepositRentalItem; drag it back out: C.RetrieveRentalItem), the period
//    (C.ItemRentalPeriod), Lock item / Lock fee (C.ItemRentalLockItem / LockFee), Confirm (C.ConfirmItemRental, enabled
//    by S.CanConfirmItemRental); nothing moves until the server answers;
//  - the fee only ADDS (C.ItemRentalFee sends the added amount; there is no minus or reset: cancel the deal instead);
//  - Rented out = S.GetRentedItems (C.GetRentedItems, at most once a minute) with the 3-item limit, plus your returned
//    items that are still rental-locked (UserItem.Rental.RentalLocked); Renting = your bag / worn items with rental
//    information (owner, return date, the binding flags the server set);
//  - no menu entry: KeybindOptions.Rental (no default key) toggles it; the inventory / game menu may link it later;
//  - HIDDEN (Crystal keeps no data for them): the fee earned per rental, the fast-forward clock, the simulated partner
//    and the contract overlay.
// Narrow layout (a layer below 1040 px): the rules column is hidden and the columns shrink.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { BindMode } from "../../../protocol/enums.generated.ts";
import { KeybindOptions } from "../../../game/keybinds.ts";
import { BELT_IDX } from "../../../game/inventory.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import type { Rental } from "../../../game/rental.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, fmtDate, fmtDuration, gradeName, h, injectCss, plural, restartAnim, setText, tabs, type Btn,
} from "../core/index.ts";
import { routeDrop, type CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry). */
export const RENT_WINDOW = "rent";

const MAX_OUT = 3, MIN_DAYS = 1, MAX_DAYS = 30, DAY = 86_400_000; // PlayerObject: 3 items out, 1..30 days
const FEE_STEPS = [1_000, 10_000, 100_000] as const;
/** The binding flags a rented item gets (PlayerObject.ConfirmItemRental), in the order the chips show them. */
const BOUND: readonly [number, string][] = [
  [BindMode.DontDrop, "Can't drop"], [BindMode.DontStore, "Can't store"], [BindMode.DontSell, "Can't sell"], [BindMode.DontTrade, "Can't trade"],
  [BindMode.UnableToRent, "Can't rent out"], [BindMode.DontUpgrade, "Can't upgrade"], [BindMode.UnableToDisassemble, "Can't disassemble"],
];

/** ClientTextKeys (English) as the classic rentalWindow.ts. */
const T = {
  ItemRentalCancelledFaceOtherParty: "Item rental cancelled.\nTo complete item rental please face the other party throughout the transaction.",
  RentalFee: "Rental fee:",
  Cancelled: "Item rental cancelled.",
  Done: "The rental is done.",
};

type Tab = 0 | 1 | 2; // Deal, Rented out, Renting

// — The session (one per UI runtime): whether the rented-items list has arrived, our own cancels —

class RentSession {
  /** S.GetRentedItems arrived at least once (the 3-item limit is only known after it). */
  listed = false;
  /** We sent C.CancelItemRental ourselves (no "face the other party" message for it). */
  selfCancel = false;

  constructor(host: ModernUiHost) {
    const off = host.ctx.models.rental.listed.on(() => { this.listed = true; });
    host.rt.signal.addEventListener("abort", off, { once: true });
  }
}

function rentSession(host: ModernUiHost): RentSession {
  return host.rt.service("rentSession", () => new RentSession(host));
}

// — The window —

const CSS = `
.hudm .rnwin { width: 980px; }
.hudm .rnwin .panel { height: 590px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .rnwin .bar { display: flex; gap: 10px; align-items: center; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rnwin .bar .sp { flex: 1; } .hudm .rnwin .bar .gold { font-size: 12px; color: var(--dim); } .hudm .rnwin .bar .gold b { color: var(--gold); }
.hudm .rnwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .rnwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .rnwin .lbl { display: flex; justify-content: space-between; align-items: center; }
.hudm .rnwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .rnwin .desk { flex: 1; min-height: 0; display: flex; flex-direction: column; gap: 10px; padding: 12px 16px; }
.hudm .rnwin .top { display: flex; align-items: center; gap: 12px; }
.hudm .rnwin .who { display: flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 20px; border: 1px solid var(--fr); background: color-mix(in srgb, var(--frl) 5%, transparent); font-size: 12px; }
.hudm .rnwin .who .av { width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 9px; font-weight: 800; border: 1px solid var(--ac); }
.hudm .rnwin .who small { display: block; font-size: 10px; color: var(--dim); }
.hudm .rnwin .role { font-size: 12px; color: var(--dim); } .hudm .rnwin .role b { color: var(--ac); }
.hudm .rnwin .sides { flex: 1; min-height: 0; display: grid; grid-template-columns: 1fr 40px 1fr; gap: 10px; }
.hudm .rnwin .side { position: relative; display: flex; flex-direction: column; gap: 10px; padding: 12px; border-radius: var(--rad); overflow-y: auto;
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rnwin .side > * { flex-shrink: 0; }
.hudm .rnwin .side.lock { border-color: var(--ok); }
.hudm .rnwin .side h4 { margin: 0; font-size: 12px; letter-spacing: .12em; text-transform: uppercase; color: var(--ac); display: flex; justify-content: space-between; }
.hudm .rnwin .side h4 span { font-size: 10px; letter-spacing: 0; text-transform: none; color: var(--dim); }
.hudm .rnwin .tag { position: absolute; right: 10px; top: 10px; font-size: 9px; font-weight: 800; letter-spacing: .1em; padding: 2px 8px; border-radius: 8px; color: #000; background: var(--ok); }
.hudm .rnwin .item { display: flex; gap: 10px; align-items: center; } .hudm .rnwin .item b { font-size: 15px; } .hudm .rnwin .item small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rnwin .gic { width: 56px; height: 56px; flex: none; border-radius: var(--rad); border: 1px solid var(--fr); background: center / auto no-repeat, radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); image-rendering: pixelated; }
.hudm .rnwin .bag { display: grid; grid-template-columns: repeat(auto-fill, 38px); gap: 3px; max-height: 128px; overflow-y: auto; scrollbar-width: thin; }
.hudm .rnwin input[type=range] { width: 100%; accent-color: var(--ac); }
.hudm .rnwin .days { display: flex; justify-content: space-between; align-items: baseline; font-size: 11px; color: var(--dim); } .hudm .rnwin .days b { font-size: 22px; color: #fff; }
.hudm .rnwin .prow { display: flex; gap: 6px; align-items: center; }
.hudm .rnwin .prow.flash { animation: hudm-rnflash 1s ease-out 2; } @keyframes hudm-rnflash { 30% { background: color-mix(in srgb, var(--ac) 25%, transparent); } }
.hudm .rnwin .fee { font-size: 26px; font-weight: 800; color: var(--gold); font-variant-numeric: tabular-nums; } .hudm .rnwin .fee small { font-size: 11px; font-weight: 400; color: var(--dim); }
.hudm .rnwin .qb { display: flex; gap: 4px; flex-wrap: wrap; }
.hudm .rnwin .mid { display: flex; align-items: center; justify-content: center; font-size: 22px; color: var(--ac); }
.hudm .rnwin .rnrow { display: flex; gap: 6px; } .hudm .rnwin .rnrow .btn { flex: 1; height: 30px; }
.hudm .rnwin .msg { font-size: 12px; color: var(--dim); min-height: 16px; } .hudm .rnwin .msg.err { color: var(--bad-soft); }
.hudm .rnwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; }
.hudm .rnwin .rules { width: 230px; flex: none; padding: 12px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 6px; font-size: 11px; color: var(--dim); overflow-y: auto; }
.hudm .rnwin .rules div { padding: 6px 8px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); line-height: 1.35; } .hudm .rnwin .rules b { color: var(--tx); }
.hudm .rnwin .list { flex: 1; min-height: 0; padding: 14px 18px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .rnwin .list > * { flex-shrink: 0; }
.hudm .rnwin .cap { display: flex; gap: 6px; align-items: center; font-size: 12px; color: var(--dim); } .hudm .rnwin .cap i { width: 46px; height: 8px; border-radius: 4px; background: #05070c; border: 1px solid var(--fr); }
.hudm .rnwin .cap i.on { background: var(--ac); } .hudm .rnwin .cap .sp { flex: 1; }
.hudm .rnwin .card { display: grid; grid-template-columns: 1fr 230px; gap: 14px; align-items: center; padding: 10px 12px; border-radius: var(--rad);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rnwin .card.icn { grid-template-columns: 52px 1fr 230px; }
.hudm .rnwin .card b { font-size: 14px; } .hudm .rnwin .card small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rnwin .card.empty { grid-template-columns: 1fr; text-align: center; border-style: dashed; color: var(--dim); font-size: 12px; padding: 16px; }
.hudm .rnwin .card.ret { opacity: .8; }
.hudm .rnwin .st { font-size: 10px; font-weight: 800; letter-spacing: .08em; padding: 1px 7px; border-radius: 7px; margin-left: 6px; }
.hudm .rnwin .st.a { color: var(--ac); border: 1px solid var(--ac); } .hudm .rnwin .st.r { color: var(--warn); border: 1px solid var(--warn); }
.hudm .rnwin .tl { font-size: 11px; color: var(--dim); }
.hudm .rnwin .chips { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 6px; }
.hudm .rnwin .chips span { font-size: 11px; padding: 2px 9px; border-radius: 9px; color: #ffb3a6; background: color-mix(in srgb, var(--bad) 10%, transparent); border: 1px solid color-mix(in srgb, var(--bad) 35%, transparent); }
.hudm .rnwin .gap { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-top: 4px; }
.hudm.narrow .rnwin { width: 800px; }
.hudm.narrow .rnwin .rules { display: none; }
.hudm.narrow .rnwin .card { grid-template-columns: 1fr 190px; } .hudm.narrow .rnwin .card.icn { grid-template-columns: 52px 1fr 190px; }
`;

const itemName = (info: ItemInfo | undefined): string => info ? itemFriendlyName(info) || info.Name : "Unknown item";
const gradeColour = (grade: number): string => `var(--grade-${(gradeName(grade) || "none").toLowerCase()})`;
/** A countdown: "3d 04h 10m" style from now to `t` (ms). */
const leftText = (t: number, now = Date.now()): string => t <= now ? "due" : fmtDuration(Math.floor((t - now) / 1000));

/** A time label that the shown-only ticker rewrites. */
interface Countdown { el: HTMLElement; until: number; prefix: string; done: string }

class RentWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly rental: Rental;
  private tab: Tab = 1;
  private note: [string, string] = ["", ""];
  /** The countdown labels of the Rented out / Renting views (rebuilt with their view). */
  private outCd: Countdown[] = [];
  private inCd: Countdown[] = [];
  private inKey = "";
  private bagCells: ItemCell[] = [];
  private guestImg = -2;
  private listKey = "";

  // — DOM, built once —
  private readonly tabsW = tabs(["Deal", "Rented out", "Renting"], 1, (i) => { this.tab = i as Tab; this.invalidate(); });
  private readonly goldText = h("b");
  private readonly deskView = h("div", "desk");
  private readonly outView = h("div", "list");
  private readonly inView = h("div", "list");
  // Deal: the partner, the item side, the fee side, the buttons
  private readonly whoName = h("b");
  private readonly whoAv = h("div", "av");
  private readonly roleText = h("span", "role");
  private readonly itemSide = h("div", "side");
  private readonly itemHead = h("span");
  private readonly itemTag = h("span", "tag", "LOCKED");
  private readonly itemName = h("b");
  private readonly itemSub = h("small");
  private readonly rentCellBox = h("div");
  private readonly guestIcon = h("div", "gic");
  private readonly bagLbl = h("div", "lbl", "From your bag", h("b", "", "click or drag an item"));
  private readonly bagGrid = h("div", "bag");
  private readonly daysText = h("b");
  private readonly backText = h("span");
  private readonly range = h("input");
  private readonly periodRow = h("div", "prow");
  private readonly setPeriodBtn: Btn;
  private readonly lockItemBtn: Btn;
  private readonly takeBackBtn: Btn;
  private readonly feeSide = h("div", "side");
  private readonly feeHead = h("span");
  private readonly feeTag = h("span", "tag", "LOCKED");
  private readonly feeText = h("span");
  private readonly feeSub = h("small");
  private readonly feeAdd = h("div", "qb");
  private readonly feeNote = h("div", "note", "Each amount leaves your gold at once and comes back if the rental is cancelled. The fee can only go up: cancel to start again.");
  private readonly lockFeeBtn: Btn;
  private readonly addBtns: Btn[] = [];
  private readonly confirmBtn: Btn;
  private readonly cancelBtn: Btn;
  private readonly msgEl = h("div", "msg");
  private readonly rentingCell: ItemCell;

  constructor(private readonly ui: ModernUiHost, private readonly session: RentSession) {
    super(ui.rt, "rnwin", 20, 24);
    injectCss("win-rent", CSS);
    this.m = ui.ctx.models;
    this.rental = this.m.rental;
    const sig = this.signal;
    const c = this.m.itemChanges;

    // — Deal: the lender's side (the Renting cell + bag strip + period + lock), or the lender's item seen by the borrower
    this.rentingCell = ui.cells.cell("renting", 0, { size: 56, signal: sig, filter: (_i, info) => info && (info.Bind & BindMode.UnableToRent) ? "Can't be rented out" : "" });
    this.rentCellBox.append(this.rentingCell.el);
    this.range.type = "range";
    this.range.min = String(MIN_DAYS);
    this.range.max = String(MAX_DAYS);
    this.range.value = "7";
    this.listen(this.range, "input", () => this.renderDays());
    this.setPeriodBtn = button("Set period", () => this.setPeriod(), { title: "Send the rental period (C.ItemRentalPeriod)" });
    this.lockItemBtn = button("Lock item", () => c.lockItem(), { cls: "main", icon: "lock", title: "Lock the item and the period" });
    this.takeBackBtn = button("Take back", () => this.takeBack(), { icon: "back", title: "Put the item back in your bag" });
    this.periodRow.append(h("div", "", this.daysText), this.backText);
    this.itemSide.append(h("h4", "", this.itemHead, h("span", "", "item + period")), this.itemTag,
      h("div", "item", this.rentCellBox, this.guestIcon, h("div", "", this.itemName, this.itemSub)),
      this.bagLbl, this.bagGrid,
      h("div", "days", this.periodRow), this.range, h("div", "rnrow", this.setPeriodBtn.el, this.takeBackBtn.el, this.lockItemBtn.el));
    this.listen(this.guestIcon, "pointerover", () => { const it = this.rental.guestLoanItem; if (it) ui.tooltip.show(this.guestIcon, it, ui.labelContext()); });
    this.listen(this.guestIcon, "pointerout", () => ui.tooltip.hide());
    // A click on a bag-strip cell deposits it (the drag's click hook; drags go through the routes as anywhere else)
    const offClick = ui.drag.addClickHook((ref) => {
      if (!this.open || this.tab !== 0 || this.rental.mode !== "lend" || ref.grid !== "inv" || !this.bagCells.some((b) => b.ref.index === ref.index)) return false;
      this.deposit(ref.index);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    // — Deal: the fee side
    for (const a of FEE_STEPS) {
      const b = button(`+${fmt(a)}`, () => this.addFee(a), { title: `Add ${fmt(a)} gold to the fee` });
      this.addBtns.push(b);
      this.feeAdd.append(b.el);
    }
    const more = button("Add…", () => void this.addFeeAsk(), { icon: "coin", title: "Add an amount of gold to the fee" });
    this.addBtns.push(more);
    this.feeAdd.append(more.el);
    this.lockFeeBtn = button("Lock fee", () => c.lockFee(), { cls: "main", icon: "lock", title: "Lock the fee" });
    this.feeSide.append(h("h4", "", this.feeHead, h("span", "", "rental fee")), this.feeTag,
      h("div", "fee", this.feeText, h("small", "", " gold"), h("br"), this.feeSub), this.feeAdd, this.feeNote, h("div", "rnrow", this.lockFeeBtn.el));

    this.confirmBtn = button("Confirm rental", () => this.rental.confirm(), { cls: "main", icon: "check", title: "Hand the item over for the fee" });
    this.cancelBtn = button("Cancel", () => this.cancelDeal(), { icon: "cross", title: "Cancel the rental (the fee and the item go back)" });
    this.deskView.append(
      h("div", "top", h("div", "lbl", "Deal with"), h("div", "who", this.whoAv, h("div", "", this.whoName, h("small", "", "facing you"))), this.roleText),
      h("div", "sides", this.itemSide, h("div", "mid", "⇄"), this.feeSide),
      h("div", "rnrow", this.confirmBtn.el, this.cancelBtn.el), this.msgEl);

    // — Layout
    const rules = h("div", "rules", h("div", "lbl", "Rental rules"),
      h("div", "", h("b", "", "The lender starts. "), "Stand next to the borrower, facing each other, and press Rent out an item."),
      h("div", "", h("b", "", `${MAX_OUT} out at most. `), "A lender can have three items rented out at once."),
      h("div", "", h("b", "", "One at a time. "), "A player can rent only one item."),
      h("div", "", h("b", "", `${MIN_DAYS}–${MAX_DAYS} days. `), "The lender sets the period and locks the item; the borrower adds the fee and locks it; the lender confirms."),
      h("div", "", h("b", "", "Bound while rented: "), BOUND.map(([, l]) => l).join(", ").toLowerCase(), "."),
      h("div", "", h("b", "", "Coming back. "), "At the end, or if the borrower dies, the item is mailed back and can't be rented again for a while."));
    const bar = h("div", "bar", this.tabsW.el, h("span", "sp"), h("span", "gold", "Gold ", this.goldText));
    this.el.append(h("div", "panel", this.titleBar("ITEM RENTAL", "Close"), bar,
      h("div", "body", h("div", "main", this.deskView, this.outView, this.inView), rules)));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    const inv = () => this.invalidate();
    on(this.m.inventory.subscribe(inv)); // the bag, gold, locks and ItemChanges' rental side
    on(this.rental.changed.on(inv));
    on(this.rental.listed.on(() => { this.listKey = ""; this.invalidate(); }));
    on(this.rental.askPeriod.on(() => { this.tab = 0; this.invalidate(); restartAnim(this.periodRow, "flash"); this.range.focus({ preventScroll: true }); }));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.listKey = this.inKey = ""; this.invalidate(); } }));
    // Countdowns: once a second, only while shown
    this.everyShown(1000, () => this.tick());
  }

  /** Rental.opened: the Deal tab. */
  showDeal(): void {
    this.tab = 0;
    this.note = ["", ""];
    this.invalidate();
  }

  /** Rental.closed: the deal ended (cancelled or done); the window stays on the list it belongs to. */
  dealEnded(cancelled: boolean, wasLend: boolean): void {
    this.tab = wasLend ? 1 : 2;
    this.note = cancelled ? [T.Cancelled, "err"] : [T.Done, ""];
    if (!cancelled && wasLend) this.rental.requestList();
    this.listKey = "";
    this.invalidate();
  }

  protected override shown(): void {
    this.rental.requestList(); // ItemRentalDialog.RequestRentedItems (at most once a minute)
    if (this.rental.mode !== null) this.tab = 0;
    else if (this.tab === 0) this.tab = 1;
    this.invalidate();
  }

  /** Every close path while a deal is open cancels it (the deal dialogs' close buttons: C.CancelItemRental). */
  protected override closed(): void {
    if (this.rental.mode !== null) this.cancelDeal();
    this.ui.tooltip.hide();
  }

  private info(item: UserItem | null | undefined): ItemInfo | undefined {
    return item ? this.m.itemInfos.get(item.ItemIndex) : undefined;
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const mode = this.rental.mode;
    if (mode === null && this.tab === 0) this.tab = 1;
    (this.tabsW.el.children[0] as HTMLElement).hidden = mode === null;
    this.tabsW.select(this.tab);
    this.tabsW.badge(1, this.rental.rentedItems.length);
    this.tabsW.badge(2, this.rentingItems().length);
    setText(this.goldText, fmt(this.m.inventory.gold));
    this.deskView.hidden = this.tab !== 0;
    this.outView.hidden = this.tab !== 1;
    this.inView.hidden = this.tab !== 2;
    if (this.tab === 0 && mode !== null) this.renderDeal(mode);
    else if (this.tab === 1) this.renderOut();
    else if (this.tab === 2) this.renderIn();
  }

  private renderDeal(mode: "lend" | "borrow"): void {
    const r = this.rental, c = this.m.itemChanges, lend = mode === "lend";
    setText(this.whoName, r.guestName);
    setText(this.whoAv, r.guestName.slice(0, 2).toUpperCase());
    this.roleText.replaceChildren(lend ? "You " : "You ", h("b", "", lend ? "lend" : "borrow"), lend ? ": put an item, set the period, lock it, then confirm once the fee is locked." : ": add the fee and lock it; the lender confirms.");

    // Item side
    const item = lend ? c.rentalItem : r.guestLoanItem, info = this.info(item);
    const itemLocked = lend ? c.rentalItemLocked : r.guestItemLocked;
    const period = lend ? c.rentalPeriod : r.guestPeriod;
    setText(this.itemHead, lend ? "You lend" : `${r.guestName} lends`);
    this.itemSide.classList.toggle("lock", itemLocked);
    this.itemTag.hidden = !itemLocked;
    this.rentCellBox.hidden = !lend;
    this.guestIcon.hidden = lend;
    setText(this.itemName, item ? itemName(info) : lend ? "Pick an item" : "No item yet");
    this.itemName.style.color = info ? gradeColour(info.Grade) : "";
    setText(this.itemSub, info ? `${gradeName(info.Grade)}${item && item.Count > 1 ? ` · ${fmt(item.Count)}` : ""}` : lend ? "drag it here or click it below" : "waiting for the lender");
    if (!lend) {
      const img = info ? info.Image : -1;
      if (img !== this.guestImg) {
        this.guestImg = img;
        this.guestIcon.style.backgroundImage = "";
        if (img >= 0) void libPicture(Lib.Items, img).then((p) => { if (p && this.guestImg === img) this.guestIcon.style.backgroundImage = `url(${p.url}), radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%)`; });
      }
    }
    const pickBag = lend && !itemLocked && !c.rentalItem;
    this.bagLbl.hidden = this.bagGrid.hidden = !pickBag;
    if (pickBag) this.renderBag();
    // Period: the lender's slider + Set; the borrower sees the lender's period
    this.range.hidden = !lend || itemLocked;
    this.range.disabled = !lend || itemLocked;
    if (lend && period && !this.range.matches(":focus")) this.range.value = String(period);
    this.renderDays();
    this.setPeriodBtn.el.hidden = this.takeBackBtn.el.hidden = this.lockItemBtn.el.hidden = !lend;
    if (lend) {
      const want = Number(this.range.value);
      this.setPeriodBtn.setDisabled(itemLocked || want === c.rentalPeriod, itemLocked ? "The item is locked" : "That period is set");
      this.takeBackBtn.setDisabled(!c.rentalItem || itemLocked || this.m.inventory.isLocked(c.rentalItem), itemLocked ? "Unable to remove locked item, cancel item rental and try again." : "");
      const lockWhy = itemLocked ? "Locked" : !c.rentalItem ? "Put an item first" : c.rentalPeriod < MIN_DAYS ? "Set the period first" : "";
      this.lockItemBtn.setDisabled(!!lockWhy, lockWhy);
      this.lockItemBtn.setLabel(itemLocked ? "Item locked" : "Lock item");
    }

    // Fee side
    const fee = lend ? r.guestFee : c.rentalGold, feeLocked = lend ? r.guestGoldLocked : c.rentalGoldLocked;
    setText(this.feeHead, lend ? `${r.guestName} pays` : "You pay");
    this.feeSide.classList.toggle("lock", feeLocked);
    this.feeTag.hidden = !feeLocked;
    setText(this.feeText, fmt(fee));
    const days = period || 0;
    setText(this.feeSub, fee && days ? `${fmt(Math.round(fee / days))} a day` : lend ? "waiting for an offer…" : "add gold below");
    this.feeAdd.hidden = this.feeNote.hidden = this.lockFeeBtn.el.hidden = lend;
    if (!lend) {
      const gold = this.m.inventory.gold;
      FEE_STEPS.forEach((a, i) => this.addBtns[i]!.setDisabled(feeLocked || gold < a, feeLocked ? "The fee is locked" : "Not enough gold"));
      this.addBtns[FEE_STEPS.length]!.setDisabled(feeLocked || gold <= 0, feeLocked ? "The fee is locked" : "No gold");
      const lockWhy = feeLocked ? "Locked" : fee < 1 ? "Add a fee first" : "";
      this.lockFeeBtn.setDisabled(!!lockWhy, lockWhy);
      this.lockFeeBtn.setLabel(feeLocked ? "Fee locked" : "Lock fee");
    }

    // Confirm (the lender only, after S.CanConfirmItemRental)
    this.confirmBtn.el.hidden = !lend;
    this.confirmBtn.setDisabled(!r.canConfirm, "Both the item and the fee must be locked");
    let msg = this.note[0], cls = this.note[1];
    if (!msg) {
      if (lend && this.session.listed && r.rentedItems.length >= MAX_OUT) { msg = `You already have ${MAX_OUT} items rented out: the server will refuse the confirm.`; cls = "err"; }
      else if (!lend) msg = itemLocked && feeLocked ? `Waiting for ${r.guestName} to confirm…` : "Only the lender confirms the rental.";
      else if (r.canConfirm) msg = "Both sides are locked: confirm to hand the item over.";
    }
    setText(this.msgEl, msg);
    this.msgEl.className = "msg" + (cls ? " " + cls : "");
  }

  private renderDays(): void {
    const lend = this.rental.mode === "lend", c = this.m.itemChanges;
    const days = lend ? (this.range.disabled ? c.rentalPeriod : Number(this.range.value)) : this.rental.guestPeriod;
    if (!days) { setText(this.daysText, "No period yet"); setText(this.backText, lend ? "1 to 30 days" : ""); return; }
    const set = !lend || days === c.rentalPeriod;
    setText(this.daysText, `${days} ${plural(days, "day")}${set ? "" : " (not sent)"}`);
    setText(this.backText, `back around ${fmtDate(Date.now() + days * DAY)}`);
    if (lend && !this.range.disabled) this.setPeriodBtn.setDisabled(days === c.rentalPeriod, "That period is set");
  }

  /** The deal's bag strip (bag cells, rebuilt only when the bag size changed). */
  private renderBag(): void {
    const inv = this.m.inventory.inventory, n = Math.max(0, inv.length - BELT_IDX);
    if (this.bagCells.length === n) return;
    for (const b of this.bagCells) b.dispose();
    this.bagCells = [];
    for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 38, signal: this.signal }));
    this.bagGrid.replaceChildren(...this.bagCells.map((b) => b.el));
  }

  /** Rented out: S.GetRentedItems (3 slots) + your own items back from a rental and still rental-locked. */
  private renderOut(): void {
    const list = this.rental.rentedItems, lockedBack = this.lockedBack();
    const key = `${this.session.listed}|${list.map((r) => `${r.ItemId}:${r.ItemReturnDate}:${r.RentingPlayerName}`).join(",")}|${lockedBack.map((i) => `${i.UniqueID}:${this.info(i) ? 1 : 0}`).join(",")}|${this.rental.mode}|${this.note[0]}`;
    if (key === this.listKey) return;
    this.listKey = key;
    this.outCd = [];
    const n = list.length;
    const cap = h("div", "cap", "Rented out ");
    for (let i = 0; i < MAX_OUT; i++) cap.append(h("i", i < n ? "on" : ""));
    cap.append(this.session.listed ? `${n}/${MAX_OUT}` : "…", h("span", "sp"));
    const rentOut = button("Rent out an item", () => this.request(), { cls: "main", icon: "swap", title: "Face the player you lend to, then press (C.ItemRentalRequest)" });
    const full = this.session.listed && n >= MAX_OUT;
    rentOut.setDisabled(this.rental.mode !== null || full, this.rental.mode !== null ? "A rental is already open" : `You already have ${MAX_OUT} items rented out`);
    cap.append(rentOut.el);
    const kids: HTMLElement[] = [cap];
    if (this.note[0]) kids.push(h("div", "msg" + (this.note[1] ? " " + this.note[1] : ""), this.note[0]));
    kids.push(h("div", "note", "Stand next to the borrower, facing each other, then press Rent out an item. The list updates at most once a minute."));
    for (const r of list) {
      const until = dateFromBinary(r.ItemReturnDate).getTime();
      const tl = h("div", "tl");
      const left = h("span");
      tl.append(left, h("br"), `returns ${fmtDate(until)}`);
      kids.push(h("div", "card", h("div", "", h("b", "", r.ItemName), h("span", "st a", "RENTED"), h("small", "", `to ${r.RentingPlayerName}`)), tl));
      this.outCd.push({ el: left, until, prefix: "Back in ", done: "Due back now" });
    }
    if (this.session.listed) for (let i = n; i < MAX_OUT; i++) kids.push(h("div", "card empty", "Free rental slot"));
    else kids.push(h("div", "card empty", "Asking the server for your rented-out items…"));
    if (lockedBack.length) {
      kids.push(h("div", "gap", "Back from a rental"));
      for (const it of lockedBack) {
        const info = this.info(it), until = dateFromBinary(it.Rental!.ExpiryDate).getTime();
        const name = h("b", "", itemName(info));
        if (info) name.style.color = gradeColour(info.Grade);
        const left = h("span");
        kids.push(h("div", "card ret", h("div", "", name, h("span", "st r", "RETURNED")), h("div", "tl", left)));
        this.outCd.push({ el: left, until, prefix: "Can be rented again in ", done: "Can be rented again" });
      }
    }
    this.outView.replaceChildren(...kids);
    this.tick();
  }

  /** Renting: your bag / worn items with rental information (not the rental-locked ones you own). */
  private renderIn(): void {
    const items = this.rentingItems();
    const key = items.map((i) => `${i.UniqueID}:${i.Rental!.ExpiryDate}:${i.Rental!.BindingFlags}:${this.info(i) ? 1 : 0}`).join(",");
    if (key === this.inKey) return;
    this.inKey = key;
    this.inCd = [];
    const kids: HTMLElement[] = [];
    if (!items.length) kids.push(h("div", "card empty", "You are not renting anything. A player can rent one item at a time; the lender starts the rental."));
    for (const it of items) {
      const info = this.info(it), rent = it.Rental!, until = dateFromBinary(rent.ExpiryDate).getTime();
      const name = h("b", "", itemName(info));
      if (info) name.style.color = gradeColour(info.Grade);
      const left = h("span");
      const chips = h("div", "chips");
      for (const [flag, label] of BOUND) if (rent.BindingFlags & flag) chips.append(h("span", "", label));
      kids.push(h("div", "card", h("div", "", name, h("small", "", `Rented from ${rent.OwnerName}`), chips), h("div", "tl", left, h("br"), `returns ${fmtDate(until)}`)));
      this.inCd.push({ el: left, until, prefix: "", done: "Rental expired" });
    }
    if (items.length) kids.push(h("div", "note", "It goes back to its owner at the return date, or straight away if you die."));
    this.inView.replaceChildren(...kids);
    this.tick();
  }

  /** Rewrites the shown view's countdowns (once a second while shown; setText skips unchanged text). */
  private tick(): void {
    const list = this.tab === 1 ? this.outCd : this.tab === 2 ? this.inCd : null;
    if (!list) return;
    const now = Date.now();
    for (const cd of list) setText(cd.el, cd.until > now ? cd.prefix + leftText(cd.until, now) : cd.done);
  }

  /** Items you are renting (UserItem.Rental with RentalLocked false: GameScene.CreateItemLabel's "rented from"). */
  private rentingItems(): UserItem[] {
    const inv = this.m.inventory, out: UserItem[] = [];
    for (const list of [inv.inventory, inv.equipment]) for (const it of list) if (it?.Rental && !it.Rental.RentalLocked) out.push(it);
    return out;
  }

  /** Your items back from a rental and still locked from renting (UserItem.Rental.RentalLocked, expiry in the future). */
  private lockedBack(): UserItem[] {
    const inv = this.m.inventory, out: UserItem[] = [], now = Date.now();
    for (const list of [inv.inventory, inv.equipment]) {
      for (const it of list) if (it?.Rental?.RentalLocked && dateFromBinary(it.Rental.ExpiryDate).getTime() > now) out.push(it);
    }
    return out;
  }

  // — Actions (all through the models: nothing moves until the server answers) —

  private say(text: string, err = false): void {
    this.note = [text, err ? "err" : ""];
    this.invalidate();
  }

  /** "Rent out an item": C.ItemRentalRequest (the server pairs you with the player you face; refusals come as chat). */
  private request(): void {
    if (this.rental.mode !== null) return;
    if (this.session.listed && this.rental.rentedItems.length >= MAX_OUT) { this.say(`You already have ${MAX_OUT} items rented out.`, true); return; }
    this.note = ["", ""];
    this.rental.request();
    this.say("Asked the player you are facing…");
  }

  private cancelDeal(): void {
    if (this.rental.mode === null) return;
    this.session.selfCancel = true;
    this.rental.cancel();
  }

  /** A click on a bag-strip cell: the inv > renting route (C.DepositRentalItem; the model locks the cell). */
  private deposit(index: number): void {
    const item = this.m.inventory.inventory[index], info = this.info(item);
    if (!item) return;
    if (info && (info.Bind & BindMode.UnableToRent)) { this.ui.drag.toast(`${itemName(info)} can't be rented out.`); return; }
    const from: CellRef = { grid: "inv", index }, to: CellRef = { grid: "renting", index: 0 };
    const v = routeDrop(this.ui.cells.deps, from, to);
    if (!v.ok || !v.run) { if (v.label) this.ui.drag.toast(v.label); return; }
    this.ui.drag.request(v.run, [from, to]);
  }

  /** Take back: the renting > inv route into the first free bag cell (C.RetrieveRentalItem). */
  private takeBack(): void {
    const inv = this.m.inventory.inventory;
    let to = -1;
    for (let i = BELT_IDX; i < inv.length && to < 0; i++) if (!inv[i]) to = i;
    if (to < 0) { this.say("Your bag is full.", true); return; }
    const from: CellRef = { grid: "renting", index: 0 }, dest: CellRef = { grid: "inv", index: to };
    const v = routeDrop(this.ui.cells.deps, from, dest);
    if (!v.ok || !v.run) { if (v.label) this.say(v.label, true); return; }
    this.ui.drag.request(v.run, [from, dest]);
  }

  /** Set period: C.ItemRentalPeriod (1..30). */
  private setPeriod(): void {
    const days = Number(this.range.value);
    if (!this.m.itemChanges.setRentalPeriod(days)) this.say("The period must be 1 to 30 days.", true);
    else this.note = ["", ""];
    this.invalidate();
  }

  /** A quick fee step: C.ItemRentalFee with the added amount (ItemChanges.addRentalFee refuses above your gold). */
  private addFee(amount: number): void {
    const c = this.m.itemChanges;
    if (c.rentalGoldLocked) return;
    if (amount > this.m.inventory.gold) { this.say("Not enough gold.", true); return; }
    this.note = ["", ""];
    c.addRentalFee(amount);
  }

  /** "Add…": the amount box up to your gold (ItemRentDialog's MirAmountBox). */
  private async addFeeAsk(): Promise<void> {
    const gold = this.m.inventory.gold;
    if (gold <= 0 || this.m.itemChanges.rentalGoldLocked) return;
    const n = await this.ui.rt.popups.amount({ title: T.RentalFee, text: "How much gold do you add to the fee?", max: gold, gold: true, value: Math.min(gold, 1_000) });
    if (n && n > 0 && this.rental.mode === "borrow") this.addFee(n);
  }
}

registerWindow(RENT_WINDOW, (host) => new RentWindow(host, rentSession(host)), {
  // No default key on the desktop (KeyBindSettings has none); a player may bind it.
  binds: [KeybindOptions.Rental],
  // S.ItemRentalRequest (Rental.opened) opens the window on its Deal tab; Rental.closed ends it there.
  watch: (host, open) => {
    const rental = host.ctx.models.rental, session = rentSession(host);
    let lastMode: "lend" | "borrow" | null = rental.mode;
    const offs = [
      rental.opened.on(({ mode }) => {
        lastMode = mode;
        session.selfCancel = false;
        host.rt.window<RentWindow>(RENT_WINDOW)?.showDeal();
        open(true);
        host.openWindow("inventory", true); // GameScene.ItemRentalRequest opens the bag with the deal
      }),
      rental.closed.on(({ cancelled }) => {
        const self = session.selfCancel;
        session.selfCancel = false;
        host.rt.peekWindow<RentWindow>(RENT_WINDOW)?.dealEnded(cancelled, lastMode === "lend");
        lastMode = null;
        // GameScene.CancelItemRental: the face-the-other-party box, unless we cancelled it ourselves
        if (cancelled && !self) void host.rt.popups.confirm({ title: "Item rental", text: T.ItemRentalCancelledFaceOtherParty, kind: "warning", buttons: "ok" });
      }),
    ];
    return () => { for (const off of offs) off(); };
  },
});
