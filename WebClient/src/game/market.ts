// The Trust Merchant's state: TrustMerchantDialog (Client/MirScenes/Dialogs/TrustMerchantDialog.cs) UserMode, MarketType,
// SearchTime / MarketTime throttles, the listings and their pages, Selected, the price sort, the category filter and the
// sell slot (SellItemSlot, its bag cell locked), plus GameScene's NPCMarket / NPCMarketPage / MarketFail / MarketSuccess
// handlers. The consign request itself is Inventory.consign. game.ts feeds handle() (true = consumed, like the old
// window); the UI follows the events: `opened` (NPCMarket: show the dialog; it then picks the Market tab with setTab),
// `changed` (redraw the rows), `sellChanged` (the sell cell) and `message` (the MirMessageBox of MarketFail /
// MarketSuccess). The UI calls reset() when the dialog hides (TrustMerchantDialog.Hide).

import type { ClientAuction, ClientMarketSale, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets, MarketSearchSort } from "../protocol/client.ts";
import { ChatType, ItemType, MarketCollectionMode, MarketPanelType } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { Emitter } from "./emitter.ts";

// Shared/Globals.cs
export const SEARCH_DELAY = 500;
export const CONSIGNMENT_LENGTH = 7;
export const CONSIGNMENT_COST = 5000;
export const MIN_CONSIGNMENT = 5000;
export const MAX_CONSIGNMENT = 50000000;
export const AUCTION_COST = 5000;
export const MIN_STARTING_BID = 0;
export const MAX_STARTING_BID = 50000;
export const UINT_MAX = 4294967295;
/** TrustMerchantDialog: rows a page (the server pages by 10 too). */
export const MARKET_PAGE_ROWS = 10;
/** MarketTime after a buy / bid / get back / sell now / collect (CMain.Time + 3000). */
const MARKET_DELAY = 3000;
/** PlayerObject.MarketPriceHistory's cooldown (the server drops faster requests silently). */
export const PRICE_HISTORY_DELAY = 1000;

export { MarketSearchSort };

/** C.MarketSearchEx (web, phase 2.6): C.MarketSearch's fields plus a server-side sort, price range and minimum grade.
 *  Omitted fields: Match "", Type ItemType.Nothing, Usermode false, shapes 0..5000, the current panel, no sort, any price
 *  (PriceMax 0 = no max), MinGrade 0 (ItemGrade.None). */
export interface MarketSearchExParams {
  match?: string; type?: number; usermode?: boolean; minShape?: number; maxShape?: number; marketType?: number;
  /** MarketSearchSort. */
  sort?: number; priceMin?: number; priceMax?: number; minGrade?: number;
}

const u32 = (v: number | undefined): number => Math.min(UINT_MAX, Math.max(0, Math.trunc(v ?? 0)));

/** GameScene.MarketFail reasons (ClientTextKeys English text). */
export const MARKET_FAIL_TEXT: Readonly<Record<number, string>> = {
  0: "You cannot use the TrustMerchant when dead.",
  1: "You cannot buy from the TrustMerchant without using.",
  2: "This item has already been sold.",
  3: "This item has Expired and cannot be brought.",
  4: "Not enough gold.",
  5: "You do not have enough weight or space spare to buy this item.",
  6: "You cannot buy your own items.",
  7: "You are too far away from the Trust Merchant.",
  8: "You cannot hold enough gold to get your sale.",
  9: "This item has not met the minimum bid yet.",
  10: "Auction has already ended for this item.",
};

/** TrustMerchantDialog.Filter. Type null = a category that only expands. */
export interface MarketFilter {
  readonly index: number; readonly title: string; readonly type: number | null;
  readonly minShape: number; readonly maxShape: number; readonly sub: readonly MarketFilter[];
}

function f(index: number, title: string, type: number | null, minShape = 0, maxShape = 32767, sub: MarketFilter[] = []): MarketFilter {
  return { index, title, type, minShape, maxShape, sub };
}

