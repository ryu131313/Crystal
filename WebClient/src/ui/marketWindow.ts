// Trust Merchant: TrustMerchantDialog (Client/MirScenes/Dialogs/TrustMerchantDialog.cs) with the desktop artwork (Title
// 786 market / 787 consign + auction, tab buttons, Prguse2 filter buttons, AuctionRows) + GameScene's market handlers
// (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess) + the consign confirm of NPCDropDialog (PanelType.Consign).
//   Market tab:  category filters (SetupFilters / DrawFilters with Up / Down / PositionBar), name search, refresh, 10 rows a
//                page (more pages via C.MarketPage), price sort on the Price header (CyclePriceFilter), Buy (consign) or Bid
//                (auction), Mail (MailComposeLetterDialog to the seller, through `mailTo`).
//   Consign / Auction tabs ("my items", UserMode = true): sell cell (pick an item in the bag, then click the cell), price
//                box (consign 5,000 - 50,000,000; starting bid 0 - 50,000), Sell, Get Back, Collect Sold (consign),
//                Sell Now (auction, when the seller column reads "Bid Met").
// The state (user mode, tab, throttles, listings, pages, selection, price sort, filter selection, sell slot) and the
// market packets are the Market model (src/game/market.ts); this window is its view.
// Not ported: the GameShop tab (TrustMerchantDialog.Show(true) is never called), the TotalGold label (hidden on the
// desktop too).

import type { ClientAuction, ServerPacket, UserItem } from "../protocol/server.ts";
import { BindMode, ItemGrade, MarketItemType, MarketPanelType } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import type { Emitter } from "../game/emitter.ts";
import {
  type Market, type MarketFilter, MARKET_FILTERS, MARKET_PAGE_ROWS, CONSIGNMENT_LENGTH, CONSIGNMENT_COST, MIN_CONSIGNMENT, MAX_CONSIGNMENT,
  AUCTION_COST, MIN_STARTING_BID, MAX_STARTING_BID, UINT_MAX,
} from "../game/market.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { SoundList } from "../game/audio.ts";
import { type InventoryWindow, placeCell } from "./inventoryWindow.ts";
import { showItemLabel, moveItemLabel, hideItemLabel } from "./itemLabel.ts";
import { dragY } from "./mir/controls.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, MirTextBox, closeButton, Lib, uiSound } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

/** ClientTextKeys.ConsignmentRules / AuctionRules (English). */
const CONSIGNMENT_TEXT = `1. Consignment is ${CONSIGNMENT_COST} gold per item \n\n2. 1% of sale price is paid to Trust Merchant at sale end\n\n3. Maximum ${CONSIGNMENT_LENGTH} days of item sale registration until item is removed\n\n4. Maximum of unlimited items allowed for sale\n\n5. Sale price can be set between: ${MIN_CONSIGNMENT} - ${MAX_CONSIGNMENT} gold`;
const AUCTION_TEXT = `1. Auction cost is ${AUCTION_COST} gold, max starting bid is ${MAX_STARTING_BID} gold per item \n\n2. 1% of final bid price is paid to Trust Merchant at auction end\n\n3. Maximum ${CONSIGNMENT_LENGTH} days of item sale registration, afterwards the item will be sent to highest bidder\n\n4. Maximum of unlimited items allowed for auction\n\n`;

/** new Font(Settings.FontName, Settings.FontSize - 1, FontStyle.Italic) / (Settings.FontSize - 1). */
const TITLE_FONT = "italic 7pt Arial";
const HELP_FONT = "7pt Arial";
const MAX_LINES = 19; // MaxLines

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

/** "###,###,##0". */
const n0 = (v: number) => v.toLocaleString("en-US");

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

/** AuctionRow.Update price colours (Red / Orange / LawnGreen / DeepSkyBlue / White). */
function priceColour(price: number): string {
  if (price > 10000000) return "#ff0000";
  if (price > 1000000) return "#ffa500";
  if (price > 100000) return "#7cfc00";
  if (price > 10000) return "#00bfff";
  return "#ffffff";
}

/** AuctionRow.Update seller colours in user mode (Gold / Red / LawnGreen). */
function sellerColour(seller: string, userMode: boolean): string {
  if (!userMode) return "#ffffff";
  switch (seller) {
    case "Sold": return "#ffd700";
    case "Expired": return "#ff0000";
    case "Bid Met": return "#7cfc00";
    default: return "#ffffff";
  }
}

