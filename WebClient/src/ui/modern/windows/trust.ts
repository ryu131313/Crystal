// Ported from play/hudTrust.ts: the new UI's Trust Merchant (market / auction house) over the real Market model
// (src/game/market.ts). Desktop counterpart: TrustMerchantDialog (Client/MirScenes/Dialogs/TrustMerchantDialog.cs) +
// GameScene.NPCMarket / NPCMarketPage / MarketFail / MarketSuccess; the classic port is src/ui/marketWindow.ts (its
// rules are kept: price limits, confirm texts, the get-back question for unsold items, MarketTime / SearchTime).
//
// Server rules (PlayerObject ConsignItem / MarketBuy / MarketSellNow / MarketGetBack, Globals): consign for
// ConsignmentCost at MinConsignment .. MaxConsignment, auctions for AuctionCost with a starting bid of MinStartingBid ..
// MaxStartingBid, listed for ConsignmentLength days; a bid must beat the current one (the outbid player is refunded by
// mail); the seller may Sell now once a bid is in ("Bid Met"); sales pay the seller less Globals.Commission (5 %); sold
// and expired listings are collected here. Every action is gated by the server on the market page (NPCScript.MarketKey).
//
// Changes from the mock:
//  - opens only when the server opens it (S.NPCMarket: Market.opened) and closes with the NPC session (NpcSession.closed,
//    or a new NPC page = GameScene.NPCResponse); no menu entry or hotkey. A fresh open picks the Market panel like the
//    desktop (Market.setTab). Closing the window is TrustMerchantDialog.Hide (Market.reset: the sell cell unlocks);
//  - Browse = S.NPCMarket pages (10 rows, more via C.MarketPage): the desktop's category filters, a name search and the
//    phase 2.6 extended search C.MarketSearchEx (server-side sort: price up / down, newest, ending soonest; price range;
//    minimum grade). "Buy now / Auction" and "Usable by me" filter the LOADED page only (the server has no such filter);
//    category counts are not sent, so there are none;
//  - Buy now and bids are C.MarketBuy (BidPrice 0 / the bid) after a popup; quick bids +5 / +10 / +25 % of the price to
//    beat (at least Price + 1, as the desktop's amount box). Nothing changes locally: the row is locked until
//    S.MarketSuccess / S.MarketFail (or MarketTime passes), then the view is searched again; gold is only read from
//    Inventory;
//  - the price history sparkline and the suggested price come from C.MarketPriceHistory / S.MarketPriceHistory (the last
//    14 completed sales of the ItemInfo, cached in the Market model, one request a second);
//  - My listings = the user-mode search (Consign or Auction panel): status from the server's Seller label (For Sale /
//    No Bid / Bid Met / Sold / Expired); Collect / Take back / Cancel are C.MarketGetBack (Mode Any; an unsold item asks
//    first), Sell now is C.MarketSellNow, Collect sold is C.MarketGetBack (Mode Sold) for all;
//  - Sell: a bag item (click it in the side bag, or drag it onto the sell cell "x:consign") becomes the Market sell item
//    (its bag cell locked), then C.ConsignItem through Market.sell / Inventory.consign; the cell stays locked until
//    S.ConsignItem. Fee, commission and limits are the server's;
//  - Watchlist: AuctionIDs with a snapshot, kept in this browser (localStorage "crystal.ui.trustWatch"); live data only
//    for listings on the loaded page (Find searches the item's name). Messages: this session's market answers;
//  - HIDDEN (ClientAuction has no data for them): the bid count, the leading bidder / "you lead", the seller of your own
//    listings' buyers, the mock's simulated traffic, toasts and history entries.
// Narrow layout (a layer below 1040 px): 740 px wide; the listing detail opens over the table (Back closes it).

import type { ClientAuction, ClientMarketSale, ItemInfo, UserItem } from "../../../protocol/server.ts";
import { BindMode, ItemGrade, MarketItemType, MarketPanelType, RequiredClass, RequiredGender, RequiredType, Stat } from "../../../protocol/enums.generated.ts";
import {
  AUCTION_COST, CONSIGNMENT_COST, CONSIGNMENT_LENGTH, MARKET_FAIL_TEXT, MARKET_FILTERS, MARKET_PAGE_ROWS, MAX_CONSIGNMENT,
  MAX_STARTING_BID, MIN_CONSIGNMENT, MIN_STARTING_BID, MarketSearchSort, PRICE_HISTORY_DELAY, SEARCH_DELAY, UINT_MAX,
  type MarketFilter,
} from "../../../game/market.ts";
import { BELT_IDX } from "../../../game/inventory.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import type { ItemLabelContext } from "../../itemLabel.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, fmtDuration, fmtTimeOfDay, gradeName, gradeVar, h, injectCss, later, parseAmount, plural,
  segmented, setStyle, setText, svg, svgNode, tabs, toggleSwitch, typingField, type Btn, type IconName, type PopupItem,
} from "../core/index.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const TRUST_WINDOW = "trust";

/** Globals.Commission (the share of a sale the trust merchant keeps; PlayerObject MarketGetBack / MarketSellNow). */
const COMMISSION = 0.05;
/** Gap kept between search-type requests (C.MarketSearch / SearchEx / Page / Refresh): the server drops one sent within
 *  Globals.SearchDelay of the last, silently. */
const SEARCH_GAP = SEARCH_DELAY + 60;
/** Market.ready's delay after a buy / bid / get back / sell now (MarketTime = CMain.Time + 3000), plus a frame. */
const MARKET_WAIT = 3100;
const DAY_MS = 86_400_000;
const WATCH_KEY = "crystal.ui.trustWatch";
const WATCH_MAX = 50;
const LOG_MAX = 60;
const LIST_GRID = "x:market", SEL_GRID = "x:marketSel", SELL_GRID = "x:consign";

/** RequiredType -> the stat it reads (itemLabel.ts LEVEL section); Level / MaxLevel compare the level. */
const REQ_STAT: Readonly<Partial<Record<number, number>>> = {
  [RequiredType.MaxAC]: Stat.MaxAC, [RequiredType.MaxMAC]: Stat.MaxMAC, [RequiredType.MaxDC]: Stat.MaxDC, [RequiredType.MaxMC]: Stat.MaxMC,
  [RequiredType.MaxSC]: Stat.MaxSC, [RequiredType.MinAC]: Stat.MinAC, [RequiredType.MinMAC]: Stat.MinMAC, [RequiredType.MinDC]: Stat.MinDC,
  [RequiredType.MinMC]: Stat.MinMC, [RequiredType.MinSC]: Stat.MinSC,
};

/** The user-mode Seller labels (AuctionInfo.GetSellerLabel) -> the status shown. */
const STATUS: Readonly<Record<string, { text: string; cls: string }>> = {
  "For Sale": { text: "Listed", cls: "active" }, "No Bid": { text: "No bids", cls: "active" }, "Bid Met": { text: "Bid received", cls: "bid" },
  Sold: { text: "Sold", cls: "sold" }, Expired: { text: "Expired", cls: "expired" },
};

const SORTS: readonly [number, string][] = [
  [MarketSearchSort.None, "Default order"], [MarketSearchSort.PriceAscending, "Price: low to high"], [MarketSearchSort.PriceDescending, "Price: high to low"],
  [MarketSearchSort.Newest, "Newest first"], [MarketSearchSort.EndingSoonest, "Ending soonest"],
];

type Tab = "browse" | "mine" | "sell" | "watch" | "log";
const TABS: readonly Tab[] = ["browse", "mine", "sell", "watch", "log"];
const TAB_NAMES = ["Browse", "My listings", "Sell", "Watchlist", "Messages"];
type Kind = "all" | "consign" | "auction";

/** A watched listing: the AuctionID and what it looked like when starred (the server cannot look one up by id). */
interface WatchEntry { id: string; item: number; name: string; count: number; price: number; type: number; seller: string; date: string; added: number }

/** The last completed sales' figures (oldest first in `sales`). */
interface SaleStats { n: number; avg: number; lo: number; hi: number; median: number; last: number }
function saleStats(sales: readonly ClientMarketSale[] | undefined): SaleStats | null {
  if (!sales || !sales.length) return null;
  const p = sales.map((s) => s.Price);
  const sorted = p.slice().sort((a, b) => a - b);
  const mid = sorted.length >> 1;
  return {
    n: p.length, avg: p.reduce((a, b) => a + b, 0) / p.length, lo: sorted[0]!, hi: sorted[sorted.length - 1]!,
    median: sorted.length % 2 ? sorted[mid]! : (sorted[mid - 1]! + sorted[mid]!) / 2, last: p[p.length - 1]!,
  };
}

const round100 = (v: number): number => Math.round(v / 100) * 100;
const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
/** What the seller receives from a sale of `cost` (server: cost - cost * Commission, truncated). */
const net = (cost: number): number => Math.max(0, Math.trunc(cost - cost * COMMISSION));
/** ConsignmentDate + ConsignmentLength days. */
const endsAt = (l: { ConsignmentDate: bigint }): number => dateFromBinary(l.ConsignmentDate).getTime() + CONSIGNMENT_LENGTH * DAY_MS;