/** TrustMerchantDialog.SetupFilters. */
export const MARKET_FILTERS: readonly MarketFilter[] = [
  f(0, "Show All Items", ItemType.Nothing),
  f(1, "Weapon Items", ItemType.Weapon),
  f(2, "Drapery Items", null, 0, 32767, [
    f(201, "Armour", ItemType.Armour), f(202, "Helmet", ItemType.Helmet), f(203, "Belt", ItemType.Belt),
    f(204, "Boots", ItemType.Boots), f(205, "Stone", ItemType.Stone)]),
  f(3, "Accessory Items", null, 0, 32767, [
    f(301, "Necklaces", ItemType.Necklace), f(302, "Bracelets", ItemType.Bracelet), f(303, "Rings", ItemType.Ring)]),
  f(4, "Consumable Items", null, 0, 32767, [
    f(401, "Recovery Pots", ItemType.Potion, 0, 2), f(402, "Buff Pots", ItemType.Potion, 3, 4),
    f(403, "Scrolls / Oils", ItemType.Scroll), f(404, "Misc Items", ItemType.Script)]),
  f(5, "Enhancement", null, 0, 32767, [f(501, "Gems", ItemType.Potion, 3, 3), f(502, "Orbs", ItemType.Potion, 4, 4)]),
  f(6, "Books", null, 0, 32767, [
    f(601, "Warrior", ItemType.Book, 0, 30), f(602, "Wizard", ItemType.Book, 31, 60), f(603, "Taoist", ItemType.Book, 61, 90),
    f(604, "Assassin", ItemType.Book, 91, 120), f(605, "Archer", ItemType.Book, 121, 150)]),
  f(7, "Craft Items", null, 0, 32767, [
    f(701, "Materials", ItemType.CraftingMaterial), f(703, "Meat", ItemType.Meat), f(704, "Ore", ItemType.Ore)]),
];

/** MarketPriceFilter. */
export type PriceFilter = "normal" | "low" | "high";

export class Market {
  private _open = false;            // TrustMerchantDialog.Visible (NPCMarket .. reset)
  private _userMode = false;        // UserMode
  private _marketType: number = MarketPanelType.Market; // MarketType
  private searchTime = 0;           // SearchTime
  private marketTime = 0;           // MarketTime
  private _listings: ClientAuction[] = [];
  private _page = 0;
  private _pageCount = 0;
  private _selected: ClientAuction | null = null; // Selected.Listing
  private _priceFilter: PriceFilter = "normal";
  private _filterIndex = -1;        // SelectedIndex
  private _subFilterIndex = -1;     // SelectedSubIndex
  private _sellItem: UserItem | null = null; // SellItemSlot (tempCell locked)
  private readonly _priceHistory = new Map<number, readonly ClientMarketSale[]>(); // S.MarketPriceHistory per ItemIndex
  private priceHistoryTime = 0;     // PRICE_HISTORY_DELAY gate

  /** GameScene.NPCMarket: show the dialog (before the listings are applied; a fresh open picks the Market tab). */
  readonly opened = new Emitter<void>();
  /** The listings, page, selection, user mode or price sort changed: redraw. */
  readonly changed = new Emitter<void>();
  /** The sell slot changed. */
  readonly sellChanged = new Emitter<void>();
  /** GameScene.MarketFail / MarketSuccess: a MirMessageBox. */
  readonly message = new Emitter<{ text: string }>();
  /** S.MarketPriceHistory: `sales` (oldest first, at most 14) for `itemIndex` arrived; also cached (priceHistory). */
  readonly priceHistoryChanged = new Emitter<{ itemIndex: number; sales: readonly ClientMarketSale[] }>();

  constructor(private readonly deps: {
    send: (f: Uint8Array) => void; now: () => number; inventory: Inventory; chat: (text: string, type: number) => void;
  }) {}

  get open(): boolean { return this._open; }
  /** UserMode: the listings are the user's own (Consign / Auction tabs). */
  get userMode(): boolean { return this._userMode; }
  /** MarketType (MarketPanelType). */
  get marketType(): number { return this._marketType; }
  get listings(): readonly ClientAuction[] { return this._listings; }
  get page(): number { return this._page; }
  get pageCount(): number { return this._pageCount; }
  get selected(): ClientAuction | null { return this._selected; }
  get priceFilter(): PriceFilter { return this._priceFilter; }
  get filterIndex(): number { return this._filterIndex; }
  get subFilterIndex(): number { return this._subFilterIndex; }
  /** SellItemSlot. */
  get sellItem(): UserItem | null { return this._sellItem; }
  /** MarketTime has passed (the buy / mail / sell buttons act). */
  get ready(): boolean { return this.deps.now() >= this.marketTime; }

  /** TrustMerchantDialog.Hide: the listings and selection go, the sell slot unlocks. */
  reset(): void {
    this._open = false;
    this._listings = [];
    this._selected = null;
    this.releaseSellItem();
  }

  /** TrustMerchantDialog.TMerchantDialog: switch the panel and search the user's (Consign / Auction) or all (Market)
   *  listings. */
  setTab(type: number): void {
    this._marketType = type;
    this.deps.send(ClientPackets.MarketSearch({ Match: "", Type: ItemType.Nothing, Usermode: type !== MarketPanelType.Market, MinShape: 0, MaxShape: 5000, MarketType: type }));
  }

  /** DrawFilters' selection (SelectedIndex / SelectedSubIndex). */
  selectFilter(index: number, subIndex: number): void {
    this._filterIndex = index;
    this._subFilterIndex = subIndex;
  }

