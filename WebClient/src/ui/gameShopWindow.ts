// Game Shop with the desktop artwork: GameShopDialog (Client/MirScenes/Dialogs/GameshopDialog.cs, Title 749) + GameShopCell
// (Client/MirControls/MirGameShopCell.cs, Title 750) + GameScene.GameShopUpdate / GameShopStock.
//   Section tabs (Title 770 / 776 / 772 / 774: All Items / Top Items / Deals / New, New shown once an item dated within
//   7 days arrives), class buttons (Title 751 .. 766; Show() starts on the user's class), the name search box, the
//   22-row category list (GetCategories / SetCategories) with its up / down buttons, position bar and mouse wheel, 4 x 2
//   cells a page sorted by name, credits + gold, the Gold / Credits payment check boxes (gold default), page buttons.
//   A cell: name in its grade colour, the item (label on hover), count, stock (0 = unlimited), credit / gold price x
//   quantity, quantity - / + (Shift = 10; capped at 5 stacks, 99 and the stock), Buy (MirMessageBox YesNo, then
//   C.GameshopBuy).
// The window owns GameScene.GameShopInfoList. Toggle with Y (KeybindOptions.GameShop) or the Shop HUD button; Escape
// (Closeall) hides it. Not ported: the GameShopViewer (draws the item on the user's paperdoll sprites): the Preview
// button shows for weapons / armours / mounts / transforms like the desktop but does nothing.

import type { GameShopItem, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, ItemGrade, ItemType, MirClassName } from "../protocol/enums.generated.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import {
  MirWindow, MirControl, MirImage, MirButton, MirCheckBox, MirLabel, MirTextBox, Lib, closeButton, setPicture, type Point,
} from "./mir/controls.ts";
import { type InventoryWindow } from "./inventoryWindow.ts";
import { showItemLabel, hideItemLabel } from "./itemLabel.ts";
import { dragY } from "./mir/controls.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const CELLS = 8; // GameShopDialog.Grid (4 x 2)
const FILTERS = 22; // GameShopDialog.Filters
const NEW_DAYS = 7;
const SHOW_ALL = "Show All";
type Section = "Show All" | "TopItems" | "DealItems" | "NewItems";
const FONT_7 = "7pt Arial";
const GRAY = "#808080"; // Color.Gray
const FILTER_SELECTED = "rgb(230,200,160)";
const FILTER_HOVER = "rgb(160,140,110)";

/** "###,###,##0". */
const n0 = (n: number) => n.toLocaleString("en-US");

/** GameScene.GradeNameColor. */
function gradeNameColor(grade: number): string {
  switch (grade) {
    case ItemGrade.Rare: return "#00bfff";
    case ItemGrade.Legendary: return "#ff8c00";
    case ItemGrade.Mythical: return "#dda0dd";
    case ItemGrade.Heroic: return "#ff0000";
    default: return "#ffff00";
  }
}

/** GameScene.GameShopUpdate's `Date > CMain.Now.AddDays(-7)`. */
function isNew(item: GameShopItem): boolean {
  return dateFromBinary(item.Date).getTime() > Date.now() - NEW_DAYS * 86400000;
}

/** GameShopCell.OnMouseMove: ShowItem = new UserItem(Item.Info) { MaxDura = CurrentDura = Info.Durability, Count = Item.Count }. */
function showItem(item: GameShopItem): UserItem {
  return {
    UniqueID: 0n, ItemIndex: item.Info.Index, CurrentDura: item.Info.Durability, MaxDura: item.Info.Durability, Count: item.Count,
    SoulBoundId: -1, Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [],
    RefinedValue: 0, RefineAdded: 0, RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false,
    Sealed: null, GMMade: false,
  };
}

/** GameShopCell (Title 750, 125 x 146): one persistent cell per grid slot; Item = null hides it (the desktop disposes it). */
class GameShopCell {
  readonly control: MirImage;
  item: GameShopItem | null = null;
  /** GameShopCell.Quantity (a byte; a new cell starts at 1). */
  quantity = 1;
  private readonly nameLabel: MirLabel;
  private readonly goldLabel: MirLabel;
  private readonly gpLabel: MirLabel;
  private readonly stockLabel: MirLabel;
  private readonly countLabel: MirLabel;
  private readonly quantityLabel: MirLabel;
  private readonly buyItem: MirButton;
  private readonly previewItem: MirButton;
  /** DrawControl: Items[Info.Image] centred in 32 x 32 at (12, 40); hovering it shows the item label (ItemDisplayArea). */
  private readonly icon: HTMLDivElement;
  private shown: UserItem | null = null;
  private readonly shop: GameShopWindow;