/** NPCDropDialog.Confirm, PanelType.Consign: bind check, MirAmountBox (ConsignmentPrice, min .. max) for the price, C.ConsignItem (Type = Consign). */
export function npcConsign(inv: Inventory, index: number, notify: (text: string) => void): void {
  const item = inv.inventory[index];
  if (!item || inv.isLocked(item)) return;
  const info = inv.info(item);
  if (info && ((info.Bind & BindMode.DontStore) !== 0 || (info.Bind & BindMode.DontSell) !== 0)) { notify("Cannot consign this item."); return; }
  amountBox("Consignment Price:", info?.Image ?? -1, MAX_CONSIGNMENT, (amount) => {
    if (amount < MIN_CONSIGNMENT || amount > MAX_CONSIGNMENT) return;
    if (inv.inventory[index] !== item) return; // moved / gone while the box was open
    inv.consign(item, amount, MarketPanelType.Consign);
  }, MIN_CONSIGNMENT);
}

/** TrustMerchantDialog.AuctionRow: 354 x 32 at (127, 82 + i * 33); icon centred in 34 x 32, name, price, seller / expiry. */
class AuctionRow {
  readonly control: MirControl;
  private readonly icon: MirImage;
  private readonly nameLabel: MirLabel;
  private readonly priceLabel: MirLabel;
  private readonly sellerLabel: MirLabel;
  private readonly expireLabel: MirLabel;
  listing: ClientAuction | null = null;

  constructor(parent: MirControl, location: [number, number], onClick: (row: AuctionRow) => void, onHover: (row: AuctionRow, e: PointerEvent | null) => void) {
    this.control = new MirControl({ parent, location, size: [354, 32], visible: false });
    this.control.onClick(() => { uiSound(SoundList.ButtonA); onClick(this); });
    this.icon = new MirImage({ parent: this.control, library: Lib.Prguse, index: 540, location: [0, 0], notControl: true });
    this.nameLabel = new MirLabel({ parent: this.control, autoSize: true, location: [38, 8], notControl: true });
    this.priceLabel = new MirLabel({ parent: this.control, autoSize: true, location: [170, 8], notControl: true });
    this.sellerLabel = new MirLabel({ parent: this.control, autoSize: true, location: [256, 0], notControl: true });
    this.expireLabel = new MirLabel({ parent: this.control, autoSize: true, location: [256, 14], notControl: true });
    // OnMouseEnter / OnMouseLeave: GameScene.CreateItemLabel(Listing.Item) / DisposeItemLabel
    this.control.el.addEventListener("pointerenter", (e) => onHover(this, e));
    this.control.el.addEventListener("pointermove", (e) => onHover(this, e));
    this.control.el.addEventListener("pointerleave", () => onHover(this, null));
  }

  /** AuctionRow.Clear. */
  clear(): void {
    this.listing = null;
    this.control.visible = false;
    this.nameLabel.text = this.priceLabel.text = this.sellerLabel.text = "";
  }

  /** AuctionRow.Update + UpdateInterface. */
  update(l: ClientAuction, name: string, image: number | undefined, grade: number, userMode: boolean, selected: boolean): void {
    this.listing = l;
    this.nameLabel.text = name;
    this.nameLabel.foreColour = gradeColour(grade);
    this.priceLabel.text = `${n0(l.Price)} ${l.ItemType === MarketItemType.Auction ? "Bid" : ""}`;
    this.priceLabel.foreColour = priceColour(l.Price);
    this.sellerLabel.text = l.Seller;
    this.sellerLabel.foreColour = sellerColour(l.Seller, userMode);
    const showSeller = l.ItemType === MarketItemType.Consign || l.ItemType === MarketItemType.Auction;
    this.sellerLabel.visible = showSeller;
    this.expireLabel.visible = showSeller;
    this.expireLabel.text = formatDate(new Date(dateFromBinary(l.ConsignmentDate).getTime() + CONSIGNMENT_LENGTH * 86400000));
    // IconImage: Items[Info.Image], or Prguse 540 for a zero count
    if (l.Item.Count > 0) {
      if (image === undefined) this.icon.library = null;
      else { this.icon.library = Lib.Items; this.icon.index = image; }
    } else { this.icon.library = Lib.Prguse; this.icon.index = 540; }
    const lib = this.icon.library, idx = this.icon.index;
    void this.icon.ready.then(() => {
      if (this.icon.library !== lib || this.icon.index !== idx) return;
      const [w, h] = this.icon.size;
      this.icon.location = [Math.trunc((34 - w) / 2), Math.trunc((32 - h) / 2)];
    });
    // Border with BorderColour (200, 100, 0) while selected
    this.control.el.style.outline = selected ? "1px solid rgb(200,100,0)" : "";
    this.control.visible = true;
  }
}