  /** The filter buttons' Click, after the selection (selectFilter): search when the filter has a type (`match` = the
   *  search box). */
  filterSearch(item: MarketFilter, sub: MarketFilter | null, match: string): void {
    const target = sub ?? (item.sub.length ? null : item);
    if (target && target.type !== null) {
      this.deps.send(ClientPackets.MarketSearch({ Match: match, Type: target.type, Usermode: false, MinShape: target.minShape, MaxShape: target.maxShape, MarketType: this._marketType }));
    }
  }

  /** SearchTime not yet passed: the "search again after" line, true. */
  private searchWait(): boolean {
    const t = this.deps.now();
    if (t >= this.searchTime) return false;
    this.deps.chat(`You can search again after ${Math.ceil((this.searchTime - t) / 1000)} seconds.`, ChatType.System);
    return true;
  }

  /** FindButton (empty text first) / SearchTextBox_KeyPress Enter (the wait first). */
  search(match: string, enter: boolean): void {
    if (!match && !enter) return;
    if (this.searchWait()) return;
    if (!match) return;
    this.searchTime = this.deps.now() + SEARCH_DELAY;
    this.deps.send(ClientPackets.MarketSearch({ Match: match, Type: ItemType.Nothing, Usermode: false, MinShape: 0, MaxShape: 5000, MarketType: this._marketType }));
  }

  /** RefreshButton: C.MarketRefresh unless SearchTime has not passed; true = sent (the UI clears its search box). */
  refresh(): boolean {
    if (this.searchWait()) return false;
    this.searchTime = this.deps.now() + SEARCH_DELAY;
    this.deps.send(ClientPackets.MarketRefresh());
    return true;
  }

  /** C.MarketSearchEx (web): MarketSearch with a server-side sort, price range and minimum grade, under the same SearchTime
   *  throttle as search(). The reply is the usual S.NPCMarket / S.NPCMarketPage (listings + paging as for MarketSearch);
   *  the server sorts, so keep priceFilter "normal" to show its order. False = throttled (chat line posted), nothing sent. */
  searchEx(params: MarketSearchExParams = {}): boolean {
    if (this.searchWait()) return false;
    this.searchTime = this.deps.now() + SEARCH_DELAY;
    const marketType = params.marketType ?? this._marketType;
    this.deps.send(ClientPackets.MarketSearchEx({
      Match: params.match ?? "", Type: params.type ?? ItemType.Nothing, Usermode: params.usermode ?? false,
      MinShape: params.minShape ?? 0, MaxShape: params.maxShape ?? 5000, MarketType: marketType,
      Sort: params.sort ?? MarketSearchSort.None, PriceMin: u32(params.priceMin), PriceMax: u32(params.priceMax),
      MinGrade: Math.min(255, Math.max(0, Math.trunc(params.minGrade ?? 0))),
    }));
    return true;
  }

  /** The last S.MarketPriceHistory received for `itemIndex` (oldest first), or undefined = never asked / not answered. */
  priceHistory(itemIndex: number): readonly ClientMarketSale[] | undefined { return this._priceHistory.get(itemIndex); }

  /** C.MarketPriceHistory (web): ask for the last 14 sale prices of ItemInfo `itemIndex` (the market page must be open on
   *  the server). One request per PRICE_HISTORY_DELAY; false = too soon, nothing sent. Answer: `priceHistoryChanged`. */
  requestPriceHistory(itemIndex: number): boolean {
    const t = this.deps.now();
    if (t < this.priceHistoryTime) return false;
    this.priceHistoryTime = t + PRICE_HISTORY_DELAY;
    this.deps.send(ClientPackets.MarketPriceHistory({ ItemIndex: itemIndex }));
    return true;
  }

  /** BackButton. */
  back(): void {
    if (this._page <= 0) return;
    this._page--;
    this.changed.emit();
  }

  /** NextButton: a cached page, else C.MarketPage. */
  next(): void {
    if (this._page >= this._pageCount - 1) return;
    if (this._page < Math.trunc((this._listings.length - 1) / MARKET_PAGE_ROWS)) {
      this._page++;
      this.changed.emit();
      return;
    }
    this.deps.send(ClientPackets.MarketPage({ Page: this._page + 1 }));
  }

  /** CyclePriceFilter: Normal -> Low -> High -> Normal. */
  cyclePriceFilter(): void {
    this._priceFilter = this._priceFilter === "normal" ? "low" : this._priceFilter === "low" ? "high" : "normal";
    this.changed.emit();
  }