  constructor(parent: MirControl, location: Point, shop: GameShopWindow) {
    this.shop = shop;
    this.control = new MirImage({ parent, library: Lib.Title, index: 750, location, size: [125, 146], visible: false });
    const c = this.control;
    this.nameLabel = new MirLabel({ parent: c, size: [125, 15], align: "center", location: [0, 13], notControl: true });
    this.goldLabel = new MirLabel({ parent: c, size: [95, 20], align: "right", location: [2, 102], notControl: true });
    this.gpLabel = new MirLabel({ parent: c, size: [95, 20], align: "right", location: [2, 81], notControl: true });
    new MirLabel({ parent: c, size: [40, 20], location: [53, 37], notControl: true, foreColour: GRAY, font: FONT_7, text: "STOCK:" });
    this.stockLabel = new MirLabel({ parent: c, size: [20, 20], align: "center", location: [93, 37], notControl: true, font: FONT_7 });
    this.countLabel = new MirLabel({ parent: c, size: [30, 20], align: "right", location: [16, 60], notControl: true, font: FONT_7 });
    this.buyItem = new MirButton({ parent: c, library: Lib.Title, index: 778, hoverIndex: 779, pressedIndex: 780, location: [42, 122], sound: SoundList.ButtonA,
      onClick: () => { if (this.item) this.shop.buyProduct(this.item, this.quantity); } });
    this.previewItem = new MirButton({ parent: c, library: Lib.Title, index: 781, hoverIndex: 782, pressedIndex: 783, location: [8, 122], sound: SoundList.ButtonA,
      visible: false }); // GameShopViewer is not ported
    new MirButton({ parent: c, library: Lib.Prguse2, index: 243, hoverIndex: 244, pressedIndex: 245, location: [97, 56], sound: SoundList.ButtonA,
      onClick: (e) => this.quantityUp(e.shiftKey) });
    new MirButton({ parent: c, library: Lib.Prguse2, index: 240, hoverIndex: 241, pressedIndex: 242, location: [55, 56], sound: SoundList.ButtonA,
      onClick: (e) => this.quantityDown(e.shiftKey) });
    this.quantityLabel = new MirLabel({ parent: c, size: [20, 13], center: true, location: [74, 56], notControl: true });
    this.icon = document.createElement("div");
    this.icon.className = "mir-pic";
    this.icon.style.pointerEvents = "auto";
    c.el.append(this.icon);
    this.icon.addEventListener("pointermove", (e) => {
      if (!this.item) return;
      this.shown ??= showItem(this.item);
      showItemLabel(this.shown, this.shop.labelContext(), e.clientX, e.clientY, { info: this.item.Info });
    });
    this.icon.addEventListener("pointerleave", () => { this.shown = null; hideItemLabel(); });
    c.el.addEventListener("pointerleave", () => { this.shown = null; hideItemLabel(); }); // MouseLeave: DisposeItemLabel
  }

  /** UpdateShop: a new GameShopCell for `item` (Quantity 1), or none. */
  set(item: GameShopItem | null): void {
    if (this.shown) { this.shown = null; hideItemLabel(); }
    this.item = item;
    this.quantity = 1;
    this.control.visible = !!item;
    this.goldLabel.text = "";
    this.gpLabel.text = "";
    this.previewItem.visible = false;
    this.buyItem.location = [42, 122];
    if (!item) { void setPicture(this.icon, null, -1); return; }
    const image = item.Info.Image;
    void setPicture(this.icon, Lib.Items, image).then((pic) => {
      if (!pic || this.item !== item) return;
      this.icon.style.left = Math.trunc((32 - pic.width) / 2) + 12 + "px";
      this.icon.style.top = Math.trunc((32 - pic.height) / 2) + 40 + "px";
    });
    this.updateText();
  }

