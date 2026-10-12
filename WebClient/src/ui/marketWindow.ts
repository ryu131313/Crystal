// Trust Merchant: TrustMerchantDialog (Client/MirScenes/Dialogs/TrustMerchantDialog.cs, DOM edition) + GameScene's market
// handlers (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess) + the consign confirm of NPCDropDialog (PanelType.Consign).
//   Market tab:  category filters (SetupFilters / DrawFilters), name search, refresh, 10 rows a page (more pages via
//                C.MarketPage), price sort on the Price header (CyclePriceFilter), Buy (consign) or Bid (auction).
//   Consign / Auction tabs ("my items", Usermode = true): sell cell (pick an item in the bag, then click the cell), price
//                box (consign 5,000 - 50,000,000; starting bid 0 - 50,000), Sell, Get Back, Collect Sold (consign),
//                Sell Now (auction, when the seller column reads "Bid Met").
// Not ported: the Mail button (mail is not ported), the GameShop tab (TrustMerchantDialog.Show(true) is never called),
// the TotalGold label (hidden on the desktop too). MirMessageBox / MirAmountBox are confirm() / prompt(); the
// MarketFail / MarketSuccess boxes are the non-blocking messageBox.

import type { ClientAuction, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { BindMode, ChatType, ItemGrade, ItemType, MarketCollectionMode, MarketItemType, MarketPanelType } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { type InventoryWindow, itemIcon } from "./inventoryWindow.ts";
import { libIcon } from "./buffBar.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

// Shared/Globals.cs
const SEARCH_DELAY = 500;
const CONSIGNMENT_LENGTH = 7;
const CONSIGNMENT_COST = 5000;
const MIN_CONSIGNMENT = 5000;
const MAX_CONSIGNMENT = 50000000;
const AUCTION_COST = 5000;
const MIN_STARTING_BID = 0;
const MAX_STARTING_BID = 50000;
const UINT_MAX = 4294967295;

const CONSIGNMENT_TEXT = `1. Consignment is ${CONSIGNMENT_COST} gold per item\n\n2. 1% of sale price is paid to Trust Merchant at sale end\n\n3. Maximum ${CONSIGNMENT_LENGTH} days of item sale registration until item is removed\n\n4. Maximum of unlimited items allowed for sale\n\n5. Sale price can be set between: ${MIN_CONSIGNMENT} - ${MAX_CONSIGNMENT} gold`;
const AUCTION_TEXT = `1. Auction cost is ${AUCTION_COST} gold, max starting bid is ${MAX_STARTING_BID} gold per item\n\n2. 1% of final bid price is paid to Trust Merchant at auction end\n\n3. Maximum ${CONSIGNMENT_LENGTH} days of item sale registration, afterwards the item will be sent to highest bidder\n\n4. Maximum of unlimited items allowed for auction`;

/** GameScene.MarketFail reasons (ClientTextKeys English text). */
const FAIL_TEXT: Record<number, string> = {
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
interface Filter { index: number; title: string; type: number | null; minShape: number; maxShape: number; sub: Filter[] }

function f(index: number, title: string, type: number | null, minShape = 0, maxShape = 32767, sub: Filter[] = []): Filter {
  return { index, title, type, minShape, maxShape, sub };
}

/** TrustMerchantDialog.SetupFilters. */
const FILTERS: Filter[] = [
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

type PriceFilter = "normal" | "low" | "high"; // MarketPriceFilter

/** DateTime.FromBinary (copy of screens.ts dateFromBinary): low 62 bits = ticks since 0001-01-01; kind bits ignored. */
function dateFromBinary(v: bigint): Date {
  const ticks = BigInt.asUintN(62, v);
  return new Date(Number((ticks - 621355968000000000n) / 10000n));
}

/** "{0:dd/MM/yy HH:mm:ss}". */
function formatDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${p(d.getFullYear() % 100)} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

/** GameScene.GradeNameColor (Yellow shown White in AuctionRow.Update). */
function gradeColour(grade: number): string {
  switch (grade) {
    case ItemGrade.Rare: return "#00bfff";
    case ItemGrade.Legendary: return "#ff8c00";
    case ItemGrade.Mythical: return "#dda0dd";
    case ItemGrade.Heroic: return "#ff0000";
    default: return "#ffffff";
  }
}

/** AuctionRow.Update price colours. */
function priceColour(price: number): string {
  if (price > 10000000) return "#ff0000";
  if (price > 1000000) return "#ffa500";
  if (price > 100000) return "#7cfc00";
  if (price > 10000) return "#00bfff";
  return "#ffffff";
}

/** AuctionRow.Update seller colours in user mode. */
function sellerColour(seller: string, userMode: boolean): string {
  if (!userMode) return "#ffffff";
  switch (seller) {
    case "Sold": return "#ffd700";
    case "Expired": return "#ff0000";
    case "Bid Met": return "#7cfc00";
    default: return "#ffffff";
  }
}

/** NPCDropDialog.Confirm, PanelType.Consign: bind check, MirAmountBox for the price, C.ConsignItem (Type = Consign). */
export function npcConsign(inv: Inventory, index: number, notify: (text: string) => void): void {
  const item = inv.inventory[index];
  if (!item || inv.isLocked(item)) return;
  const info = inv.info(item);
  if (info && ((info.Bind & BindMode.DontStore) !== 0 || (info.Bind & BindMode.DontSell) !== 0)) { notify("Cannot consign this item."); return; }
  const text = prompt(`Consignment Price: (${MIN_CONSIGNMENT.toLocaleString()} - ${MAX_CONSIGNMENT.toLocaleString()})`, "");
  if (text === null) return;
  const amount = Math.trunc(Number(text.replace(/[,\s]/g, "")));
  if (!(amount >= MIN_CONSIGNMENT && amount <= MAX_CONSIGNMENT)) return;
  inv.consign(item, amount, MarketPanelType.Consign);
}

export class MarketWindow {
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly now: () => number;

  // TrustMerchantDialog state
  private userMode = false;           // UserMode
  private marketType: number = MarketPanelType.Market; // MarketType
  private searchTime = 0;             // SearchTime
  private marketTime = 0;             // MarketTime
  private listings: ClientAuction[] = [];
  private page = 0;
  private pageCount = 0;
  private selected: ClientAuction | null = null; // Selected.Listing
  private priceFilter: PriceFilter = "normal";
  private filterIndex = 0;            // SelectedIndex
  private subFilterIndex = -1;        // SelectedSubIndex
  private sellItem: UserItem | null = null; // SellItemSlot (tempCell locked)
  private amount = 0;                 // Amount
  private amountValid = false;        // SellItemButton.Enabled

  private readonly root = el("div", { class: "window market-window", hidden: true });
  private readonly tabs = el("div", { class: "market-tabs" });
  private readonly side = el("div", { class: "market-side" });
  private readonly rows = el("div", { class: "market-rows" });
  private readonly header = el("div", { class: "market-row market-header" });
  private readonly pageLabel = el("span", { class: "market-page" });
  private readonly footer = el("div", { class: "market-footer" });
  private readonly searchBox = el("input", { class: "market-search", maxLength: 20, placeholder: "Search" });
  private readonly priceBox = el("input", { class: "market-price", maxLength: 20, inputMode: "numeric" });

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, now: () => number, npc: { onClose: () => void }) {
    this.stage = stage;
    // NPCDialog.Hide hides the TrustMerchantDialog
    const prevClose = npc.onClose;
    npc.onClose = () => { prevClose(); this.close(); };
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.now = now;
    this.searchBox.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key === "Enter") { e.preventDefault(); this.search(true); this.searchBox.blur(); }
      else if (e.key === "Escape") this.searchBox.blur();
    });
    this.priceBox.addEventListener("keydown", (e) => e.stopPropagation());
    this.priceBox.addEventListener("input", () => this.priceChanged());
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Trust Merchant"), el("button", { class: "window-close", onclick: () => this.close() }, "×")),
      this.tabs,
      el("div", { class: "market-body" }, this.side, el("div", { class: "market-main" }, this.header, this.rows,
        el("div", { class: "market-pager" },
          el("button", { onclick: () => this.back() }, "◀"), this.pageLabel, el("button", { onclick: () => this.next() }, "▶")))),
      this.footer,
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Closeall) && this.visible && !(document.activeElement instanceof HTMLInputElement)) this.close();
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NPCMarket":
        // GameScene.NPCMarket (Show() first: on a fresh open it sends the Market search like the desktop)
        this.show();
        this.userMode = p.UserMode;
        this.listings = p.Listings.slice();
        this.page = 0;
        this.pageCount = p.Pages;
        this.render();
        return true;
      case "NPCMarketPage":
        // GameScene.NPCMarketPage
        if (!this.visible) return true;
        this.listings.push(...p.Listings);
        this.page = Math.trunc((this.listings.length - 1) / 10);
        this.render();
        return true;
      case "MarketFail":
        // GameScene.MarketFail
        this.marketTime = 0;
        if (FAIL_TEXT[p.Reason]) messageBox(this.stage, FAIL_TEXT[p.Reason]!);
        return true;
      case "MarketSuccess":
        // GameScene.MarketSuccess
        this.marketTime = 0;
        messageBox(this.stage, p.Message);
        return true;
      case "NPCResponse":
        // GameScene.NPCResponse hides the TrustMerchantDialog
        this.close();
        return false;
      case "ConsignItem":
        if (this.sellItem && this.sellItem.UniqueID === p.UniqueID) this.sellItem = null;
        if (this.visible) this.render();
        return false;
      default: return false;
    }
  }

  /** TrustMerchantDialog.Show (not the GameShop variant): Market tab, inventory shown beside it. */
  private show(): void {
    if (this.visible) return;
    this.root.hidden = false;
    this.setTab(MarketPanelType.Market);
    if (!this.invWindow.visible) this.invWindow.toggle(true);
  }

  /** TrustMerchantDialog.Hide (also NPCDialog.Hide / GameScene.NPCResponse). */
  close(): void {
    if (!this.visible) return;
    this.root.hidden = true;
    this.listings = [];
    this.selected = null;
    this.releaseSellItem();
  }

  /** The tempCell unlock (Market button, ItemCell_Click, Hide). */
  private releaseSellItem(): void {
    if (this.sellItem) {
      this.inv.setLocked(this.sellItem, false);
      this.sellItem = null;
    }
  }

  /** TrustMerchantDialog.TMerchantDialog: switch the panel and search the user's (Consign / Auction) or all (Market) listings. */
  private setTab(type: number): void {
    this.marketType = type;
    if (type === MarketPanelType.Market) {
      this.filterIndex = 0;
      this.subFilterIndex = -1;
    } else {
      this.priceBox.value = "";
      this.amount = 0;
      this.amountValid = false;
    }
    this.send(ClientPackets.MarketSearch({ Match: "", Type: ItemType.Nothing, Usermode: type !== MarketPanelType.Market, MinShape: 0, MaxShape: 5000, MarketType: type }));
    this.render();
  }

  /** FindButton (empty text first) / SearchTextBox_KeyPress Enter (the wait first). */
  private search(enter: boolean): void {
    const t = this.now();
    const match = this.searchBox.value;
    if (!match && !enter) return;
    if (t < this.searchTime) {
      this.chat(`You can search again after ${Math.ceil((this.searchTime - t) / 1000)} seconds.`, ChatType.System);
      return;
    }
    if (!match) return;
    this.searchTime = t + SEARCH_DELAY;
    this.send(ClientPackets.MarketSearch({ Match: match, Type: ItemType.Nothing, Usermode: false, MinShape: 0, MaxShape: 5000, MarketType: this.marketType }));
  }

  /** RefreshButton. */
  private refresh(): void {
    const t = this.now();
    if (t < this.searchTime) {
      this.chat(`You can search again after ${Math.ceil((this.searchTime - t) / 1000)} seconds.`, ChatType.System);
      return;
    }
    this.searchTime = t + SEARCH_DELAY;
    this.searchBox.value = "";
    this.send(ClientPackets.MarketRefresh());
  }

  /** DrawFilters button click: expand, and search when the filter has a type. */
  private filterClick(item: Filter, sub: Filter | null): void {
    this.filterIndex = item.index;
    this.subFilterIndex = sub ? sub.index : -1;
    const target = sub ?? (item.sub.length ? null : item);
    if (target && target.type !== null) {
      this.send(ClientPackets.MarketSearch({ Match: this.searchBox.value, Type: target.type, Usermode: false, MinShape: target.minShape, MaxShape: target.maxShape, MarketType: this.marketType }));
    }
    this.render();
  }

  /** BackButton. */
  private back(): void {
    if (this.page <= 0) return;
    this.page--;
    this.render();
  }

  /** NextButton: a cached page, else C.MarketPage. */
  private next(): void {
    if (this.page >= this.pageCount - 1) return;
    if (this.page < Math.trunc((this.listings.length - 1) / 10)) {
      this.page++;
      this.render();
      return;
    }
    this.send(ClientPackets.MarketPage({ Page: this.page + 1 }));
  }

  /** CyclePriceFilter: Normal -> Low -> High -> Normal. */
  private cyclePriceFilter(): void {
    this.priceFilter = this.priceFilter === "normal" ? "low" : this.priceFilter === "low" ? "high" : "normal";
    this.render();
  }

  /** GetOrderedListings. */
  private ordered(): ClientAuction[] {
    if (this.priceFilter === "high") return this.listings.slice().sort((a, b) => b.Price - a.Price);
    if (this.priceFilter === "low") return this.listings.slice().sort((a, b) => a.Price - b.Price);
    return this.listings;
  }

  private name(item: UserItem): string {
    const info = this.inv.info(item);
    const base = info ? itemFriendlyName(info) : `Item #${item.ItemIndex}`;
    return item.Count > 1 ? `${base} (${item.Count})` : base; // UserItem.FriendlyName
  }

  /** BuyButton: user mode = get back (confirm when unsold), else buy (confirm) or bid (amount, then confirm). */
  private buy(): void {
    const s = this.selected;
    if (!s || this.now() < this.marketTime) return;
    const name = this.name(s.Item);
    const getBack = () => { this.marketTime = this.now() + 3000; this.send(ClientPackets.MarketGetBack({ Mode: MarketCollectionMode.Any, AuctionID: s.AuctionID })); };
    if (this.userMode) {
      if (s.ItemType === MarketItemType.Consign) {
        if (s.Seller === "For Sale") { if (confirm(`${name} has not sold, Are you sure you want to get it back?`)) getBack(); }
        else getBack();
      } else if (s.ItemType === MarketItemType.Auction) {
        if (s.Seller === "No Bid") { if (confirm(`${name} has not sold, Are you sure you want to get it back?`)) getBack(); }
        else getBack();
      }
      return;
    }
    switch (s.ItemType) {
      case MarketItemType.Consign:
      case MarketItemType.GameShop:
        if (confirm(`Are you sure you want to buy ${name} for ${s.Price.toLocaleString()} Gold?`)) {
          this.marketTime = this.now() + 3000;
          this.send(ClientPackets.MarketBuy({ AuctionID: s.AuctionID, BidPrice: 0 }));
        }
        break;
      case MarketItemType.Auction: {
        // MirAmountBox(BidAmount, max uint, min Price + 1, default Price + 1)
        const min = Math.min(UINT_MAX, s.Price + 1);
        const text = prompt(`Bid Amount: (min ${min.toLocaleString()})`, String(min));
        if (text === null) return;
        const bid = Math.trunc(Number(text.replace(/[,\s]/g, "")));
        if (!(bid >= min && bid <= UINT_MAX)) return;
        if (confirm(`Are you sure you want to bid ${bid.toLocaleString()} Gold for ${name}?`)) {
          this.marketTime = this.now() + 3000;
          this.send(ClientPackets.MarketBuy({ AuctionID: s.AuctionID, BidPrice: bid }));
        }
        break;
      }
    }
  }

  /** SellNowButton. */
  private sellNow(): void {
    const s = this.selected;
    if (!s || this.now() < this.marketTime) return;
    this.marketTime = this.now() + 3000;
    this.send(ClientPackets.MarketSellNow({ AuctionID: s.AuctionID }));
  }

  /** CollectSoldButton (user mode only): collect every sold item, then refresh. */
  private collectSold(): void {
    if (this.now() < this.marketTime || !this.userMode) return;
    this.marketTime = this.now() + 3000;
    this.send(ClientPackets.MarketGetBack({ Mode: MarketCollectionMode.Sold, AuctionID: 0n }));
    this.send(ClientPackets.MarketRefresh());
  }

  /** ItemCell_Click: the picked inventory item (GameScene.SelectedCell) becomes the sell item, its cell locked. */
  private sellCellClick(): void {
    this.releaseSellItem();
    const slot = this.invWindow.pickedInventorySlot;
    const item = slot >= 0 ? this.inv.inventory[slot] ?? null : null;
    if (!item) { this.render(); return; }
    this.sellItem = item;
    this.inv.setLocked(item, true);
    this.invWindow.clearPick();
    this.render();
    this.priceBox.focus();
  }

  /** TextBox_TextChanged: digits only, clamp to the max, valid at or above the min (consign) / starting bid (auction). */
  private priceChanged(): void {
    const digits = this.priceBox.value.replace(/[^\d]/g, "");
    if (!digits) { this.amount = 0; this.amountValid = false; this.priceBox.value = ""; this.priceBox.classList.remove("bad", "max"); this.renderSellState(); return; }
    let amount = Math.min(UINT_MAX, Number(digits));
    const auction = this.marketType === MarketPanelType.Auction;
    const min = auction ? MIN_STARTING_BID : MIN_CONSIGNMENT, max = auction ? MAX_STARTING_BID : MAX_CONSIGNMENT;
    this.amountValid = amount >= min;
    if (this.amountValid && amount > max) amount = max;
    this.amount = amount;
    this.priceBox.value = amount.toLocaleString("en-US");
    this.priceBox.classList.toggle("bad", !this.amountValid);
    this.priceBox.classList.toggle("max", this.amountValid && amount === max);
    this.renderSellState();
  }

  private sellButton: HTMLButtonElement | null = null;

  private renderSellState(): void {
    if (this.sellButton) this.sellButton.disabled = !this.amountValid || !this.sellItem;
  }

  /** SellItemButton: C.ConsignItem with the panel's type (the cell stays locked until S.ConsignItem), then re-open the tab. */
  private sell(): void {
    if (!this.sellItem || !this.amountValid) return;
    this.inv.consign(this.sellItem, this.amount, this.marketType);
    this.sellItem = null;
    this.setTab(this.marketType);
  }

  /** TrustMerchantDialog.UpdateInterface + TMerchantDialog's per-tab visibility. */
  private render(): void {
    if (this.root.hidden) return;
    const market = this.marketType === MarketPanelType.Market;
    const auction = this.marketType === MarketPanelType.Auction;

    // Tabs (MarketButton / ConsignmentButton / AuctionButton)
    const tab = (type: number, label: string) => el("button", { class: `market-tab${this.marketType === type ? " on" : ""}`, onclick: () => {
      if (type === MarketPanelType.Market) this.releaseSellItem();
      this.setTab(type);
    } }, label);
    this.tabs.replaceChildren(tab(MarketPanelType.Market, "Market"), tab(MarketPanelType.Consign, "Consignment"), tab(MarketPanelType.Auction, "Auction"));

    // Left column: filters (market) or the sell panel (consign / auction)
    if (market) {
      const buttons: HTMLElement[] = [];
      for (const item of FILTERS) {
        buttons.push(el("button", { class: `market-filter${item.index === this.filterIndex ? " on" : ""}`, onclick: () => this.filterClick(item, null) }, item.title));
        if (item.index === this.filterIndex) {
          for (const sub of item.sub) {
            buttons.push(el("button", { class: `market-filter sub${sub.index === this.subFilterIndex ? " on" : ""}`, onclick: () => this.filterClick(item, sub) }, sub.title));
          }
        }
      }
      this.side.replaceChildren(el("div", { class: "market-filters" }, ...buttons));
    } else {
      const cell = el("div", { class: "inv-cell market-sell-cell", title: this.sellItem ? this.invWindow.describe(this.sellItem) : "Pick an item in your bag, then click here." });
      if (this.sellItem) {
        const info = this.inv.info(this.sellItem);
        if (info) {
          const img = el("img", { alt: info.Name, draggable: false });
          void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
          cell.append(img);
        }
        if (this.sellItem.Count > 1) cell.append(el("span", { class: "inv-count" }, String(this.sellItem.Count)));
      }
      cell.onmousedown = (e) => { if (e.button === 0) this.sellCellClick(); else if (e.button === 2) { this.releaseSellItem(); this.render(); } };
      this.sellButton = el("button", { class: "primary", onclick: () => this.sell() }, "Sell");
      this.side.replaceChildren(
        el("div", { class: "market-side-title" }, "Sell Item"), cell,
        el("div", { class: "market-side-name" }, this.sellItem ? this.name(this.sellItem) : ""),
        el("div", { class: "market-side-title" }, auction ? "Starting Bid" : "Sale Price"), this.priceBox, this.sellButton,
        el("div", { class: "market-help" }, auction ? AUCTION_TEXT : CONSIGNMENT_TEXT));
      this.renderSellState();
    }

    // Header (TitleItemLabel / TitlePriceLabel (click = sort) / TitleExpiryLabel)
    const arrow = this.priceFilter === "high" ? " ▼" : this.priceFilter === "low" ? " ▲" : "";
    const priceTitle = market ? "Price/Bid" : auction ? "Highest Bid" : "Price";
    const expiryTitle = market ? "Seller/Expiry" : auction ? "End Date" : "Expiry";
    this.header.replaceChildren(el("span", { class: "market-icon" }), el("span", { class: "market-name" }, "Item"),
      el("span", { class: "market-price-col sortable", title: "Sort by price", onclick: () => this.cyclePriceFilter() }, priceTitle + arrow),
      el("span", { class: "market-seller" }, expiryTitle));

    // Rows (10 per page)
    const ordered = this.ordered();
    const pageRows = ordered.slice(this.page * 10, this.page * 10 + 10);
    if (this.selected && !pageRows.includes(this.selected)) this.selected = null;
    this.rows.replaceChildren(...pageRows.map((l) => this.row(l)));
    this.pageLabel.textContent = `${this.page + 1}/${this.pageCount}`;

    // Footer buttons
    const sel = this.selected;
    const footer: HTMLElement[] = [];
    if (market) {
      footer.push(this.searchBox,
        el("button", { onclick: () => this.search(false) }, "Find"),
        el("button", { onclick: () => this.refresh() }, "Refresh"));
    }
    const spacer = el("span", { class: "market-spacer" });
    footer.push(spacer);
    if (this.marketType === MarketPanelType.Consign) footer.push(el("button", { disabled: !!sel, onclick: () => this.collectSold() }, "Collect Sold"));
    if (auction) footer.push(el("button", { disabled: !(sel && sel.Seller === "Bid Met"), onclick: () => this.sellNow() }, "Sell Now"));
    footer.push(el("button", { class: "primary", disabled: !sel, onclick: () => this.buy() }, market ? "Buy" : "Get Back"));
    this.footer.replaceChildren(...footer);
  }

  /** AuctionRow.Update / UpdateInterface (icon: the item, or Prguse 540 for a zero count). */
  private row(l: ClientAuction): HTMLElement {
    const info = this.inv.info(l.Item);
    const icon = el("span", { class: "market-icon" });
    const img = el("img", { alt: "", draggable: false });
    const url = l.Item.Count > 0 && info ? itemIcon(info.Image) : libIcon("Prguse", 540);
    void url.then((u) => { if (u) img.src = u; });
    icon.append(img);
    const showSeller = l.ItemType === MarketItemType.Consign || l.ItemType === MarketItemType.Auction;
    const expiry = new Date(dateFromBinary(l.ConsignmentDate).getTime() + CONSIGNMENT_LENGTH * 86400000);
    const r = el("div", { class: `market-row${l === this.selected ? " selected" : ""}`, title: this.invWindow.describe(l.Item),
      onclick: () => { this.selected = l; this.render(); } },
      icon,
      el("span", { class: "market-name", style: `color:${gradeColour(info?.Grade ?? 0)}` }, this.name(l.Item)),
      el("span", { class: "market-price-col", style: `color:${priceColour(l.Price)}` }, `${l.Price.toLocaleString()}${l.ItemType === MarketItemType.Auction ? " Bid" : ""}`),
      el("span", { class: "market-seller" },
        showSeller ? el("div", { style: `color:${sellerColour(l.Seller, this.userMode)}` }, l.Seller) : "",
        showSeller ? el("div", { class: "market-expiry" }, formatDate(expiry)) : ""));
    return r;
  }
}