export class MarketWindow {
  private readonly stage: HTMLElement;
  /** The Trust Merchant's state (src/game/market.ts). */
  private readonly market: Market;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  /** MailButton: GameScene.Scene.MailComposeLetterDialog.ComposeMail(seller, message); set by game.ts (inert while null). */
  mailTo: ((name: string, message: string) => void) | null = null;

  // TrustMerchantDialog view state (the rest lives in the Market model)
  private skip = 0;                   // Skip
  private possibleTotal = 0;          // PossibleTotal
  private amount = 0;                 // Amount
  private amountValid = false;        // SellItemButton.Enabled

  private readonly win: MirWindow;
  private readonly marketButton: MirButton;
  private readonly consignmentButton: MirButton;
  private readonly auctionButton: MirButton;
  private readonly helpLabel: MirLabel;
  private readonly pageLabel: MirLabel;
  private readonly upButton: MirButton;
  private readonly downButton: MirButton;
  private readonly positionBar: MirButton;
  private readonly mailButton: MirButton;
  private readonly refreshButton: MirButton;
  private readonly buyButton: MirButton;
  private readonly sellNowButton: MirButton;
  private readonly collectSoldButton: MirButton;
  private readonly searchBox: MirTextBox;
  private readonly findButton: MirButton;
  private readonly itemCellHost: MirControl;
  private itemCell: HTMLElement | null = null;
  private readonly priceBox: MirTextBox;
  private readonly sellButton: MirButton;
  private readonly rows: AuctionRow[] = [];
  private readonly titleSalePrice: MirLabel;
  private readonly titleSell: MirLabel;
  private readonly titleItem: MirLabel;
  private readonly titlePrice: MirLabel;
  private readonly priceFilterIcon: MirImage;
  private readonly titleExpiry: MirLabel;
  private filterButtons: MirButton[] = [];