  /** GameShopCell.UpdateText. */
  updateText(): void {
    const item = this.item;
    if (!item) return;
    const name = itemFriendlyName(item.Info);
    this.nameLabel.text = name.length > 17 ? name.substring(0, 17) : name;
    this.nameLabel.foreColour = gradeNameColor(item.Info.Grade);
    if (item.CanBuyGold) this.goldLabel.text = n0(item.GoldPrice * this.quantity);
    if (item.CanBuyCredit) this.gpLabel.text = n0(item.CreditPrice * this.quantity);
    this.stockLabel.text = item.Stock === 0 ? "∞" : String(item.Stock); // the desktop's "99+" is overwritten by this
    this.countLabel.text = String(item.Count);
    this.quantityLabel.text = String(this.quantity);
    const t = item.Info.Type;
    if (t === ItemType.Mount || t === ItemType.Weapon || t === ItemType.Armour || t === ItemType.Transform) {
      this.previewItem.visible = true;
      this.buyItem.location = [75, 122];
    }
  }

  /** quantityUp.Click (Quantity is a byte). */
  private quantityUp(shift: boolean): void {
    const item = this.item;
    if (!item) return;
    let q = (this.quantity + (shift ? 10 : 1)) & 0xff;
    const stack = Math.max(1, item.Info.StackSize), count = Math.max(1, item.Count);
    if ((q * count) / stack > 5) q = Math.trunc((5 * stack) / count) > 99 ? 99 : Math.trunc((5 * stack) / count) & 0xff;
    if (q >= 99) q = 99;
    if (item.Stock !== 0 && q > item.Stock) q = item.Stock;
    this.quantity = q;
    this.updateText();
  }

  /** quantityDown.Click. */
  private quantityDown(shift: boolean): void {
    let q = (this.quantity - (shift ? 10 : 1)) & 0xff;
    if (q <= 1 || q > 99) q = 1;
    this.quantity = q;
    this.updateText();
  }
}

export class GameShopWindow {
  /** GameScene.GameShopInfoList. */
  readonly items: GameShopItem[] = [];
  private classFilter = SHOW_ALL;
  private typeFilter = SHOW_ALL;
  private sectionFilter: Section = "Show All";
  private page = 0;
  private startIndex = 0;
  /** CStartIndex: the first category row shown. */
  private cStartIndex = 0;
  private maxPage = 1;
  private categoryList: string[] = [];
  private readonly stage: HTMLElement;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly gold: () => number;
  private readonly credit: () => number;
  private readonly userClass: () => number;
  private readonly win: MirWindow;
  private readonly grid: GameShopCell[] = [];
  private readonly filters: MirLabel[] = [];
  private readonly totalGold: MirLabel;
  private readonly totalCredits: MirLabel;
  private readonly pageNumberLabel: MirLabel;
  private readonly positionBar: MirButton;
  private readonly search: MirTextBox;
  private readonly paymentTypeGold: MirCheckBox;
  private readonly paymentTypeCredit: MirCheckBox;
  private readonly allItems: MirButton;
  private readonly topItems: MirButton;
  private readonly deals: MirButton;
  private readonly newItems: MirButton;
  /** ALL / War / Sin / Tao / Wiz / Arch with their class filter and base Index (the selected one shows Index + 1). */
  private readonly classButtons: { button: MirButton; cls: string; index: number }[] = [];