const CSS = `
.hudm .trwin { width: 1000px; }
.hudm .trwin .panel { height: 620px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .trwin .top { display: flex; align-items: center; gap: 10px; padding: 4px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .trwin .top .sp { flex: 1; }
.hudm .trwin .top .msg { max-width: 360px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; color: var(--ok); }
.hudm .trwin .top .gold { font-size: 12px; color: var(--dim); white-space: nowrap; } .hudm .trwin .top .gold b { color: var(--gold); font-size: 14px; }
.hudm .trwin .view { flex: 1; min-height: 0; display: flex; position: relative; }
.hudm .trwin .cats { width: 178px; flex: none; overflow-y: auto; padding: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 6px; scrollbar-width: thin; }
.hudm .trwin .cats form { display: flex; gap: 4px; } .hudm .trwin .cats form input { flex: 1; min-width: 0; }
.hudm .trwin .cats .btn { height: 24px; padding: 0 8px; }
.hudm .trwin .clist { display: flex; flex-direction: column; gap: 1px; }
.hudm .trwin .ct { display: block; width: 100%; text-align: left; padding: 4px 8px; border-radius: 4px; cursor: pointer; font: inherit; font-size: 12px; color: var(--tx); border: 0; background: none; }
.hudm .trwin .ct.sub { padding-left: 20px; font-size: 11px; color: var(--dim); }
.hudm .trwin .ct:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); color: #fff; }
.hudm .trwin .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .trwin .fl { display: flex; flex-direction: column; gap: 5px; font-size: 11px; color: var(--dim); }
.hudm .trwin .fl select.fld { width: 100%; }
.hudm .trwin .fl .pr2 { display: flex; gap: 4px; align-items: center; } .hudm .trwin .fl .pr2 input { width: 0; flex: 1; }
.hudm .trwin .fl .tg { display: flex; align-items: center; justify-content: space-between; gap: 6px; color: var(--tx); }
.hudm .trwin .list { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .trwin .lh, .hudm .trwin .lr { display: grid; grid-template-columns: 34px minmax(0, 1fr) 40px 66px 78px 112px; gap: 8px; align-items: center; padding: 0 10px; }
.hudm .trwin .lh { height: 28px; flex: none; font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); border-bottom: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .trwin .lh span:last-child { text-align: right; }
.hudm .trwin .rows { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; }
.hudm .trwin .lr { min-height: 40px; cursor: pointer; font-size: 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 20%, transparent); }
.hudm .trwin .lr:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .trwin .lr.sel { background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .trwin .lr .nm { min-width: 0; } .hudm .trwin .lr .nm b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .trwin .lr .nm small, .hudm .trwin .sub2 { font-size: 10px; color: var(--dim); }
.hudm .trwin .lv.no { color: var(--bad-soft); } .hudm .trwin .tl { font-size: 11px; color: var(--dim); font-variant-numeric: tabular-nums; } .hudm .trwin .tl.soon { color: var(--warn); }
.hudm .trwin .kd { font-size: 9px; font-weight: 800; padding: 1px 6px; border-radius: 6px; white-space: nowrap; }
.hudm .trwin .kd.c { color: var(--ok); border: 1px solid color-mix(in srgb, var(--ok) 60%, #000); } .hudm .trwin .kd.a { color: var(--gold); border: 1px solid color-mix(in srgb, var(--gold) 55%, #000); }
.hudm .trwin .pr { text-align: right; font-variant-numeric: tabular-nums; } .hudm .trwin .pr b { color: var(--gold); } .hudm .trwin .pr small { display: block; font-size: 10px; color: var(--dim); }
.hudm .trwin .star { cursor: pointer; color: var(--dim); margin-left: 6px; border: 0; background: none; padding: 0; font: inherit; font-size: 12px; }
.hudm .trwin .star.on { color: var(--gold); }
.hudm .trwin .pager { flex: none; display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 6px 10px; font-size: 11px; color: var(--dim);
  border-top: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); }
.hudm .trwin .pager .sp { flex: 1; }
.hudm .trwin .pager .btn { height: 22px; padding: 0 8px; }
.hudm .trwin .none { padding: 40px 20px; text-align: center; color: var(--dim); }
.hudm .trwin .det { width: 290px; flex: none; overflow-y: auto; padding: 12px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px; scrollbar-width: thin; }
.hudm .trwin .det > * { flex-shrink: 0; } .hudm .trwin .det .back { display: none; }
.hudm .trwin .dh { display: flex; gap: 10px; align-items: center; } .hudm .trwin .dh b { display: block; font-size: 15px; }
.hudm .trwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; display: flex; flex-direction: column; gap: 4px; }
.hudm .trwin .kv { display: flex; justify-content: space-between; gap: 8px; } .hudm .trwin .kv span { color: var(--dim); } .hudm .trwin .kv b { color: #fff; font-variant-numeric: tabular-nums; }
.hudm .trwin .kv b.g { color: var(--gold); } .hudm .trwin .kv b.r { color: var(--bad-soft); } .hudm .trwin .kv b.ok { color: var(--ok); }
.hudm .trwin .big { font-size: 24px; font-weight: 800; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .trwin svg.spark { width: 100%; height: 54px; display: block; } .hudm .trwin svg.spark .l { fill: none; stroke: var(--ac); stroke-width: 1.6; vector-effect: non-scaling-stroke; }
.hudm .trwin svg.spark .a { fill: color-mix(in srgb, var(--ac) 18%, transparent); stroke: none; } .hudm .trwin svg.spark circle { fill: var(--gold); }
.hudm .trwin .row { display: flex; gap: 6px; } .hudm .trwin .row > .btn, .hudm .trwin .row > input { flex: 1; min-width: 0; }
.hudm .trwin input.amt { height: 30px; font-size: 15px; font-weight: 700; color: var(--gold); }
.hudm .trwin input.amt.bad { border-color: var(--bad); }
.hudm .trwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; white-space: pre-line; } .hudm .trwin .note.err { color: var(--bad-soft); } .hudm .trwin .note.ok { color: var(--ok); }
.hudm .trwin .mine { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .trwin .mhead { flex: none; display: flex; align-items: center; gap: 10px; padding: 8px 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); }
.hudm .trwin .mhead .ui-seg { width: 220px; } .hudm .trwin .mhead .note { flex: 1; }
.hudm .trwin .ml { display: grid; grid-template-columns: 34px minmax(0, 1fr) 100px 84px 230px; gap: 10px; align-items: center; padding: 4px 12px; min-height: 42px; font-size: 12px;
  border-bottom: 1px solid color-mix(in srgb, var(--fr) 20%, transparent); }
.hudm .trwin .ml .st { font-size: 10px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; }
.hudm .trwin .ml .st.active { color: var(--ac); } .hudm .trwin .ml .st.bid { color: var(--gold); } .hudm .trwin .ml .st.sold { color: var(--ok); } .hudm .trwin .ml .st.expired { color: var(--warn); }
.hudm .trwin .ml .acts { display: flex; gap: 4px; justify-content: flex-end; }
.hudm .trwin .sell { flex: 1; min-width: 0; display: flex; }
.hudm .trwin .sell .bag { width: 300px; flex: none; padding: 12px; display: flex; flex-direction: column; gap: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .trwin .sell .grid { display: grid; grid-template-columns: repeat(6, 40px); gap: 4px; overflow-y: auto; max-height: 420px; scrollbar-width: thin; }
.hudm .trwin .sell .form { flex: 1; min-width: 0; padding: 14px 18px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .trwin .sell .slot { display: flex; gap: 12px; align-items: center; }
.hudm .trwin .watch, .hudm .trwin .log { flex: 1; min-width: 0; overflow-y: auto; padding: 10px 14px; font-size: 12px; scrollbar-width: thin; }
.hudm .trwin .wl { display: grid; grid-template-columns: minmax(0, 1fr) 140px 110px auto; gap: 10px; align-items: center; padding: 6px 4px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .trwin .wl b { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .trwin .wl .live { color: var(--ok); } .hudm .trwin .wl .acts { display: flex; gap: 4px; }
.hudm .trwin .log div { padding: 5px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); white-space: pre-line; }
.hudm .trwin .log div.err { color: var(--bad-soft); } .hudm .trwin .log small { color: var(--dim); margin-right: 8px; font-variant-numeric: tabular-nums; }
/* Narrow layer: smaller window; the detail opens over the table. */
.hudm.narrow .trwin { width: 740px; }
.hudm.narrow .trwin .top .msg { max-width: 180px; }
.hudm.narrow .trwin .cats { width: 156px; }
.hudm.narrow .trwin .lh, .hudm.narrow .trwin .lr { grid-template-columns: 34px minmax(0, 1fr) 34px 62px 70px 96px; gap: 6px; }
.hudm.narrow .trwin .det { display: none; position: absolute; right: 0; top: 0; bottom: 0; z-index: 2; background: var(--page); box-shadow: -8px 0 20px rgba(0,0,0,.6); }
.hudm.narrow .trwin .det .back { display: inline-flex; }
.hudm.narrow .trwin.detview .det { display: flex; }
.hudm.narrow .trwin .ml { grid-template-columns: 34px minmax(0, 1fr) 84px 70px 200px; gap: 6px; }
.hudm.narrow .trwin .sell .bag { width: 250px; } .hudm.narrow .trwin .sell .grid { grid-template-columns: repeat(5, 40px); }
`;

/** One prebuilt Browse row (10 of them; re-pointed each render). */
interface BrowseRow { el: HTMLElement; name: HTMLElement; seller: HTMLElement; star: HTMLButtonElement; lv: HTMLElement; kind: HTMLElement; time: HTMLElement; price: HTMLElement; priceSub: HTMLElement; listing: ClientAuction | null }
/** One prebuilt My-listings row. */
interface MineRow { el: HTMLElement; name: HTMLElement; line: HTMLElement; status: HTMLElement; time: HTMLElement; acts: HTMLElement; actsKey: string; listing: ClientAuction | null }

class TrustWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: Tab = "browse";
  /** Consign or Auction: the My listings / Sell panel (Market.setTab). */
  private ownKind: number = MarketPanelType.Consign;
  // Browse filters
  private query = "";
  private sort: number = MarketSearchSort.None;
  private priceMin = 0;
  private priceMax = 0;
  private minGrade: number = ItemGrade.None;
  private kind: Kind = "all";
  private usableOnly = false;
  /** The rows the custom grid "x:market" shows now (the visible view's). */
  private shownRows: (ClientAuction | null)[] = [];
  /** The listing a buy / bid / get back / sell now waits on (until the answer or MarketTime). */
  private pending: bigint | null = null;
  private lastSearch = -1e9;
  private searchCancel: (() => void) | null = null;
  private historyCancel: (() => void) | null = null;
  /** ItemIndexes whose price history was asked for while this window was open. */
  private readonly asked = new Set<number>();
  private readonly watch = new Map<string, WatchEntry>();
  private watchKey = "";
  private catsKey = "";
  private detKey = "";
  private sparkKey = "";
  private sellKey = "";
  private sidePress = "";

  // — DOM, built once —
  private readonly goldEl = h("b");
  private readonly msgEl = h("span", "msg");
  private readonly topTabs: ReturnType<typeof tabs>;
  private readonly views: Record<Tab, HTMLElement>;
  // browse
  private readonly searchInput = typingField(h("input", "fld"));
  private readonly catList = h("div", "clist");
  private readonly sortSel = h("select", "fld");
  private readonly gradeSel = h("select", "fld");
  private readonly minIn = typingField(h("input", "fld"));
  private readonly maxIn = typingField(h("input", "fld"));
  private readonly kindSeg: ReturnType<typeof segmented<Kind>>;
  private readonly usableTog: ReturnType<typeof toggleSwitch>;
  private readonly browseRows: BrowseRow[] = [];
  private readonly browseNone = h("div", "none");
  private readonly browsePager: { el: HTMLElement; text: HTMLElement; note: HTMLElement; prev: Btn; next: Btn };
  // detail
  private readonly det = h("div", "det");
  private readonly detEmpty = h("div", "note", "Pick a listing to see its recent sales and to buy or bid.");
  private readonly detBody = h("div", "", "");
  private readonly detName = h("b");
  private readonly detSub = h("small", "sub2");
  private readonly histTitle = h("span");
  private readonly histAvg = h("b", "g");
  private readonly histLo = h("span");
  private readonly histHi = h("span");
  private readonly histNote = h("div", "note");
  private readonly spark = svgNode("svg", { class: "spark", viewBox: "0 0 266 54", preserveAspectRatio: "none", "aria-hidden": "true" });
  private readonly sparkArea = svgNode("path", { class: "a", d: "" });
  private readonly sparkLine = svgNode("path", { class: "l", d: "" });
  private readonly sparkDot = svgNode("circle", { r: 3, cx: -10, cy: -10 });
  private readonly buyBox = h("div", "box");
  private readonly buyBig = h("div", "big");
  private readonly buyTime = h("b");
  private readonly buyAfter = h("b");
  private readonly buyBtn: Btn;
  private readonly bidBox = h("div", "box");
  private readonly bidBig = h("div", "big");
  private readonly bidTime = h("b");
  private readonly bidIn = typingField(h("input", "fld amt"));
  private readonly bidBtn: Btn;
  private readonly bidMin = h("div", "note");
  private readonly watchBtn: Btn;
  private readonly detNote = h("div", "note");
  // mine
  private readonly mineSeg: ReturnType<typeof segmented<number>>;
  private readonly mineNote = h("div", "note");
  private readonly collectBtn: Btn;
  private readonly mineRows: MineRow[] = [];
  private readonly mineNone = h("div", "none");
  private readonly minePager: { el: HTMLElement; text: HTMLElement; note: HTMLElement; prev: Btn; next: Btn };
  // sell
  private readonly sellSeg: ReturnType<typeof segmented<number>>;
  private readonly sellBag = h("div", "grid");
  private bagCells: ItemCell[] = [];
  private readonly sellName = h("b");
  private readonly sellSub = h("div", "note");
  private readonly sellHist = h("div", "note");
  private readonly priceLbl = h("div", "lbl");
  private readonly priceIn = typingField(h("input", "fld amt"));
  private readonly useBtn: Btn;
  private readonly feeEl = h("b", "g");
  private readonly commEl = h("b");
  private readonly recvLbl = h("span");
  private readonly recvEl = h("b", "ok");
  private readonly listBtn: Btn;
  private readonly sellRules = h("div", "note");
  private readonly sellNote = h("div", "note");
  // watch / log
  private readonly watchList = h("div", "watch");
  private readonly logList = h("div", "log");

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "trwin", 12, 20);
    injectCss("win-trust", CSS);
    this.m = ui.ctx.models;
    const sig = this.signal, market = this.m.market;
    this.loadWatch();

    // Custom grids: the listing rows, the selected listing, the sell cell (display / drop only)
    const offs = [
      ui.cells.addTarget(LIST_GRID, {
        item: (i) => this.shownRows[i]?.Item ?? null,
        locked: (i) => this.busy(this.shownRows[i] ?? null),
        accepts: () => null, drop: () => {},
      }),
      ui.cells.addTarget(SEL_GRID, { item: () => market.selected?.Item ?? null, accepts: () => null, drop: () => {} }),
      ui.cells.addTarget(SELL_GRID, {
        item: () => market.sellItem,
        accepts: (from, item) => from.grid !== "inv" || from.index < BELT_IDX ? (from.grid === "inv" ? "Use a bag item" : null) : this.sellRefusal(item),
        verb: () => (this.ownKind === MarketPanelType.Auction ? "Auction" : "Consign"),
        drop: (_from, item) => this.pickSell(item),
        use: () => { market.setSellItem(null); this.invalidate(); },
      }),
    ];
    for (const off of offs) sig.addEventListener("abort", off, { once: true });

    // Top: tabs, last message, gold
    this.topTabs = tabs(TAB_NAMES, 0, (i) => this.enterTab(TABS[i]!));
    const top = h("div", "top", this.topTabs.el, h("span", "sp"), this.msgEl, h("span", "gold", "Gold ", this.goldEl));

    // — Browse: categories + search + extended filters | table | detail —
    const form = h("form", "", this.searchInput, button("Find", () => this.findClick(), { icon: "search", title: "Search item names (Enter)" }).el);
    this.searchInput.placeholder = "Item name…";
    this.searchInput.maxLength = 20;
    form.addEventListener("submit", (e) => { e.preventDefault(); this.findClick(); }, { signal: sig });
    for (const [v, t] of SORTS) { const o = h("option", "", t); o.value = String(v); this.sortSel.append(o); }
    this.sortSel.addEventListener("change", () => { this.sort = Number(this.sortSel.value); this.browseSearch(); }, { signal: sig });
    for (let g = ItemGrade.None; g <= ItemGrade.Heroic; g++) { const o = h("option", "", g === ItemGrade.None ? "Any grade" : `${gradeName(g)} or better`); o.value = String(g); this.gradeSel.append(o); }
    this.gradeSel.addEventListener("change", () => { this.minGrade = Number(this.gradeSel.value); this.browseSearch(); }, { signal: sig });
    for (const [el, ph] of [[this.minIn, "Min"], [this.maxIn, "Max"]] as const) {
      el.placeholder = ph; el.inputMode = "numeric"; el.maxLength = 13;
      el.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); this.applyPrice(); } }, { signal: sig });
    }
    this.kindSeg = segmented<Kind>([{ value: "all", label: "All" }, { value: "consign", label: "Buy now" }, { value: "auction", label: "Auction" }], "all",
      (v) => { this.kind = v; this.invalidate(); });
    this.usableTog = toggleSwitch(false, (on) => { this.usableOnly = on; this.invalidate(); }, { small: true, label: "Usable by me", title: "Hide items you cannot use (this page only)" });
    const fl = h("div", "fl",
      h("div", "lbl", "Order"), this.sortSel, h("div", "lbl", "Grade"), this.gradeSel,
      h("div", "lbl", "Price"), h("div", "pr2", this.minIn, "–", this.maxIn), button("Apply price", () => this.applyPrice(), { icon: "filter" }).el,
      h("div", "lbl", "On this page"), this.kindSeg.el, h("label", "tg", "Usable by me", this.usableTog.el),
      button("Refresh", () => this.refreshView(), { icon: "refresh", title: "Search again" }).el);
    const cats = h("div", "cats", form, this.catList, fl);

    const head = h("div", "lh", h("span"), h("span", "", "Item"), h("span", "", "Lv"), h("span", "", "Type"), h("span", "", "Time left"), h("span", "", "Price / bid"));
    const rows = h("div", "rows");
    for (let i = 0; i < MARKET_PAGE_ROWS; i++) {
      const cell = ui.cells.cell(LIST_GRID, i, { size: 32, readOnly: true, signal: sig });
      const name = h("b"), seller = h("span"), star = h("button", "star", "★");
      star.type = "button";
      star.title = "Watch this listing";
      const lv = h("span", "lv"), kind = h("span", "kd"), time = h("span", "tl"), price = h("b"), priceSub = h("small");
      const el = h("div", "lr", cell.el, h("div", "nm", name, h("small", "", seller, star)), lv, h("span", "", kind), time, h("div", "pr", price, priceSub));
      const row: BrowseRow = { el, name, seller, star, lv, kind, time, price, priceSub, listing: null };
      el.addEventListener("click", () => { if (row.listing) this.select(row.listing); }, { signal: sig });
      star.addEventListener("click", (e) => { e.stopPropagation(); if (row.listing) this.toggleWatch(row.listing); }, { signal: sig });
      this.browseRows.push(row);
      rows.append(el);
    }
    rows.append(this.browseNone);
    this.browsePager = this.pager();
    const list = h("div", "list", head, rows, this.browsePager.el);

    // Detail pane
    this.spark.append(this.sparkArea, this.sparkLine, this.sparkDot);
    const selCell = ui.cells.cell(SEL_GRID, 0, { size: 52, readOnly: true, signal: sig });
    this.buyBtn = button("Buy now", () => void this.buyNow(), { cls: "main big", icon: "coin" });
    this.buyBox.append(this.buyBig, h("div", "kv", h("span", "", "Time left"), this.buyTime), h("div", "kv", h("span", "", "Gold after"), this.buyAfter), h("div", "row", this.buyBtn.el));
    this.bidIn.inputMode = "numeric";
    this.bidIn.maxLength = 13;
    this.bidIn.addEventListener("input", () => this.bidInput(), { signal: sig });
    this.bidIn.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); void this.placeBid(); } }, { signal: sig });
    this.bidBtn = button("Place bid", () => void this.placeBid(), { cls: "main big", icon: "coin" });
    const quick = h("div", "row", ...[5, 10, 25].map((p) => button(`+${p}%`, () => this.quickBid(p), { title: `Bid ${p}% over the price to beat` }).el));
    this.bidBox.append(this.bidBig, h("div", "kv", h("span", "", "Ends in"), this.bidTime), this.bidMin, h("div", "row", this.bidIn), quick, h("div", "row", this.bidBtn.el),
      h("div", "note", "The highest bid when the auction ends wins. If someone outbids you, your gold comes back by mail."));
    this.watchBtn = button("Watch", () => { const s = market.selected; if (s) this.toggleWatch(s); }, { icon: "star" });
    const whisper = button("Whisper seller", () => { const s = market.selected; if (s && !market.userMode) this.ui.chat.prefill(`/${s.Seller} `); }, { icon: "chat" });
    const back = button("Back", () => this.el.classList.remove("detview"), { icon: "back", cls: "back" });
    this.detBody.append(
      h("div", "dh", selCell.el, h("div", "", this.detName, this.detSub)),
      h("div", "box", h("div", "kv", this.histTitle, this.histAvg), this.spark, h("div", "kv", this.histLo, this.histHi), this.histNote),
      this.buyBox, this.bidBox, this.detNote, h("div", "row", this.watchBtn.el, whisper.el));
    this.det.append(back.el, this.detEmpty, this.detBody);

    // — My listings —
    this.mineSeg = segmented<number>([{ value: MarketPanelType.Consign, label: "Buy now" }, { value: MarketPanelType.Auction, label: "Auctions" }], this.ownKind,
      (v) => this.setOwnKind(v));
    this.collectBtn = button("Collect sold", () => this.collectSold(), { cls: "main", icon: "coinGold", title: "Collect the gold of every sold listing (less the commission)" });
    const mineRows = h("div", "rows");
    for (let i = 0; i < MARKET_PAGE_ROWS; i++) {
      const cell = ui.cells.cell(LIST_GRID, i, { size: 32, readOnly: true, signal: sig });
      const name = h("b"), line = h("div", "note"), status = h("span", "st"), time = h("span", "tl"), acts = h("div", "acts");
      const el = h("div", "ml", cell.el, h("div", "", name, line), status, time, acts);
      this.mineRows.push({ el, name, line, status, time, acts, actsKey: "", listing: null });
      mineRows.append(el);
    }
    mineRows.append(this.mineNone);
    this.minePager = this.pager();
    const mine = h("div", "mine", h("div", "mhead", this.mineSeg.el, this.mineNote, this.collectBtn.el), mineRows, this.minePager.el);

    // — Sell —
    this.sellSeg = segmented<number>([
      { value: MarketPanelType.Consign, label: "Buy now", title: `Fixed price ${fmt(MIN_CONSIGNMENT)} – ${fmt(MAX_CONSIGNMENT)}, fee ${fmt(CONSIGNMENT_COST)}` },
      { value: MarketPanelType.Auction, label: "Auction", title: `Starting bid ${fmt(MIN_STARTING_BID)} – ${fmt(MAX_STARTING_BID)}, fee ${fmt(AUCTION_COST)}` },
    ], this.ownKind, (v) => this.setOwnKind(v));
    const sellCell = ui.cells.cell(SELL_GRID, 0, { size: 52, empty: svg("tag"), signal: sig });
    this.priceIn.inputMode = "numeric";
    this.priceIn.maxLength = 13;
    this.priceIn.addEventListener("input", () => this.invalidate(), { signal: sig });
    this.priceIn.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); void this.listItem(); } }, { signal: sig });
    this.useBtn = button("Use suggested", () => this.useSuggested(), { title: "The median of the item's recent sales, within the limits" });
    this.listBtn = button("List item", () => void this.listItem(), { cls: "main big", icon: "tag" });
    const bag = h("div", "bag", h("div", "lbl", "How to sell"), this.sellSeg.el, h("div", "lbl", "Your bag · click an item, or drag it to the sell cell"), this.sellBag);
    const sellForm = h("div", "form",
      h("div", "slot", sellCell.el, h("div", "", this.sellName, this.sellSub)), this.sellHist,
      this.priceLbl, h("div", "row", this.priceIn, this.useBtn.el),
      h("div", "box",
        h("div", "kv", h("span", "", "Listing fee (paid now)"), this.feeEl),
        h("div", "kv", h("span", "", `Commission on sale (${COMMISSION * 100}%)`), this.commEl),
        h("div", "kv", this.recvLbl, this.recvEl),
        h("div", "kv", h("span", "", "Listed for"), h("b", "", `${CONSIGNMENT_LENGTH} days`))),
      h("div", "row", this.listBtn.el), this.sellNote, this.sellRules);
    const sell = h("div", "sell", bag, sellForm);
    // A press on a side-bag cell marks it; the click hook then puts it in the sell cell (like the storage window).
    ui.rt.root.addEventListener("pointerdown", () => { this.sidePress = ""; }, { capture: true, signal: sig });
    this.sellBag.addEventListener("pointerdown", (e) => {
      const c = e.button === 0 ? ui.cells.cellOf(e.target) : undefined;
      this.sidePress = c ? `${c.ref.grid}:${c.ref.index}` : "";
    }, { capture: true, signal: sig });
    const offClick = ui.drag.addClickHook((ref, item) => {
      if (!this.open || this.tab !== "sell" || this.sidePress !== `${ref.grid}:${ref.index}`) return false;
      this.sidePress = "";
      const why = this.sellRefusal(item);
      if (why) this.ui.drag.toast(why); else this.pickSell(item);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    this.views = {
      browse: h("div", "view", cats, list, this.det),
      mine: h("div", "view", mine),
      sell: h("div", "view", sell),
      watch: h("div", "view", this.watchList),
      log: h("div", "view", this.logList),
    };
    this.logList.append(h("div", "note", "Answers from the trust merchant this session appear here."));
    this.el.append(h("div", "panel", this.titleBar("TRUST MERCHANT", "Close"), top, ...TABS.map((t) => this.views[t])));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(market.changed.on(() => { this.ui.cells.refresh(); this.invalidate(); }));
    on(market.sellChanged.on(() => { this.ui.cells.refresh(); this.invalidate(); }));
    on(market.priceHistoryChanged.on(() => this.invalidate()));
    on(market.message.on(({ text }) => this.answer(text)));
    on(this.m.inventory.subscribe(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.ui.cells.refresh(); this.detKey = this.sellKey = this.watchKey = ""; this.invalidate(); } }));
    // Countdowns tick in place while shown.
    this.everyShown(1000, () => this.tickTimes());
  }

  protected override shown(): void {
    // TrustMerchantDialog.Show: the Market panel, filters from the top (DrawFilters(0, -1)), the bag beside it.
    const market = this.m.market;
    this.tab = "browse";
    this.topTabs.select(0);
    this.query = ""; this.searchInput.value = "";
    this.sort = MarketSearchSort.None; this.sortSel.value = String(this.sort);
    this.minGrade = ItemGrade.None; this.gradeSel.value = String(this.minGrade);
    this.priceMin = this.priceMax = 0; this.minIn.value = this.maxIn.value = "";
    this.pending = null;
    this.asked.clear();
    this.catsKey = this.detKey = this.sparkKey = this.sellKey = "";
    this.el.classList.remove("detview");
    market.selectFilter(0, -1);
    market.setTab(MarketPanelType.Market);
    this.lastSearch = this.ui.ctx.now();
    this.ui.openWindow("inventory", true);
    this.invalidate();
  }

  protected override closed(): void {
    // TrustMerchantDialog.Hide: listings and selection go, the sell cell unlocks.
    this.searchCancel?.(); this.searchCancel = null;
    this.historyCancel?.(); this.historyCancel = null;
    this.pending = null;
    this.m.market.reset();
    this.shownRows = [];
    this.ui.cells.refresh();
  }

  // — Tabs and searches —

  private enterTab(t: Tab): void {
    const market = this.m.market;
    this.tab = t;
    this.el.classList.remove("detview");
    if (t === "browse") {
      market.releaseSellItem(); // the desktop's Market tab unlocks the sell cell
      if (market.userMode) this.browseSearch();
    } else if (t === "mine" || t === "sell") {
      if (!market.userMode || market.marketType !== this.ownKind) this.queue(() => { market.setTab(this.ownKind); });
    }
    this.invalidate();
  }

  private setOwnKind(v: number): void {
    this.ownKind = v;
    this.mineSeg.set(v);
    this.sellSeg.set(v);
    this.priceIn.value = "";
    this.queue(() => { this.m.market.setTab(v); });
    this.invalidate();
  }

  /** Runs a search-type request (MarketSearch / SearchEx / Page / Refresh) at least SEARCH_GAP after the last one; a newer
   *  one replaces a waiting one. `run` returns false when the model refused it. */
  private queue(run: () => boolean | void): void {
    this.searchCancel?.();
    this.searchCancel = null;
    const go = (): void => {
      this.searchCancel = null;
      if (!this.open || !this.m.market.open) return;
      if (run() !== false) this.lastSearch = this.ui.ctx.now();
    };
    const wait = this.lastSearch + SEARCH_GAP - this.ui.ctx.now();
    if (wait > 0) this.searchCancel = later(go, wait, this.signal); else go();
  }

  /** The selected category (a sub-filter, or a top filter without subs; null = a top filter that only expands). */
  private target(): MarketFilter | null {
    const market = this.m.market;
    const top = MARKET_FILTERS.find((f) => f.index === market.filterIndex) ?? MARKET_FILTERS[0]!;
    if (!top.sub.length) return top;
    return top.sub.find((s) => s.index === market.subFilterIndex) ?? null;
  }

  /** C.MarketSearchEx with the category, name, order, price range and grade (the reply is a normal S.NPCMarket). */
  private browseSearch(): void {
    const t = this.target() ?? MARKET_FILTERS[0]!;
    this.queue(() => this.m.market.searchEx({
      match: this.query, type: t.type ?? 0, minShape: t.minShape, maxShape: t.maxShape, usermode: false, marketType: MarketPanelType.Market,
      sort: this.sort, priceMin: this.priceMin, priceMax: this.priceMax, minGrade: this.minGrade,
    }));
  }

  private findClick(): void {
    this.query = this.searchInput.value.trim().replace(/\s+/g, "");
    this.browseSearch();
  }

  private applyPrice(): void {
    const lo = parseAmount(this.minIn.value), hi = parseAmount(this.maxIn.value);
    this.priceMin = Number.isFinite(lo) ? clamp(lo, 0, UINT_MAX) : 0;
    this.priceMax = Number.isFinite(hi) ? clamp(hi, 0, UINT_MAX) : 0;
    if (this.priceMax && this.priceMax < this.priceMin) [this.priceMin, this.priceMax] = [this.priceMax, this.priceMin];
    this.minIn.value = this.priceMin ? fmt(this.priceMin) : "";
    this.maxIn.value = this.priceMax ? fmt(this.priceMax) : "";
    this.browseSearch();
  }

  /** A category click (TrustMerchantDialog filter buttons): expand; search when it has a type. */
  private catClick(item: MarketFilter, sub: MarketFilter | null): void {
    this.m.market.selectFilter(item.index, sub ? sub.index : -1);
    this.catsKey = "";
    if (!sub && item.sub.length) { this.invalidate(); return; }
    this.browseSearch();
    this.invalidate();
  }

  /** Searches the visible view again (after an answer, or the Refresh button). */
  private refreshView(): void {
    if (this.tab === "mine" || this.tab === "sell") this.queue(() => { this.m.market.setTab(this.ownKind); });
    else this.browseSearch();
  }

  private pager(): { el: HTMLElement; text: HTMLElement; note: HTMLElement; prev: Btn; next: Btn } {
    const text = h("span"), note = h("span");
    const prev = button("", () => this.m.market.back(), { icon: "chevronLeft", title: "Previous page" });
    const next = button("", () => this.nextPage(), { icon: "chevronRight", title: "Next page" });
    return { el: h("div", "pager", note, h("span", "sp"), prev.el, text, next.el), text, note, prev, next };
  }

  /** NextButton: a cached page at once, else C.MarketPage (a search-type request). */
  private nextPage(): void {
    const market = this.m.market;
    if (market.page >= market.pageCount - 1) return;
    if (market.page < Math.trunc((market.listings.length - 1) / MARKET_PAGE_ROWS)) market.next();
    else this.queue(() => { market.next(); });
  }

  // — Actions (through the Market model; nothing changes until the server answers) —

  private busy(l: ClientAuction | null): boolean {
    return !!l && this.pending === l.AuctionID && !this.m.market.ready;
  }

  /** MarketTime / the market page: false (and a note) when an action cannot go now. */
  private canAct(): boolean {
    const market = this.m.market;
    if (!market.open) { this.say("Speak to a trust merchant first.", true); return false; }
    if (!market.ready) { this.say("Wait a moment: the trust merchant is still answering.", true); return false; }
    return true;
  }

  /** Marks the listing as waiting and re-renders when MarketTime has passed (if no answer came). */
  private wait(l: ClientAuction): void {
    this.pending = l.AuctionID;
    this.ui.cells.refresh();
    later(() => { this.ui.cells.refresh(); this.invalidate(); }, MARKET_WAIT, this.signal);
    this.invalidate();
  }

  /** The listing is still the loaded one (a new page replaces the objects). */
  private live(l: ClientAuction): boolean {
    return this.m.market.listings.includes(l);
  }

  private async buyNow(): Promise<void> {
    const market = this.m.market, l = market.selected;
    if (!l || market.userMode || l.ItemType !== MarketItemType.Consign || !this.canAct()) return;
    const ok = await this.ui.rt.popups.ask(`Are you sure you want to buy ${this.name(l.Item)} for ${fmt(l.Price)} Gold?`,
      { title: "Buy now", kind: "question", gold: l.Price, item: this.popItem(l.Item), okCancel: true });
    if (!ok || !this.open) return;
    if (!this.live(l)) { this.say("That listing changed: pick it again.", true); return; }
    if (!this.canAct()) return;
    market.buy(l, 0);
    this.wait(l);
  }

  private bidFloor(l: ClientAuction): number { return Math.min(UINT_MAX, l.Price + 1); }

  private quickBid(pct: number): void {
    const l = this.m.market.selected;
    if (!l) return;
    const v = Math.min(UINT_MAX, Math.max(this.bidFloor(l), Math.ceil(l.Price * (1 + pct / 100) / 100) * 100));
    this.bidIn.value = fmt(v);
    this.bidInput();
  }

  private bidInput(): void {
    const l = this.m.market.selected, v = parseAmount(this.bidIn.value);
    this.bidIn.classList.toggle("bad", !!this.bidIn.value && (!l || !Number.isFinite(v) || v < this.bidFloor(l)));
    this.detKey = "";
    this.invalidate();
  }

  private async placeBid(): Promise<void> {
    const market = this.m.market, l = market.selected;
    if (!l || market.userMode || l.ItemType !== MarketItemType.Auction) return;
    const v = parseAmount(this.bidIn.value), min = this.bidFloor(l);
    if (!Number.isFinite(v) || v < min || v > UINT_MAX) { this.say(`Your bid must be at least ${fmt(min)} gold.`, true); return; }
    if (v > this.m.inventory.gold) { this.say("Not enough gold.", true); return; }
    if (!this.canAct()) return;
    const ok = await this.ui.rt.popups.ask(`Are you sure you want to bid ${fmt(v)} Gold for ${this.name(l.Item)}?`,
      { title: "Place bid", kind: "question", gold: v, item: this.popItem(l.Item), okCancel: true });
    if (!ok || !this.open) return;
    if (!this.live(l)) { this.say("That listing changed: pick it again.", true); return; }
    if (!this.canAct()) return;
    market.buy(l, v);
    this.bidIn.value = "";
    this.wait(l);
  }

  /** BuyButton in user mode: C.MarketGetBack (an unsold listing asks first). Sold = collect the gold. */
  private async getBack(l: ClientAuction): Promise<void> {
    const market = this.m.market;
    if (!market.userMode || !this.canAct()) return;
    const name = this.name(l.Item);
    if (l.Seller === "For Sale" || l.Seller === "No Bid") {
      if (!await this.ui.rt.popups.ask(`${name} has not sold, Are you sure you want to get it back?`, { title: "Cancel listing", kind: "question", item: this.popItem(l.Item), okCancel: true })) return;
    } else if (l.Seller === "Bid Met") {
      if (!await this.ui.rt.popups.ask(`${name} has a bid of ${fmt(l.Price)} gold. Take it back anyway? The bidder is refunded by mail.`,
        { title: "Cancel auction", kind: "warning", item: this.popItem(l.Item), okCancel: true })) return;
    }
    if (!this.open || !this.live(l) || !this.canAct()) return;
    market.getBack(l);
    this.wait(l);
  }

  /** SellNowButton (an auction with a bid: "Bid Met"). */
  private async sellNow(l: ClientAuction): Promise<void> {
    const market = this.m.market;
    if (!market.userMode || l.Seller !== "Bid Met" || !this.canAct()) return;
    const ok = await this.ui.rt.popups.ask(`Sell ${this.name(l.Item)} now for the current bid of ${fmt(l.Price)} gold? You receive ${fmt(net(l.Price))} gold after the commission.`,
      { title: "Sell now", kind: "question", gold: l.Price, item: this.popItem(l.Item), okCancel: true });
    if (!ok || !this.open || !this.live(l) || !this.canAct()) return;
    market.select(l);
    market.sellNow();
    this.wait(l);
  }

  /** CollectSoldButton: every sold listing's gold (C.MarketGetBack Mode Sold), then a refresh. */
  private collectSold(): void {
    const market = this.m.market;
    if (!market.userMode || !this.canAct()) return;
    this.queue(() => { market.collectSold(); });
  }

  /** S.MarketSuccess / S.MarketFail (a MirMessageBox on the desktop): failures pop up, successes show in the top bar;
   *  both go to Messages; the view is searched again so sold / taken rows go. */
  private answer(text: string): void {
    const fail = Object.values(MARKET_FAIL_TEXT).includes(text);
    this.pending = null;
    this.ui.cells.refresh();
    const line = h("div", fail ? "err" : "", h("small", "", fmtTimeOfDay()), text);
    this.logList.prepend(line);
    while (this.logList.childElementCount > LOG_MAX) this.logList.lastElementChild?.remove();
    if (fail) void this.ui.rt.popups.confirm({ title: "Trust merchant", text, kind: "warning", buttons: "ok" });
    else this.say(text, false);
    if (this.open && this.m.market.open) this.refreshView();
    this.invalidate();
  }

  private say(text: string, err: boolean): void {
    setText(this.msgEl, text);
    this.msgEl.classList.toggle("bad", err);
    this.msgEl.classList.toggle("ok", !err);
    const note = this.tab === "sell" ? this.sellNote : this.tab === "browse" || this.tab === "watch" ? this.detNote : this.mineNote;
    setText(note, text);
    note.classList.toggle("err", err);
  }

  private select(l: ClientAuction): void {
    const market = this.m.market;
    if (market.selected !== l) { this.bidIn.value = ""; this.bidIn.classList.remove("bad"); setText(this.detNote, ""); }
    market.select(l);
    this.el.classList.add("detview");
    this.askHistory(l.Item.ItemIndex);
  }

  /** C.MarketPriceHistory once per item while open (the model allows one a second: a refused ask retries). */
  private askHistory(itemIndex: number): void {
    const market = this.m.market;
    if (this.asked.has(itemIndex) || !market.open) return;
    if (market.requestPriceHistory(itemIndex)) { this.asked.add(itemIndex); return; }
    this.historyCancel?.();
    this.historyCancel = later(() => { this.historyCancel = null; if (this.open) this.askHistory(itemIndex); }, PRICE_HISTORY_DELAY, this.signal);
  }

  // — Sell —

  /** NPCDropDialog Consign's checks (DontStore / DontSell), a locked cell. "" = fine. */
  private sellRefusal(item: UserItem): string {
    const info = this.m.itemInfos.get(item.ItemIndex);
    if (info && ((info.Bind & BindMode.DontStore) !== 0 || (info.Bind & BindMode.DontSell) !== 0)) return "Cannot consign this item.";
    if (this.m.inventory.isLocked(item)) return "Waiting for the server";
    return "";
  }

  /** ItemCell_Click: the bag item becomes the sell item (Market.setSellItem locks its cell). */
  private pickSell(item: UserItem): void {
    const market = this.m.market;
    if (market.marketType !== this.ownKind || !market.userMode) this.queue(() => { market.setTab(this.ownKind); });
    market.setSellItem(item);
    this.priceIn.value = "";
    this.sellKey = "";
    setText(this.sellNote, "");
    this.askHistory(item.ItemIndex);
    this.invalidate();
    this.priceIn.focus({ preventScroll: true });
  }

  private limits(): [number, number, number] {
    return this.ownKind === MarketPanelType.Auction ? [MIN_STARTING_BID, MAX_STARTING_BID, AUCTION_COST] : [MIN_CONSIGNMENT, MAX_CONSIGNMENT, CONSIGNMENT_COST];
  }

  private suggested(): number | null {
    const item = this.m.market.sellItem;
    const st = item ? saleStats(this.m.market.priceHistory(item.ItemIndex)) : null;
    if (!st) return null;
    const [lo, hi] = this.limits();
    return clamp(round100(st.median), lo, hi);
  }

  private useSuggested(): void {
    const s = this.suggested();
    if (s === null) return;
    this.priceIn.value = fmt(s);
    this.invalidate();
  }

  /** SellItemButton: the limits, the fee, a confirm, then C.ConsignItem (Market.sell; the cell stays locked until
   *  S.ConsignItem), then the user's listings again. */
  private async listItem(): Promise<void> {
    const market = this.m.market, item = market.sellItem;
    if (!item) { this.say("Pick an item from your bag first.", true); return; }
    const [lo, hi, fee] = this.limits(), v = parseAmount(this.priceIn.value);
    if (!Number.isFinite(v) || v < lo || v > hi) { this.say(`The ${this.ownKind === MarketPanelType.Auction ? "starting bid" : "price"} must be ${fmt(lo)} – ${fmt(hi)} gold.`, true); return; }
    if (this.m.inventory.gold < fee) { this.say(`You need ${fmt(fee)} gold for the listing fee.`, true); return; }
    if (!market.open) { this.say("Speak to a trust merchant first.", true); return; }
    const auction = this.ownKind === MarketPanelType.Auction;
    const ok = await this.ui.rt.popups.ask(
      `${auction ? "Auction" : "List"} ${this.name(item)} ${auction ? "with a starting bid of" : "for"} ${fmt(v)} gold?\nThe listing fee is ${fmt(fee)} gold; ${COMMISSION * 100}% of the sale goes to the trust merchant.`,
      { title: auction ? "Start auction" : "Consign item", kind: "question", gold: fee, item: this.popItem(item), okCancel: true });
    if (!ok || !this.open || market.sellItem !== item) return;
    if (market.marketType !== this.ownKind) market.setTab(this.ownKind); // Market.sell sends the panel's type
    market.sell(v);
    this.priceIn.value = "";
    this.say(`${this.name(item)} sent to the trust merchant.`, false);
    this.queue(() => { market.setTab(this.ownKind); });
    this.invalidate();
  }

  // — Watchlist (this browser only) —

  private loadWatch(): void {
    try {
      const raw = localStorage.getItem(WATCH_KEY);
      const list = raw ? JSON.parse(raw) as WatchEntry[] : [];
      if (Array.isArray(list)) for (const e of list) if (e && typeof e.id === "string" && typeof e.item === "number") this.watch.set(e.id, e);
    } catch { /* storage blocked or junk: start empty */ }
  }

  private saveWatch(): void {
    try { localStorage.setItem(WATCH_KEY, JSON.stringify([...this.watch.values()])); } catch { /* storage blocked */ }
  }

  private toggleWatch(l: ClientAuction): void {
    const id = String(l.AuctionID);
    if (!this.watch.delete(id)) {
      if (this.watch.size >= WATCH_MAX) { this.say(`You can watch up to ${WATCH_MAX} listings.`, true); return; }
      const info = this.m.itemInfos.get(l.Item.ItemIndex);
      this.watch.set(id, {
        id, item: l.Item.ItemIndex, name: info ? itemFriendlyName(info) : `Item #${l.Item.ItemIndex}`, count: l.Item.Count,
        price: l.Price, type: l.ItemType, seller: l.Seller, date: String(l.ConsignmentDate), added: Date.now(),
      });
    }
    this.saveWatch();
    this.watchKey = "";
    this.invalidate();
  }

  private findWatched(e: WatchEntry): void {
    const info = this.m.itemInfos.get(e.item);
    this.query = (info ? info.Name : e.name).replace(/\s+/g, "");
    this.searchInput.value = this.query;
    this.m.market.selectFilter(0, -1);
    this.catsKey = "";
    this.tab = "browse";
    this.topTabs.select(0);
    this.browseSearch();
    this.invalidate();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const market = this.m.market;
    setText(this.goldEl, fmt(this.m.inventory.gold));
    for (const t of TABS) this.views[t].hidden = t !== this.tab;
    // One detail pane, shared by Browse and Watchlist: it lives in the active one.
    if ((this.tab === "browse" || this.tab === "watch") && this.det.parentElement !== this.views[this.tab]) this.views[this.tab].append(this.det);
    this.topTabs.badge(3, this.watch.size);
    let ended = 0;
    if (market.userMode) for (const l of market.listings) if (l.Seller === "Sold" || l.Seller === "Expired") ended++;
    this.topTabs.badge(1, ended);
    const ctx = this.ui.labelContext();
    switch (this.tab) {
      case "browse": this.renderCats(); this.renderBrowse(ctx); this.renderDetail(); break;
      case "mine": this.renderMine(); break;
      case "sell": this.renderSell(); break;
      case "watch": this.renderWatch(); this.renderDetail(); break;
      case "log": break;
    }
    this.ui.cells.refresh();
  }

  private renderCats(): void {
    const market = this.m.market;
    const key = `${market.filterIndex}|${market.subFilterIndex}`;
    if (key === this.catsKey) return;
    this.catsKey = key;
    const btns: HTMLElement[] = [];
    const btn = (f: MarketFilter, sub: MarketFilter | null, parent: MarketFilter): void => {
      const on = sub ? sub.index === market.subFilterIndex : f.index === market.filterIndex && (f.sub.length === 0 || market.subFilterIndex < 0);
      const b = h("button", "ct" + (sub ? " sub" : "") + (on ? " on" : ""), (sub ?? f).title + (!sub && f.sub.length ? (f.index === market.filterIndex ? " ▾" : " ▸") : ""));
      b.type = "button";
      b.addEventListener("click", () => this.catClick(parent, sub));
      btns.push(b);
    };
    for (const f of MARKET_FILTERS) {
      btn(f, null, f);
      if (f.index === market.filterIndex) for (const s of f.sub) btn(f, s, f);
    }
    this.catList.replaceChildren(...btns);
  }

  /** Whether the user meets the item's class, gender and level / stat requirement (the item label's red lines). */
  private usable(item: UserItem, ctx: ItemLabelContext): boolean {
    const base = this.m.itemInfos.get(item.ItemIndex);
    if (!base) return true;
    const info = ctx.realItem(base);
    if (info.RequiredClass !== RequiredClass.None && (info.RequiredClass & (1 << ctx.job)) === 0) return false;
    if (info.RequiredGender !== RequiredGender.None && (info.RequiredGender & (1 << ctx.gender)) === 0) return false;
    const a = info.RequiredAmount;
    if (a <= 0) return true;
    if (info.RequiredType === RequiredType.Level) return ctx.level >= a;
    if (info.RequiredType === RequiredType.MaxLevel) return ctx.level <= a;
    const s = REQ_STAT[info.RequiredType];
    return s === undefined || ctx.stat(s) >= a;
  }

  private renderBrowse(ctx: ItemLabelContext): void {
    const market = this.m.market, now = Date.now();
    const loaded = !market.userMode;
    const page = loaded ? market.pageRows() : [];
    const rows: ClientAuction[] = [];
    for (const l of page) {
      if (this.kind === "consign" && l.ItemType !== MarketItemType.Consign) continue;
      if (this.kind === "auction" && l.ItemType !== MarketItemType.Auction) continue;
      if (this.usableOnly && !this.usable(l.Item, ctx)) continue;
      rows.push(l);
    }
    this.shownRows = rows;
    const sel = market.selected;
    this.browseRows.forEach((r, i) => {
      const l = rows[i] ?? null;
      r.listing = l;
      r.el.hidden = !l;
      if (!l) return;
      const info = this.m.itemInfos.get(l.Item.ItemIndex);
      r.el.classList.toggle("sel", l === sel);
      setText(r.name, this.name(l.Item));
      setStyle(r.name, "color", gradeVar(info?.Grade ?? 0));
      setText(r.seller, `by ${l.Seller}`);
      r.star.classList.toggle("on", this.watch.has(String(l.AuctionID)));
      const real = info ? ctx.realItem(info) : undefined;
      const lvReq = real && real.RequiredType === RequiredType.Level && real.RequiredAmount > 0 ? real.RequiredAmount : 0;
      setText(r.lv, lvReq ? String(lvReq) : "—");
      r.lv.classList.toggle("no", !this.usable(l.Item, ctx));
      const auction = l.ItemType === MarketItemType.Auction;
      setText(r.kind, auction ? "AUCTION" : "BUY NOW");
      r.kind.className = "kd " + (auction ? "a" : "c");
      this.timeCell(r.time, l, now);
      setText(r.price, fmt(l.Price));
      setText(r.priceSub, auction ? "Bid to beat" : l.Item.Count > 1 ? `${fmt(l.Price / l.Item.Count)} each` : "Buy now");
    });
    const hiddenHere = page.length - rows.length;
    this.browseNone.hidden = rows.length > 0;
    setText(this.browseNone, !loaded ? "Loading…" : page.length ? "Nothing on this page matches the page filters." : "No listings match. Try another category or filter.");
    this.renderPager(this.browsePager, loaded, hiddenHere > 0 ? `${hiddenHere} on this page hidden by the page filters` : "");
  }

  private renderPager(p: { text: HTMLElement; note: HTMLElement; prev: Btn; next: Btn }, loaded: boolean, note: string): void {
    const market = this.m.market;
    setText(p.text, loaded && market.pageCount ? `${market.page + 1} / ${market.pageCount}` : "–");
    setText(p.note, note);
    p.prev.setDisabled(!loaded || market.page <= 0);
    p.next.setDisabled(!loaded || market.page >= market.pageCount - 1);
  }

  private timeCell(el: HTMLElement, l: ClientAuction, now: number): void {
    const left = endsAt(l) - now;
    setText(el, left > 0 ? fmtDuration(left / 1000) : "Ended");
    el.classList.toggle("soon", left > 0 && left < 3_600_000);
    el.dataset.ends = String(endsAt(l));
  }

  /** The countdowns of the visible rows / detail (every second while shown; setText writes only on change). */
  private tickTimes(): void {
    const now = Date.now();
    for (const el of this.el.querySelectorAll<HTMLElement>(".view:not([hidden]) [data-ends]")) {
      const left = Number(el.dataset.ends) - now;
      setText(el, left > 0 ? fmtDuration(left / 1000) : "Ended");
    }
  }

  private renderDetail(): void {
    const market = this.m.market, l = this.tab === "browse" || this.tab === "watch" ? market.selected : null;
    this.detEmpty.hidden = !!l;
    this.detBody.hidden = !l;
    if (!l) { this.el.classList.remove("detview"); return; }
    const info = this.m.itemInfos.get(l.Item.ItemIndex);
    const gold = this.m.inventory.gold, ready = market.ready, busy = this.busy(l);
    const auction = l.ItemType === MarketItemType.Auction;
    const key = `${l.AuctionID}|${l.Price}|${gold}|${ready}|${busy}|${info?.Index ?? -1}|${this.bidIn.value}|${this.watch.has(String(l.AuctionID))}|${market.userMode}`;
    this.renderSpark(l.Item.ItemIndex);
    if (key === this.detKey) return;
    this.detKey = key;
    setText(this.detName, this.name(l.Item));
    setStyle(this.detName, "color", gradeVar(info?.Grade ?? 0));
    setText(this.detSub, `${info && info.Grade ? gradeName(info.Grade) + " · " : ""}${auction ? "Auction" : "Buy now"} · seller ${l.Seller}`);
    this.buyBox.hidden = auction;
    this.bidBox.hidden = !auction;
    const waitWhy = busy ? "Waiting for the trust merchant" : !ready ? "The trust merchant is still answering" : market.userMode ? "Switch to Browse" : "";
    if (!auction) {
      setText(this.buyBig, fmt(l.Price));
      this.timeCell(this.buyTime, l, Date.now());
      const after = gold - l.Price;
      setText(this.buyAfter, fmt(after));
      this.buyAfter.className = after < 0 ? "r" : "ok";
      this.buyBtn.setLabel(after < 0 ? "Not enough gold" : busy ? "Buying…" : "Buy now");
      this.buyBtn.setDisabled(after < 0 || !!waitWhy, after < 0 ? "Not enough gold" : waitWhy);
    } else {
      setText(this.bidBig, fmt(l.Price));
      this.timeCell(this.bidTime, l, Date.now());
      const min = this.bidFloor(l), v = parseAmount(this.bidIn.value);
      setText(this.bidMin, `Price to beat ${fmt(l.Price)} · your bid: ${fmt(min)} or more`);
      this.bidIn.placeholder = `${fmt(min)} or more`;
      const bad = !Number.isFinite(v) || v < min;
      const poor = !bad && v > gold;
      this.bidBtn.setLabel(busy ? "Bidding…" : poor ? "Not enough gold" : "Place bid");
      this.bidBtn.setDisabled(bad || poor || !!waitWhy, bad ? `Enter at least ${fmt(min)}` : poor ? "Not enough gold" : waitWhy);
    }
    const watched = this.watch.has(String(l.AuctionID));
    this.watchBtn.setLabel(watched ? "Watching" : "Watch");
    this.watchBtn.el.classList.toggle("main", watched);
  }

  /** The 14-sale sparkline and its figures for `itemIndex` (S.MarketPriceHistory; rewritten only when the sales change). */
  private renderSpark(itemIndex: number): void {
    const sales = this.m.market.priceHistory(itemIndex);
    const key = `${itemIndex}|${sales ? sales.length + ":" + sales.map((s) => s.Price).join(",") : "?"}`;
    if (key === this.sparkKey) return;
    this.sparkKey = key;
    const st = saleStats(sales);
    setText(this.histTitle, st ? `Recent sales (${st.n})` : "Recent sales");
    setText(this.histAvg, st ? `avg ${fmt(st.avg)}` : "");
    setText(this.histLo, st ? `low ${fmt(st.lo)}` : "");
    setText(this.histHi, st ? `high ${fmt(st.hi)}` : "");
    setText(this.histNote, sales === undefined ? "Asking the trust merchant…" : st ? `Last sale ${fmt(st.last)} gold.` : "No sales of this item recorded yet.");
    this.spark.style.display = st && st.n > 1 ? "" : "none";
    if (!st || !sales || st.n < 2) return;
    const W = 266, H = 54, n = sales.length;
    const x = (i: number) => i / (n - 1) * (W - 6) + 3;
    const y = (p: number) => H - 4 - (p - st.lo) / Math.max(1, st.hi - st.lo) * (H - 10);
    let line = "";
    sales.forEach((s, i) => { line += `${i ? "L" : "M"}${x(i).toFixed(1)},${y(s.Price).toFixed(1)}`; });
    this.sparkLine.setAttribute("d", line);
    this.sparkArea.setAttribute("d", `${line}L${x(n - 1).toFixed(1)},${H}L${x(0).toFixed(1)},${H}Z`);
    this.sparkDot.setAttribute("cx", x(n - 1).toFixed(1));
    this.sparkDot.setAttribute("cy", y(sales[n - 1]!.Price).toFixed(1));
  }

  private renderMine(): void {
    const market = this.m.market, now = Date.now();
    const loaded = market.userMode && market.marketType === this.ownKind;
    const rows = loaded ? market.pageRows() : [];
    this.shownRows = rows.slice();
    let sold = 0;
    if (loaded) for (const l of market.listings) if (l.Seller === "Sold") sold++;
    this.collectBtn.setLabel(sold ? `Collect sold (${sold})` : "Collect sold");
    this.collectBtn.setDisabled(!loaded || !sold || !market.ready, !loaded ? "Loading" : !sold ? "Nothing sold on the loaded pages" : "The trust merchant is still answering");
    this.mineRows.forEach((r, i) => {
      const l = rows[i] ?? null;
      r.listing = l;
      r.el.hidden = !l;
      if (!l) return;
      const info = this.m.itemInfos.get(l.Item.ItemIndex);
      const auction = l.ItemType === MarketItemType.Auction;
      setText(r.name, this.name(l.Item));
      setStyle(r.name, "color", gradeVar(info?.Grade ?? 0));
      const st = STATUS[l.Seller] ?? { text: l.Seller, cls: "active" };
      setText(r.line, auction ? (l.Seller === "Bid Met" ? `Auction · highest bid ${fmt(l.Price)}` : `Auction · starting bid ${fmt(l.Price)}`) : `Buy now ${fmt(l.Price)}`);
      setText(r.status, st.text);
      r.status.className = "st " + st.cls;
      if (st.cls === "active" || st.cls === "bid") this.timeCell(r.time, l, now);
      else { setText(r.time, ""); delete r.time.dataset.ends; }
      const busy = this.busy(l);
      const key = `${l.AuctionID}|${l.Seller}|${l.Price}|${busy}|${market.ready}`;
      if (key === r.actsKey) return;
      r.actsKey = key;
      const acts: HTMLElement[] = [];
      const add = (label: string, fn: () => void, cls: string, icon: IconName) => {
        const b = button(label, fn, { cls, icon });
        if (busy || !market.ready) b.setDisabled(true, busy ? "Waiting for the trust merchant" : "The trust merchant is still answering");
        acts.push(b.el);
      };
      if (l.Seller === "Sold") add(`Collect ${fmt(net(l.Price))}`, () => void this.getBack(l), "main", "coinGold");
      else if (l.Seller === "Expired") add("Take back", () => void this.getBack(l), "", "bag");
      else {
        if (l.Seller === "Bid Met") add(`Sell now ${fmt(l.Price)}`, () => void this.sellNow(l), "main", "coin");
        add("Cancel", () => void this.getBack(l), "", "cross");
      }
      r.acts.replaceChildren(...acts);
    });
    this.mineNone.hidden = rows.length > 0;
    setText(this.mineNone, !loaded ? "Loading…" : `You have no ${this.ownKind === MarketPanelType.Auction ? "auctions" : "buy-now listings"}. Use the Sell tab.`);
    this.renderPager(this.minePager, loaded, `Sold listings pay the price less ${COMMISSION * 100}% commission · expired items come back to your bag.`);
  }

  private renderSell(): void {
    const market = this.m.market, inv = this.m.inventory;
    // The side bag (cells rebuilt only when the bag size changed)
    const n = Math.max(0, inv.inventory.length - BELT_IDX);
    if (this.bagCells.length !== n) {
      for (const c of this.bagCells) c.dispose();
      this.bagCells = [];
      for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 40, signal: this.signal }));
      this.sellBag.replaceChildren(...this.bagCells.map((c) => c.el));
    }
    const item = market.sellItem, [lo, hi, fee] = this.limits(), auction = this.ownKind === MarketPanelType.Auction;
    const info = item ? this.m.itemInfos.get(item.ItemIndex) : undefined;
    const sales = item ? market.priceHistory(item.ItemIndex) : undefined, st = saleStats(sales);
    const v = parseAmount(this.priceIn.value), valid = Number.isFinite(v) && v >= lo && v <= hi;
    const key = `${item?.UniqueID ?? ""}|${info?.Index ?? -1}|${this.ownKind}|${st ? st.n + ":" + st.median : sales === undefined ? "?" : "0"}|${this.priceIn.value}|${inv.gold >= fee}|${market.open}`;
    if (key === this.sellKey) return;
    this.sellKey = key;
    setText(this.sellName, item ? this.name(item) : "No item");
    setStyle(this.sellName, "color", gradeVar(info?.Grade ?? 0));
    setText(this.sellSub, item ? "Right-click the cell to put it back." : "Click an item in your bag, or drag it here.");
    setText(this.sellHist, !item ? "" : sales === undefined ? "Asking for recent sales…" : st
      ? `Recent sales (${st.n}): median ${fmt(st.median)}, low ${fmt(st.lo)}, high ${fmt(st.hi)} gold.`
      : "No sales of this item recorded yet.");
    const sug = this.suggested();
    this.useBtn.setLabel(sug === null ? "No suggestion" : `Use ${fmt(sug)}`);
    this.useBtn.setDisabled(sug === null, "No recent sales to suggest from");
    setText(this.priceLbl, auction ? "Starting bid" : "Price");
    this.priceIn.placeholder = `${fmt(lo)} – ${fmt(hi)}`;
    this.priceIn.classList.toggle("bad", !!this.priceIn.value && !valid);
    const p = valid ? v : 0;
    setText(this.feeEl, fmt(fee));
    setText(this.commEl, fmt(p * COMMISSION));
    setText(this.recvLbl, auction ? "You receive at the starting bid" : "You receive");
    setText(this.recvEl, fmt(net(p)));
    const why = !market.open ? "Speak to a trust merchant first." : !item ? "Pick an item first" : !valid ? `Enter ${fmt(lo)} – ${fmt(hi)}` : inv.gold < fee ? `You need ${fmt(fee)} gold for the fee` : "";
    this.listBtn.setLabel(auction ? `Start auction (${fmt(fee)} gold)` : `List for ${fmt(fee)} gold`);
    this.listBtn.setDisabled(!!why, why);
    setText(this.sellRules, auction
      ? `Auction fee ${fmt(AUCTION_COST)} gold · starting bid ${fmt(MIN_STARTING_BID)} – ${fmt(MAX_STARTING_BID)} gold · ${CONSIGNMENT_LENGTH} days, then the highest bidder gets the item by mail · ${COMMISSION * 100}% of the final bid goes to the trust merchant.`
      : `Consignment fee ${fmt(CONSIGNMENT_COST)} gold · price ${fmt(MIN_CONSIGNMENT)} – ${fmt(MAX_CONSIGNMENT)} gold · listed for ${CONSIGNMENT_LENGTH} days · ${COMMISSION * 100}% of the sale goes to the trust merchant.`);
  }

  private renderWatch(): void {
    const market = this.m.market;
    const live = new Map<string, ClientAuction>();
    if (!market.userMode) for (const l of market.listings) live.set(String(l.AuctionID), l);
    const entries = [...this.watch.values()].sort((a, b) => b.added - a.added);
    const key = entries.map((e) => `${e.id}:${live.get(e.id)?.Price ?? "-"}`).join(",") + `|${market.selected?.AuctionID ?? ""}`;
    if (key === this.watchKey) return;
    this.watchKey = key;
    const now = Date.now();
    const rows: HTMLElement[] = [];
    for (const e of entries) {
      const info = this.m.itemInfos.get(e.item);
      const l = live.get(e.id);
      const name = h("b", "", (info ? itemFriendlyName(info) : e.name) + (e.count > 1 ? ` (${e.count})` : ""));
      setStyle(name, "color", gradeVar(info?.Grade ?? 0));
      let ends = 0;
      try { ends = endsAt({ ConsignmentDate: BigInt(e.date) }); } catch { ends = 0; }
      const left = ends - now;
      const state = l ? h("span", "live", `Listed · ${fmt(l.Price)}`) : h("span", "sub2", `Seen at ${fmt(e.price)} · not on this page`);
      const acts = h("div", "acts");
      if (l) acts.append(button("Show", () => this.select(l), { cls: "main", icon: "eye" }).el);
      else acts.append(button("Find", () => this.findWatched(e), { icon: "search", title: "Search the market for this item" }).el);
      acts.append(button("", () => { this.watch.delete(e.id); this.saveWatch(); this.watchKey = ""; this.invalidate(); }, { icon: "trash", title: "Stop watching" }).el);
      rows.push(h("div", "wl",
        h("div", "", name, h("div", "sub2", `${e.type === MarketItemType.Auction ? "Auction" : "Buy now"} · by ${e.seller}`)),
        state, h("span", "tl", ends ? (left > 0 ? `${fmtDuration(left / 1000)} left` : "Ended") : ""), acts));
    }
    if (!rows.length) rows.push(h("div", "none", "Star a listing to watch it here. The list is kept in this browser."));
    else rows.unshift(h("div", "note", `${entries.length} ${plural(entries.length, "listing")} watched · live prices only for listings on the loaded page.`));
    this.watchList.replaceChildren(...rows);
  }

  // — Helpers —

  /** UserItem.FriendlyName: ItemInfo.FriendlyName and the count. */
  private name(item: UserItem): string {
    const info: ItemInfo | undefined = this.m.itemInfos.get(item.ItemIndex);
    const base = info ? itemFriendlyName(info) : `Item #${item.ItemIndex}`;
    return item.Count > 1 ? `${base} (${item.Count})` : base;
  }

  private popItem(item: UserItem): PopupItem | undefined {
    const info = this.m.itemInfos.get(item.ItemIndex);
    return info ? { name: itemFriendlyName(info), img: info.Image, count: item.Count } : undefined;
  }
}

registerWindow(TRUST_WINDOW, (host) => new TrustWindow(host), {
  binds: [],
  watch: (host, open) => {
    const m = host.ctx.models;
    const offs = [
      // GameScene.NPCMarket: Show() on a fresh open (later S.NPCMarket answers only refresh the rows)
      m.market.opened.on(() => { if (!host.rt.isOpen(TRUST_WINDOW)) open(true); }),
      // NPCDialog.Hide / GameScene.NPCResponse hide the TrustMerchantDialog
      m.npcSession.closed.on(() => { if (host.rt.isOpen(TRUST_WINDOW)) open(false); }),
      m.npcSession.page.on(() => { if (host.rt.isOpen(TRUST_WINDOW)) open(false); }),
    ];
    return () => { for (const off of offs) off(); };
  },
});