  constructor(stage: HTMLElement, market: Market, inv: Inventory, invWindow: InventoryWindow, npc: { readonly closed: Emitter<void> }) {
    this.stage = stage;
    this.market = market;
    this.inv = inv;
    this.invWindow = invWindow;
    const offs = [
      // NPCDialog.Hide hides the TrustMerchantDialog
      npc.closed.on(() => this.close()),
      // GameScene.NPCMarket: Show() before the listings are applied
      market.opened.on(() => this.show()),
      market.changed.on(() => this.render()),
      market.sellChanged.on(() => { if (this.visible) this.renderSellCell(); }),
      // GameScene.MarketFail / MarketSuccess
      market.message.on(({ text }) => { messageBox(this.stage, text); }),
    ];

    // — TrustMerchantDialog ctor —
    const w = this.win = new MirWindow({ library: Lib.Title, index: 786, movable: true, sort: true, location: [0, 0], size: [492, 478], onHide: () => this.onHidden() });
    w.el.addEventListener("contextmenu", (e) => e.preventDefault());
    const tab = (index: number, pressed: number, x: number, type: number) =>
      new MirButton({ parent: w, library: Lib.Title, index, pressedIndex: pressed, location: [x, 35], onClick: () => {
        this.setTab(type);
        if (type === MarketPanelType.Market) { this.market.releaseSellItem(); this.render(); }
      } });
    this.marketButton = tab(789, 788, 9, MarketPanelType.Market);
    this.consignmentButton = tab(791, 790, 104, MarketPanelType.Consign);
    this.auctionButton = tab(817, 816, 199, MarketPanelType.Auction);
    closeButton(w, [465, 3], () => this.close());

    this.helpLabel = new MirLabel({ parent: w, text: CONSIGNMENT_TEXT, size: [115, 205], location: [8, 237], font: HELP_FONT, visible: false });
    const btn = (library: string, index: number, location: [number, number], onClick: () => void) =>
      new MirButton({ parent: w, library, index, hoverIndex: index + 1, pressedIndex: index + 2, location, sound: SoundList.ButtonA, onClick });
    btn(Lib.Prguse2, 240, [251, 419], () => this.market.back());
    btn(Lib.Prguse2, 243, [320, 419], () => this.market.next());
    this.pageLabel = new MirLabel({ parent: w, size: [70, 18], location: [260, 419], align: "center", text: "0/0", notControl: true });
    this.upButton = new MirButton({ parent: w, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], location: [108, 60], sound: SoundList.ButtonA,
      onClick: () => { if (this.skip <= 0) return; this.skip--; this.drawFilters(this.market.filterIndex, this.market.subFilterIndex); } });
    this.downButton = new MirButton({ parent: w, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], location: [108, 429], sound: SoundList.ButtonA,
      onClick: () => { if (this.skip + MAX_LINES >= this.possibleTotal) return; this.skip++; this.drawFilters(this.market.filterIndex, this.market.subFilterIndex); } });
    this.positionBar = new MirButton({ parent: w, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [108, 73], sound: 0, visible: false });
    dragY(this.positionBar, (y) => this.positionBarMoving(y));

    this.mailButton = btn(Lib.Prguse, 437, [350, 448], () => this.mail());
    this.refreshButton = btn(Lib.Prguse, 663, [320, 448], () => this.refresh());
    this.buyButton = btn(Lib.Title, 703, [380, 448], () => this.buy());
    this.sellNowButton = btn(Lib.Title, 700, [324, 448], () => this.market.sellNow());
    this.collectSoldButton = btn(Lib.Title, 680, [300, 448], () => this.market.collectSold());

    this.searchBox = new MirTextBox({ parent: w, location: [11, 452], size: [110, 18], maxLength: 20,
      onEnter: () => { this.market.search(this.searchBox.text, true); this.searchBox.input.blur(); }, onEscape: () => this.searchBox.input.blur() });
    this.findButton = btn(Lib.Title, 480, [124, 448], () => this.market.search(this.searchBox.text, false));

    // ItemCell (47, 104): the sell slot (a MirItemCell, lime border)
    this.itemCellHost = new MirControl({ parent: w, location: [47, 104], size: [36, 32], visible: false });
    this.priceBox = new MirTextBox({ parent: w, location: [15, 165], size: [100, 18], maxLength: 20, visible: false, onChange: () => this.priceChanged() });
    this.priceBox.input.addEventListener("keypress", (ev) => { const e = ev as KeyboardEvent; if (e.key.length === 1 && !/\d/.test(e.key)) e.preventDefault(); }); // MirInputBox_KeyPress
    this.sellButton = new MirButton({ parent: w, library: Lib.Title, index: 700, hoverIndex: 701, pressedIndex: 702, location: [39, 188], sound: SoundList.ButtonA,
      visible: false, enabled: false, onClick: () => this.sell() });

    for (let i = 0; i < MARKET_PAGE_ROWS; i++) {
      this.rows.push(new AuctionRow(w, [127, 82 + i * 33], (row) => this.market.select(row.listing), (row, e) => this.rowHover(row, e)));
    }

    const title = (text: string, size: [number, number], location: [number, number]) =>
      new MirLabel({ parent: w, text, font: TITLE_FONT, center: true, size, location });
    this.titleSalePrice = title("SALE PRICE", [100, 21], [15, 142]);
    this.titleSell = title("SELL ITEM", [110, 21], [10, 60]);
    this.titleItem = title("ITEM", [166, 21], [127, 60]);
    this.titlePrice = title("PRICE", [88, 21], [295, 60]);
    this.titlePrice.onClick(() => this.cyclePriceFilter());
    this.priceFilterIcon = new MirImage({ parent: w, library: Lib.Prguse2, index: 926, location: [295 + 88 - 12, 60 + Math.trunc((21 - 14) / 2) + 2], visible: false,
      onClick: () => this.cyclePriceFilter() });
    this.titleExpiry = title("EXPIRY", [98, 21], [384, 60]);

    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Closeall) && this.visible && !(document.activeElement instanceof HTMLInputElement)) this.close();
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => { for (const off of offs) off(); w.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** The packets the window still reacts to itself (the Market model has the market packets); never consumes. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NPCResponse":
        // GameScene.NPCResponse hides the TrustMerchantDialog
        this.close();
        return false;
      default: return false;
    }
  }

  /** TrustMerchantDialog.Show (not the GameShop variant): Market tab, InventoryDialog shown beside it. */
  private show(): void {
    if (this.visible) return;
    this.win.show();
    this.setTab(MarketPanelType.Market);
    this.invWindow.location = [this.win.size[0] + 5, this.win.location[1]];
    if (!this.invWindow.visible) this.invWindow.toggle(true);
  }

  /** TrustMerchantDialog.Hide (also NPCDialog.Hide / GameScene.NPCResponse / Closeall). */
  close(): void {
    this.win.hide();
  }

  private onHidden(): void {
    this.market.reset(); // listings, selection, the sell slot's lock
    hideItemLabel();
    this.invWindow.location = [0, 0];
  }

  /** TrustMerchantDialog.TMerchantDialog: switch the panel (artwork, buttons, titles) and search the user's (Consign /
   *  Auction) or all (Market) listings (the model's MarketType + C.MarketSearch, first: nothing below sends). */
  private setTab(type: number): void {
    const market = type === MarketPanelType.Market;
    const auction = type === MarketPanelType.Auction;
    this.market.setTab(type);
    this.marketButton.index = market ? 788 : 789;
    this.consignmentButton.index = type === MarketPanelType.Consign ? 790 : 791;
    this.auctionButton.index = auction ? 816 : 817;
    this.win.index = market ? 786 : 787;
    if (market) this.buyButton.setIndices(703, 704, 705);
    else this.buyButton.setIndices(706, 707, 708);
    this.sellNowButton.visible = auction;
    this.collectSoldButton.visible = type === MarketPanelType.Consign;
    this.upButton.visible = market;
    this.downButton.visible = market;
    this.mailButton.visible = market;
    this.findButton.visible = market;
    this.searchBox.visible = market;
    this.refreshButton.visible = market;
    this.priceBox.visible = !market;
    this.itemCellHost.visible = !market;
    this.sellButton.visible = !market;
    this.helpLabel.visible = !market;
    this.titleSalePrice.visible = !market;
    this.titleSell.visible = !market;
    this.titleSalePrice.text = auction ? "STARTING BID" : "SALE PRICE";
    this.titlePrice.text = market ? "PRICE / BID" : auction ? "HIGHEST BID" : "PRICE";
    this.titleExpiry.text = market ? "SELLER / EXPIRY" : auction ? "END DATE" : "EXPIRY";
    if (market) {
      this.drawFilters(0, -1);
    } else {
      for (const b of this.filterButtons) b.visible = false;
      this.positionBar.visible = false;
      this.helpLabel.text = auction ? AUCTION_TEXT : CONSIGNMENT_TEXT;
      this.priceBox.text = "";
      this.amount = 0;
      this.amountValid = false;
      this.sellButton.enabled = false;
      this.setPriceBorder("");
    }
    this.renderSellCell();
    this.render();
  }

  /** DrawFilters: the visible filter buttons (Prguse2 920 / 921, sub-filters 922 / 923) from Skip, at most MaxLines. */
  private drawFilters(index: number, subIndex: number): void {
    this.market.selectFilter(index, subIndex);
    for (const b of this.filterButtons) b.dispose();
    this.filterButtons = [];
    const x = 7;
    let y = 60, current = 0, skipped = this.skip;
    this.possibleTotal = MARKET_FILTERS.length;
    const label = (parent: MirButton, text: string, lx: number) =>
      new MirLabel({ parent, size: [99, 18], location: [lx, 1], text, notControl: true });
    for (const item of MARKET_FILTERS) {
      if (skipped > 0) { skipped--; continue; }
      if (current >= MAX_LINES) break;
      current++;
      const b = new MirButton({ parent: this.win, library: Lib.Prguse2, index: item.index === index ? 921 : 920, hoverIndex: 921, pressedIndex: 921, location: [x, y], sound: SoundList.ButtonA,
        onClick: () => this.filterClick(item, null) });
      label(b, item.title, 2);
      this.filterButtons.push(b);
      y += 20;
      if (item.index !== index) continue;
      this.possibleTotal += item.sub.length;
      if (item.sub.length) y += 2;
      for (const sub of item.sub) {
        if (skipped > 0) { skipped--; continue; }
        if (current >= MAX_LINES) break;
        current++;
        const s = new MirButton({ parent: this.win, library: Lib.Prguse2, index: sub.index === subIndex ? 923 : 922, hoverIndex: 923, pressedIndex: 923, location: [x, y], sound: SoundList.ButtonA,
          onClick: () => this.filterClick(item, sub) });
        label(s, sub.title, 10);
        this.filterButtons.push(s);
        y += 21;
      }
    }
    this.updatePositionBar();
  }

  /** The filter buttons' Click: expand, and search when the filter has a type. */
  private filterClick(item: MarketFilter, sub: MarketFilter | null): void {
    this.drawFilters(item.index, sub ? sub.index : -1);
    this.market.filterSearch(item, sub, this.searchBox.text);
  }

  private posRange(): [number, number] {
    return [60 + 13, 429 - 19]; // PosMinY = UpButton.Y + 13, PosMaxY = DownButton.Y - 19
  }

  /** UpdatePositionBar. */
  private updatePositionBar(): void {
    if (this.possibleTotal <= MAX_LINES || this.market.marketType !== MarketPanelType.Market) { this.positionBar.visible = false; return; }
    this.positionBar.visible = true;
    const [min, max] = this.posRange();
    const interval = Math.trunc((max - min) / (this.possibleTotal - MAX_LINES));
    this.positionBar.location = [108, Math.max(min, Math.min(max, min + this.skip * interval))];
  }

  /** PositionBar_OnMoving. */
  private positionBarMoving(y: number): void {
    if (this.possibleTotal <= MAX_LINES) return;
    const [min, max] = this.posRange();
    y = Math.max(min, Math.min(max, y));
    const interval = Math.trunc((max - min) / (this.possibleTotal - MAX_LINES)) || 1;
    this.skip = Math.floor((y - min) / interval);
    this.positionBar.location = [108, y];
    this.drawFilters(this.market.filterIndex, this.market.subFilterIndex);
  }

  /** RefreshButton (the search box clears when the refresh goes out). */
  private refresh(): void {
    if (this.market.refresh()) this.searchBox.text = "";
  }

  /** CyclePriceFilter: Normal -> Low -> High -> Normal (UpdatePriceFilterIcon: Prguse2 925 low / 926 high). */
  private cyclePriceFilter(): void {
    const next = this.market.priceFilter === "normal" ? "low" : this.market.priceFilter === "low" ? "high" : "normal";
    this.priceFilterIcon.visible = next !== "normal";
    this.priceFilterIcon.index = next === "low" ? 925 : 926;
    this.market.cyclePriceFilter();
  }

  private name(item: UserItem): string {
    const info = this.inv.info(item);
    const base = info ? itemFriendlyName(info) : `Item #${item.ItemIndex}`;
    return item.Count > 1 ? `${base} (${item.Count})` : base; // UserItem.FriendlyName
  }

  /** MailButton: a letter to the seller ("I am interested in purchasing {0} for {1}."). */
  private mail(): void {
    const s = this.market.selected;
    if (!s || !this.market.ready || !this.mailTo) return;
    this.mailTo(s.Seller, `I am interested in purchasing ${this.name(s.Item)} for ${s.Price}.`);
  }

  /** BuyButton: user mode = get back (confirm when unsold), else buy (confirm) or bid (MirAmountBox, then confirm). */
  private buy(): void {
    const s = this.market.selected;
    if (!s || !this.market.ready) return;
    const name = this.name(s.Item);
    const yesNo = (text: string, yes: () => void) => messageBox(this.stage, text, [{ label: "Yes", onClick: yes }, { label: "No" }]);
    const getBack = () => this.market.getBack(s);
    if (this.market.userMode) {
      const unsold = (s.ItemType === MarketItemType.Consign && s.Seller === "For Sale") || (s.ItemType === MarketItemType.Auction && s.Seller === "No Bid");
      if (s.ItemType !== MarketItemType.Consign && s.ItemType !== MarketItemType.Auction) return;
      if (unsold) yesNo(`${name} has not sold, Are you sure you want to get it back?`, getBack);
      else getBack();
      return;
    }
    switch (s.ItemType) {
      case MarketItemType.Consign:
      case MarketItemType.GameShop:
        yesNo(`Are you sure you want to buy ${name} for ${n0(s.Price)} Gold?`, () => this.market.buy(s, 0));
        break;
      case MarketItemType.Auction: {
        // MirAmountBox(BidAmount, Info.Image, uint.MaxValue, Price + 1, Price + 1)
        const min = Math.min(UINT_MAX, s.Price + 1);
        amountBox("Bid Amount:", this.inv.info(s.Item)?.Image ?? -1, UINT_MAX, (bid) => {
          yesNo(`Are you sure you want to bid ${n0(bid)} Gold for ${name}?`, () => this.market.buy(s, bid));
        }, min, min);
        break;
      }
    }
  }

  /** ItemCell_Click: the picked inventory item (GameScene.SelectedCell) becomes the sell item, its cell locked. */
  private sellCellClick(): void {
    const slot = this.invWindow.pickedInventorySlot;
    const item = slot >= 0 ? this.inv.inventory[slot] ?? null : null;
    this.market.setSellItem(item); // the old slot unlocks first; sellChanged redraws the cell
    if (!item) return;
    this.invWindow.clearPick();
    this.renderSellCell();
    this.priceBox.focus();
  }

  /** The ItemCell's picture (a read-only MirItemCell over the slot; clicks go to ItemCell_Click, right-click clears). */
  private renderSellCell(): void {
    this.itemCell?.remove();
    const cell = placeCell(this.invWindow.viewCell(this.market.sellItem), this.itemCellHost, [0, 0]);
    cell.addEventListener("mousedown", (e) => {
      if (e.button === 0) this.sellCellClick();
      else if (e.button === 2) { this.market.releaseSellItem(); this.renderSellCell(); }
    });
    this.itemCell = cell;
    this.sellButton.enabled = this.amountValid && !!this.market.sellItem;
  }

  /** PriceTextBox.BorderColour (the desktop sets it without Border = true; drawn here as the feedback it intends). */
  private setPriceBorder(colour: string): void {
    this.priceBox.el.style.outline = colour ? `1px solid ${colour}` : "";
  }

  /** TextBox_TextChanged: digits only, clamp to the max, valid at or above the min (consign) / starting bid (auction). */
  private priceChanged(): void {
    const digits = this.priceBox.text.replace(/[^\d]/g, "");
    if (!digits) { this.amount = 0; this.amountValid = false; this.priceBox.text = ""; this.setPriceBorder(""); this.sellButton.enabled = false; return; }
    let amount = Math.min(UINT_MAX, Number(digits));
    const auction = this.market.marketType === MarketPanelType.Auction;
    const min = auction ? MIN_STARTING_BID : MIN_CONSIGNMENT, max = auction ? MAX_STARTING_BID : MAX_CONSIGNMENT;
    this.amountValid = amount >= min;
    if (this.amountValid && amount > max) amount = max;
    this.amount = amount;
    this.priceBox.text = n0(amount);
    this.setPriceBorder(!this.amountValid ? "#ff0000" : amount === max ? "#ffa500" : "#00ff00");
    this.sellButton.enabled = this.amountValid && !!this.market.sellItem;
  }

  /** SellItemButton: C.ConsignItem with the panel's type (the cell stays locked until S.ConsignItem), then re-open the tab. */
  private sell(): void {
    if (!this.market.sellItem || !this.amountValid) return;
    this.market.sell(this.amount);
    this.setTab(this.market.marketType);
  }

  /** AuctionRow hover: GameScene.CreateItemLabel(Listing.Item). */
  private rowHover(row: AuctionRow, e: PointerEvent | null): void {
    if (!e || !row.listing) { hideItemLabel(); return; }
    if (e.type === "pointermove") moveItemLabel(e.clientX, e.clientY);
    else showItemLabel(row.listing.Item, this.invWindow.labelContext(), e.clientX, e.clientY);
  }

  /** TrustMerchantDialog.UpdateInterface. */
  private render(): void {
    if (!this.visible) return;
    this.pageLabel.text = `${this.market.page + 1}/${this.market.pageCount}`;
    const pageRows = this.market.pageRows(); // drops a selection that is not on this page
    const sel = this.market.selected;
    for (let i = 0; i < MARKET_PAGE_ROWS; i++) {
      const l = pageRows[i];
      if (!l) { this.rows[i]!.clear(); continue; }
      const info = this.inv.info(l.Item);
      this.rows[i]!.update(l, this.name(l.Item), info?.Image, info?.Grade ?? 0, this.market.userMode, l === sel);
    }
    this.buyButton.enabled = !!sel; this.buyButton.grayScale = !sel;
    this.collectSoldButton.enabled = !sel; this.collectSoldButton.grayScale = !!sel;
    this.mailButton.enabled = !!sel; this.mailButton.grayScale = !sel;
    const bidMet = !!sel && sel.Seller === "Bid Met";
    this.sellNowButton.enabled = bidMet; this.sellNowButton.grayScale = !bidMet;
  }
}