  /** GetOrderedListings. */
  ordered(): readonly ClientAuction[] {
    if (this._priceFilter === "high") return this._listings.slice().sort((a, b) => b.Price - a.Price);
    if (this._priceFilter === "low") return this._listings.slice().sort((a, b) => a.Price - b.Price);
    return this._listings;
  }

  /** The current page's rows (UpdateInterface); a selection not on it is dropped. */
  pageRows(): readonly ClientAuction[] {
    const rows = this.ordered().slice(this._page * MARKET_PAGE_ROWS, this._page * MARKET_PAGE_ROWS + MARKET_PAGE_ROWS);
    if (this._selected && !rows.includes(this._selected)) this._selected = null;
    return rows;
  }

  /** AuctionRow click. */
  select(listing: ClientAuction | null): void {
    this._selected = listing;
    this.changed.emit();
  }

  /** BuyButton in user mode: C.MarketGetBack (the UI confirms an unsold item first). */
  getBack(listing: ClientAuction): void {
    this.marketTime = this.deps.now() + MARKET_DELAY;
    this.deps.send(ClientPackets.MarketGetBack({ Mode: MarketCollectionMode.Any, AuctionID: listing.AuctionID }));
  }

  /** BuyButton's Yes: C.MarketBuy (bid 0 = buy outright; an auction bid after the amount box). */
  buy(listing: ClientAuction, bid: number): void {
    this.marketTime = this.deps.now() + MARKET_DELAY;
    this.deps.send(ClientPackets.MarketBuy({ AuctionID: listing.AuctionID, BidPrice: bid }));
  }

  /** SellNowButton. */
  sellNow(): void {
    const s = this._selected;
    if (!s || !this.ready) return;
    this.marketTime = this.deps.now() + MARKET_DELAY;
    this.deps.send(ClientPackets.MarketSellNow({ AuctionID: s.AuctionID }));
  }

  /** CollectSoldButton (user mode only): collect every sold item, then refresh. */
  collectSold(): void {
    if (!this.ready || !this._userMode) return;
    this.marketTime = this.deps.now() + MARKET_DELAY;
    this.deps.send(ClientPackets.MarketGetBack({ Mode: MarketCollectionMode.Sold, AuctionID: 0n }));
    this.deps.send(ClientPackets.MarketRefresh());
  }

  /** ItemCell_Click: bag item `item` (the pick) becomes the sell item, its cell locked (null clears the slot). */
  setSellItem(item: UserItem | null): void {
    this.releaseSellItem();
    if (item) {
      this._sellItem = item;
      this.deps.inventory.setLocked(item, true);
    }
    this.sellChanged.emit();
  }

  /** The tempCell unlock (Market tab, ItemCell right-click, Hide). */
  releaseSellItem(): void {
    if (this._sellItem) {
      this.deps.inventory.setLocked(this._sellItem, false);
      this._sellItem = null;
    }
  }

  /** SellItemButton: C.ConsignItem with the panel's type (the cell stays locked until S.ConsignItem). */
  sell(amount: number): boolean {
    if (!this._sellItem) return false;
    this.deps.inventory.consign(this._sellItem, amount, this._marketType);
    this._sellItem = null;
    return true;
  }

  /** Server packets (after Inventory). True = consumed (the old window's early returns). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NPCMarket":
        // GameScene.NPCMarket (Show() first: on a fresh open it sends the Market search like the desktop)
        this.opened.emit();
        this._open = true;
        this._userMode = p.UserMode;
        this._listings = p.Listings.slice();
        this._page = 0;
        this._pageCount = p.Pages;
        this.changed.emit();
        return true;
      case "NPCMarketPage":
        // GameScene.NPCMarketPage
        if (!this._open) return true;
        this._listings.push(...p.Listings);
        this._page = Math.trunc((this._listings.length - 1) / MARKET_PAGE_ROWS);
        this.changed.emit();
        return true;
      case "MarketFail": {
        // GameScene.MarketFail
        this.marketTime = 0;
        const text = MARKET_FAIL_TEXT[p.Reason];
        if (text) this.message.emit({ text });
        return true;
      }
      case "MarketSuccess":
        // GameScene.MarketSuccess
        this.marketTime = 0;
        this.message.emit({ text: p.Message });
        return true;
      case "MarketPriceHistory":
        // web (phase 2.6): cache and announce
        this._priceHistory.set(p.ItemIndex, p.Sales);
        this.priceHistoryChanged.emit({ itemIndex: p.ItemIndex, sales: p.Sales });
        return true;
      case "ConsignItem":
        // GameScene.ConsignItem (Inventory unlocked the cell): the sell slot empties
        if (this._sellItem && this._sellItem.UniqueID === p.UniqueID) this._sellItem = null;
        this.sellChanged.emit();
        return false;
      default: return false;
    }
  }
}