  constructor(stage: HTMLElement, invWindow: InventoryWindow, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void,
    gold: () => number, credit: () => number, userClass: () => number) {
    this.stage = stage;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.gold = gold;
    this.credit = credit;
    this.userClass = userClass;

    // — GameShopDialog ctor —
    const win = this.win = new MirWindow({ library: Lib.Title, index: 749, movable: true, sort: true, location: "center", onHide: () => hideItemLabel() });
    new MirImage({ parent: win, library: Lib.Title, index: 26, location: [18, 9] }); // TitleLabel
    // UpdateShop Location: i < 4 ? (152 + i * 132, 115) : (152 + (i - 4) * 132, 275)
    for (let i = 0; i < CELLS; i++) this.grid.push(new GameShopCell(win, [152 + (i % 4) * 132, i < 4 ? 115 : 275], this));
    closeButton(win, [671, 4], () => this.toggle(false));
    this.totalGold = new MirLabel({ parent: win, size: [100, 20], align: "right", location: [123, 449], notControl: true });
    this.totalCredits = new MirLabel({ parent: win, size: [100, 20], align: "right", location: [5, 449], notControl: true });
    new MirButton({ parent: win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], location: [120, 103], sound: SoundList.ButtonA,
      onClick: () => { // UpButton
        if (this.cStartIndex <= 0) return;
        this.cStartIndex--;
        this.setCategories();
        this.updatePositionBar();
      } });
    new MirButton({ parent: win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], location: [120, 421], sound: SoundList.ButtonA,
      onClick: () => { // DownButton
        if (this.cStartIndex + FILTERS >= this.categoryList.length) return;
        this.cStartIndex++;
        this.setCategories();
        this.updatePositionBar();
      } });
    this.positionBar = new MirButton({ parent: win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [120, 117], sound: 0 });
    dragY(this.positionBar, (y) => this.positionBarMoving(y));
    const filterBackground = new MirImage({ parent: win, library: Lib.Title, index: 769, location: [11, 102] });
    filterBackground.el.addEventListener("wheel", (e) => this.filterScrolling(e));
    this.search = new MirTextBox({ parent: win, backColour: "rgb(4,4,4)", foreColour: "#ffffff", size: [140, 16], location: [540, 69], font: "9pt Arial", maxLength: 23 });
    this.search.input.addEventListener("keyup", () => this.getCategories()); // Search.TextBox.KeyUp
    this.paymentTypeGold = new MirCheckBox({ parent: win, library: Lib.Prguse, tickedIndex: 2087, unTickedIndex: 2086, location: [250, 449], checked: true,
      labelText: "Buy with Gold", hint: "Buy item(s) with Gold.", onChange: () => this.refreshPayType(1) });
    this.paymentTypeCredit = new MirCheckBox({ parent: win, library: Lib.Prguse, tickedIndex: 2087, unTickedIndex: 2086, location: [340, 449],
      labelText: "Buy with Credits", hint: "Buy item(s) with Credits.", onChange: () => this.refreshPayType(0) });
    const tab = (index: number, x: number, section: Section, visible = true) => new MirButton({ parent: win, library: Lib.Title, index, location: [x, 68], visible,
      sound: SoundList.ButtonA, onClick: () => {
        this.sectionFilter = section;
        this.resetTabs();
        this.getCategories();
      } });
    this.allItems = tab(770, 138, "Show All");
    this.topItems = tab(776, 209, "TopItems");
    this.deals = tab(772, 280, "DealItems");
    this.newItems = tab(774, 351, "NewItems", false);
    const classes: [string, number, Point][] = [[SHOW_ALL, 751, [539, 37]], ["Warrior", 754, [568, 38]], ["Assassin", 757, [591, 38]], ["Taoist", 760, [614, 38]],
      ["Wizard", 763, [637, 38]], ["Archer", 766, [660, 38]]];
    for (const [cls, index, location] of classes) {
      const button = new MirButton({ parent: win, library: Lib.Title, index, hoverIndex: index + 1, pressedIndex: index + 2, location,
        onClick: () => {
          this.classFilter = cls;
          this.typeFilter = SHOW_ALL;
          this.getCategories();
          this.resetClass();
        } });
      this.classButtons.push({ button, cls, index });
    }
    this.pageNumberLabel = new MirLabel({ parent: win, size: [83, 17], location: [597, 446], center: true, font: FONT_7 });
    new MirButton({ parent: win, library: Lib.Prguse2, index: 240, hoverIndex: 241, pressedIndex: 242, location: [600, 448], sound: SoundList.ButtonA,
      onClick: () => { // PreviousButton
        this.page = Math.max(0, this.page - 1);
        this.startIndex = CELLS * this.page;
        this.updateShop();
      } });
    new MirButton({ parent: win, library: Lib.Prguse2, index: 243, hoverIndex: 244, pressedIndex: 245, location: [660, 448], sound: SoundList.ButtonA,
      onClick: () => { // NextButton
        this.page++;
        if (this.page + 1 > this.maxPage) this.page--;
        this.startIndex = CELLS * this.page;
        this.updateShop();
      } });
    for (let i = 0; i < FILTERS; i++) {
      const f = new MirLabel({ parent: win, size: [90, 20], location: [15, 103 + 15 * i], foreColour: GRAY, font: FONT_7 });
      f.onClick(() => { // Filters[i].Click
        this.typeFilter = f.text;
        this.page = 0;
        this.startIndex = 0;
        this.updateShop();
        for (const p of this.filters) p.el.style.color = p.text === f.text ? FILTER_SELECTED : GRAY;
      });
      f.el.addEventListener("pointerenter", () => {
        for (const p of this.filters) if (p.text === f.text && p.text !== this.typeFilter) p.el.style.color = FILTER_HOVER;
      });
      f.el.addEventListener("pointerleave", () => {
        for (const p of this.filters) if (p.text === f.text && p.text !== this.typeFilter) p.el.style.color = GRAY;
      });
      f.el.addEventListener("wheel", (e) => this.filterScrolling(e));
      this.filters.push(f);
    }

    const signal = gameSignal();
    signal.addEventListener("abort", () => win.dispose());
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.GameShop)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall)) this.toggle(false);
    }, { signal });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** GameShopDialog.New.Visible (an item dated within the last 7 days was listed). */
  get hasNewItems(): boolean {
    return this.newItems.visible;
  }

  /** The item label context (InventoryWindow's, for the cells). */
  labelContext() {
    return this.invWindow.labelContext();
  }

  /** KeybindOptions.GameShop: Show / Hide. */
  toggle(show = !this.win.visible): void {
    if (show === this.win.visible) return;
    if (!show) { this.win.hide(); return; }
    // GameShopDialog.Show
    this.win.show();
    this.classFilter = MirClassName[this.userClass()] ?? SHOW_ALL;
    this.sectionFilter = "Show All";
    this.resetTabs();
    this.resetClass();
    this.getCategories();
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GameShopInfo": // GameScene.GameShopUpdate
        p.Item.Stock = p.StockLevel;
        this.items.push(p.Item);
        if (isNew(p.Item)) this.newItems.visible = true;
        return true;
      case "GameShopStock": // GameScene.GameShopStock
        for (let i = 0; i < this.items.length; i++) {
          const item = this.items[i]!;
          if (item.GIndex !== p.GIndex) continue;
          if (p.StockLevel === 0) { this.items.splice(i, 1); i--; }
          else item.Stock = p.StockLevel;
          if (this.visible) this.updateShop();
        }
        return true;
      default:
        // GameShopDialog.Process: totalCredits / totalGold (refreshed on any packet while open; gold and credit
        // change only through packets). Not consumed: Inventory handles GainedGold / GainedCredit / ...
        if (this.visible) this.updateMoney();
        return false;
    }
  }

  /** GameShopDialog.Process. */
  private updateMoney(): void {
    this.totalCredits.text = n0(this.credit());
    this.totalGold.text = n0(this.gold());
  }

  /** PType_Clicked -> RefreshPayType: 0 = credits, 1 = gold (one is always checked). */
  private refreshPayType(idx: number): void {
    this.paymentTypeCredit.checked = idx === 0;
    this.paymentTypeGold.checked = idx === 1;
  }

  /** GameShopDialog.ResetTabs. */
  private resetTabs(): void {
    this.allItems.index = this.sectionFilter === "Show All" ? 771 : 770;
    this.topItems.index = this.sectionFilter === "TopItems" ? 777 : 776;
    this.deals.index = this.sectionFilter === "DealItems" ? 773 : 772;
    this.newItems.index = this.sectionFilter === "NewItems" ? 775 : 774;
  }

  /** GameShopDialog.ResetClass. */
  private resetClass(): void {
    for (const c of this.classButtons) c.button.index = c.cls === this.classFilter ? c.index + 1 : c.index;
  }

  /** FilterScrolling (mouse wheel over the category list). */
  private filterScrolling(e: WheelEvent): void {
    e.preventDefault();
    const count = e.deltaY < 0 ? 1 : e.deltaY > 0 ? -1 : 0; // e.Delta / MouseWheelScrollDelta
    if (this.cStartIndex === 0 && count >= 0) return;
    if (this.cStartIndex === this.categoryList.length - 1 && count <= 0) return;
    if (this.categoryList.length <= FILTERS) return;
    this.cStartIndex -= count;
    if (this.cStartIndex < 0) this.cStartIndex = 0;
    if (this.cStartIndex + FILTERS > this.categoryList.length - 1) this.cStartIndex = this.categoryList.length - FILTERS;
    this.setCategories();
    this.updatePositionBar();
  }

  /** UpdatePositionBar. */
  private updatePositionBar(): void {
    if (this.categoryList.length <= FILTERS) return;
    const interval = Math.trunc(290 / (this.categoryList.length - FILTERS));
    const y = Math.max(117, Math.min(401, 117 + this.cStartIndex * interval));
    this.positionBar.location = [120, y];
  }

  /** PositionBar_OnMoving. */
  private positionBarMoving(dragged: number): void {
    const y = Math.max(117, Math.min(401, dragged));
    if (this.categoryList.length > FILTERS) {
      const interval = Math.trunc(284 / (this.categoryList.length - FILTERS));
      if (interval > 0) { // the desktop divides by zero beyond 284 extra categories
        this.cStartIndex = Math.floor(Math.trunc((y - 117) / interval));
        this.cStartIndex = Math.min(this.cStartIndex, this.categoryList.length - FILTERS);
        this.setCategories();
      }
    }
    this.positionBar.location = [120, y];
  }

  /** The search filter of GetCategories / UpdateShop (FriendlyName contains the text, case-insensitive). */
  private shopList(): GameShopItem[] {
    const text = this.search.text.toLowerCase();
    if (text === "") return this.items;
    return this.items.filter((f) => itemFriendlyName(f.Info).toLowerCase().includes(text));
  }

  private matchesClass(item: GameShopItem): boolean {
    return item.Class === this.classFilter || item.Class === "All" || this.classFilter === SHOW_ALL;
  }

  private matchesSection(item: GameShopItem): boolean {
    const s = this.sectionFilter;
    return s === "Show All" || (s === "TopItems" && item.TopItem) || (s === "DealItems" && item.Deal) || (s === "NewItems" && isNew(item));
  }

  /** GameShopDialog.GetCategories. */
  private getCategories(): void {
    this.typeFilter = SHOW_ALL;
    this.page = 0;
    this.startIndex = 0;
    this.positionBar.location = [120, 117];
    const list: string[] = [SHOW_ALL];
    for (const item of this.shopList()) {
      if (list.includes(item.Category) || item.Category === "") continue;
      if (this.matchesClass(item) && this.matchesSection(item)) list.push(item.Category);
    }
    this.categoryList = list;
    this.cStartIndex = 0;
    this.setCategories();
    this.updateShop();
  }

  /** GameShopDialog.SetCategories. */
  private setCategories(): void {
    for (let i = 0; i < FILTERS; i++) {
      const f = this.filters[i];
      const text = i < this.categoryList.length ? this.categoryList[i + this.cStartIndex] ?? "" : "";
      f.text = text;
      f.el.style.color = text !== "" && text === this.typeFilter ? FILTER_SELECTED : GRAY;
      f.notControl = text === "";
    }
  }

  /** GameShopDialog.UpdateShop. */
  updateShop(): void {
    const filtered: GameShopItem[] = [];
    for (const item of this.shopList()) {
      if (!this.matchesClass(item)) continue;
      if (item.Category !== this.typeFilter && this.typeFilter !== SHOW_ALL) continue;
      if (this.matchesSection(item)) filtered.push(item);
    }
    this.maxPage = Math.max(1, Math.ceil(filtered.length / CELLS));
    this.pageNumberLabel.text = `${this.page + 1} / ${this.maxPage}`;
    const maxIndex = filtered.length - 1;
    if (this.startIndex > maxIndex) this.startIndex = maxIndex;
    if (this.startIndex < 0) this.startIndex = 0;
    filtered.sort((a, b) => itemFriendlyName(a.Info).localeCompare(itemFriendlyName(b.Info))); // OrderBy(FriendlyName)
    for (let i = 0; i < CELLS; i++) this.grid[i].set(filtered[i + this.startIndex] ?? null);
    this.updateMoney();
  }

  /** GameShopCell.BuyProduct: PType 0 = credit, 1 = gold (PlayerObject.GameshopBuy). */
  buyProduct(item: GameShopItem, quantity: number): void {
    let pType = -1;
    if (this.paymentTypeCredit.checked && item.CanBuyCredit) pType = 0;
    else if (this.paymentTypeGold.checked && item.CanBuyGold) pType = 1;
    if (pType === -1) { this.chat("You MUST select a payment type!", ChatType.System); return; } // YouMustSelectPaymentType
    const cost = (pType === 0 ? item.CreditPrice : item.GoldPrice) * quantity;
    if (cost > (pType === 0 ? this.credit() : this.gold())) { this.chat("You can't afford the selected item.", ChatType.System); return; }
    const name = itemFriendlyName(item.Info);
    // ConfirmBuyItemCredits / ConfirmPurchaseItemGold (the English Gold text has no space after "buy")
    const text = pType === 0
      ? `Are you sure would you like to buy ${quantity} x \n${name}(${item.Count}) for ${cost} Credits?`
      : `Are you sure would you like to buy${quantity} x \n${name}(${item.Count}) for ${cost} Gold?`;
    messageBox(this.stage, text, [
      { label: "Yes", onClick: () => this.send(ClientPackets.GameshopBuy({ GIndex: item.GIndex, Quantity: quantity, PType: pType })) },
      { label: "No" },
    ]);
  }
}
