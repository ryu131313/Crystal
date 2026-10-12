// Game Shop: GameShopDialog (Client/MirScenes/Dialogs/GameshopDialog.cs, DOM edition) + GameShopCell
// (Client/MirControls/MirGameShopCell.cs) + GameScene.GameShopUpdate / GameShopStock.
//   Section tabs (All Items / Top Items / Deals / New, New shown once an item dated within 7 days arrives), class
//   buttons (All, Warrior, Assassin, Taoist, Wizard, Archer; Show() starts on the user's class), name search, category
//   list (GetCategories / SetCategories), 4 x 2 cells a page sorted by name, credits + gold, payment type (gold default).
//   A cell: name, icon + item tooltip, count, stock (0 = unlimited), credit / gold price x quantity, quantity +/-
//   (Shift = 10; capped at 5 stacks, 99 and the stock), Buy (confirm() for the MirMessageBox, then C.GameshopBuy).
// The window owns GameScene.GameShopInfoList. Toggle with Y (KeybindOptions.GameShop) or the Shop HUD button.
// Not ported: the Preview button / GameShopViewer (draws the item on the user's paperdoll sprites), the category
// scroll bar (the category list scrolls instead), GameDataChanged shop pushes (the packet is not ported).

import type { GameShopItem, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, MirClassName } from "../protocol/enums.generated.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { type InventoryWindow, itemIcon } from "./inventoryWindow.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

const CELLS = 8; // GameShopDialog.Grid (4 x 2)
const NEW_DAYS = 7;
const SHOW_ALL = "Show All";
type Section = "Show All" | "TopItems" | "DealItems" | "NewItems";
const SECTIONS: [Section, string][] = [["Show All", "All Items"], ["TopItems", "Top Items"], ["DealItems", "Deals"], ["NewItems", "New"]];
const CLASSES: [string, string][] = [[SHOW_ALL, "All"], ["Warrior", "War"], ["Assassin", "Sin"], ["Taoist", "Tao"], ["Wizard", "Wiz"], ["Archer", "Arch"]];

/** "###,###,##0". */
const n0 = (n: number) => n.toLocaleString("en-US");

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

export class GameShopWindow {
  readonly hudButton: HTMLButtonElement;
  /** GameScene.GameShopInfoList. */
  readonly items: GameShopItem[] = [];
  private classFilter = SHOW_ALL;
  private typeFilter = SHOW_ALL;
  private sectionFilter: Section = "Show All";
  private page = 0;
  private startIndex = 0;
  private maxPage = 1;
  /** GameShopCell.Quantity per listed item (the desktop recreates the cells, so UpdateShop resets them to 1). */
  private readonly quantities = new Map<number, number>();
  private payCredit = false; // PaymentTypeCredit.Checked (PaymentTypeGold starts checked)
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly gold: () => number;
  private readonly credit: () => number;
  private readonly userClass: () => number;
  private readonly root = el("div", { class: "window shop-window", hidden: true });
  private readonly sectionButtons: HTMLButtonElement[];
  private readonly classButtons: HTMLButtonElement[];
  private readonly search = el("input", { type: "text", maxLength: 23, placeholder: "Search" });
  private readonly categories = el("div", { class: "shop-categories" });
  private readonly grid = el("div", { class: "shop-grid" });
  private readonly creditLabel = el("span", {});
  private readonly goldLabel = el("span", {});
  private readonly payGoldBox = el("input", { type: "radio", name: "shop-pay", checked: true });
  private readonly payCreditBox = el("input", { type: "radio", name: "shop-pay" });
  private readonly pageLabel = el("span", { class: "shop-page" });

  constructor(stage: HTMLElement, invWindow: InventoryWindow, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void,
    gold: () => number, credit: () => number, userClass: () => number) {
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.gold = gold;
    this.credit = credit;
    this.userClass = userClass;
    this.hudButton = el("button", { title: "Game Shop (Y)", onclick: () => this.toggle() }, "Shop") as HTMLButtonElement;
    // allItems / topItems / Deals / New .Click
    this.sectionButtons = SECTIONS.map(([section, label]) => el("button", {
      onclick: () => { this.sectionFilter = section; this.resetTabs(); this.getCategories(); },
    }, label) as HTMLButtonElement);
    this.sectionButtons[3]!.hidden = true; // New.Visible = false until GameShopUpdate sees a new item
    // ALL / War / Sin / Tao / Wiz / Arch .Click
    this.classButtons = CLASSES.map(([cls, label]) => el("button", {
      title: cls === SHOW_ALL ? "All classes" : cls,
      onclick: () => { this.classFilter = cls; this.typeFilter = SHOW_ALL; this.getCategories(); this.resetClass(); },
    }, label) as HTMLButtonElement);
    this.search.addEventListener("keyup", () => this.getCategories()); // Search.TextBox.KeyUp
    // PType_Clicked -> RefreshPayType
    this.payGoldBox.onchange = () => { this.payCredit = !this.payGoldBox.checked; };
    this.payCreditBox.onchange = () => { this.payCredit = this.payCreditBox.checked; };
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Game Shop"), el("button", { class: "window-close", title: "Close (Y)", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "row shop-top" }, el("div", { class: "shop-tabs" }, ...this.sectionButtons),
        el("div", { class: "shop-classes" }, ...this.classButtons), this.search),
      el("div", { class: "shop-body" }, this.categories, this.grid),
      el("div", { class: "row shop-foot" },
        el("span", { class: "shop-money" }, "Credits ", this.creditLabel), el("span", { class: "shop-money" }, "Gold ", this.goldLabel),
        el("label", { class: "shop-pay", title: "Buy item(s) with Gold." }, this.payGoldBox, "Buy with Gold"),
        el("label", { class: "shop-pay", title: "Buy item(s) with Credits." }, this.payCreditBox, "Buy with Credits"),
        el("button", { title: "Previous page", onclick: () => this.prevPage() }, "◀"), this.pageLabel,
        el("button", { title: "Next page", onclick: () => this.nextPage() }, "▶")),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("keydown", (e) => e.stopPropagation()); // typing in the search box is not game input
    stage.append(this.root);
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (matches(e, KeybindOptions.GameShop) && !(a instanceof HTMLInputElement) && !(a instanceof HTMLTextAreaElement)) this.toggle();
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** GameShopDialog.New.Visible (an item dated within the last 7 days was listed). */
  get hasNewItems(): boolean {
    return !this.sectionButtons[3]!.hidden;
  }

  /** KeybindOptions.GameShop: Show / Hide. */
  toggle(show = this.root.hidden): void {
    if (show === !this.root.hidden) return;
    this.root.hidden = !show;
    if (!show) return;
    // GameShopDialog.Show
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
        if (isNew(p.Item)) this.sectionButtons[3]!.hidden = false;
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

  private updateMoney(): void {
    this.creditLabel.textContent = n0(this.credit());
    this.goldLabel.textContent = n0(this.gold());
  }

  /** GameShopDialog.ResetTabs. */
  private resetTabs(): void {
    this.sectionButtons.forEach((b, i) => b.classList.toggle("on", SECTIONS[i]![0] === this.sectionFilter));
  }

  /** GameShopDialog.ResetClass. */
  private resetClass(): void {
    this.classButtons.forEach((b, i) => b.classList.toggle("on", CLASSES[i]![0] === this.classFilter));
  }

  /** The search filter of GetCategories / UpdateShop (FriendlyName contains the text, case-insensitive). */
  private shopList(): GameShopItem[] {
    const text = this.search.value.toLowerCase();
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

  /** GameShopDialog.GetCategories (+ SetCategories: every category is listed; the box scrolls). */
  private getCategories(): void {
    this.typeFilter = SHOW_ALL;
    this.page = 0;
    this.startIndex = 0;
    const list: string[] = [SHOW_ALL];
    for (const item of this.shopList()) {
      if (list.includes(item.Category) || item.Category === "") continue;
      if (this.matchesClass(item) && this.matchesSection(item)) list.push(item.Category);
    }
    this.categories.replaceChildren(...list.map((c) => {
      const label = el("div", { class: "shop-category", onclick: () => this.selectCategory(c) }, c);
      label.dataset["category"] = c;
      return label;
    }));
    this.categories.scrollTop = 0;
    this.renderCategories();
    this.updateShop();
  }

  /** Filters[i].Click. */
  private selectCategory(category: string): void {
    this.typeFilter = category;
    this.page = 0;
    this.startIndex = 0;
    this.updateShop();
    this.renderCategories();
  }

  private renderCategories(): void {
    for (const c of Array.from(this.categories.children) as HTMLElement[]) c.classList.toggle("on", c.dataset["category"] === this.typeFilter);
  }

  /** PreviousButton.Click. */
  private prevPage(): void {
    this.page = Math.max(0, this.page - 1);
    this.startIndex = CELLS * this.page;
    this.updateShop();
  }

  /** NextButton.Click. */
  private nextPage(): void {
    this.page++;
    if (this.page + 1 > this.maxPage) this.page--;
    this.startIndex = CELLS * this.page;
    this.updateShop();
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
    this.pageLabel.textContent = `${this.page + 1} / ${this.maxPage}`;
    const maxIndex = filtered.length - 1;
    if (this.startIndex > maxIndex) this.startIndex = maxIndex;
    if (this.startIndex < 0) this.startIndex = 0;
    filtered.sort((a, b) => itemFriendlyName(a.Info).localeCompare(itemFriendlyName(b.Info))); // OrderBy(FriendlyName)
    this.quantities.clear();
    const cells: HTMLElement[] = [];
    for (let i = 0; i < CELLS; i++) {
      const item = filtered[i + this.startIndex];
      cells.push(item ? this.cell(item) : el("div", { class: "shop-cell empty" }));
    }
    this.grid.replaceChildren(...cells);
    this.updateMoney();
  }

  /** GameShopCell (constructor + UpdateText + DrawControl). */
  private cell(item: GameShopItem): HTMLElement {
    const info = item.Info;
    const name = itemFriendlyName(info);
    const img = el("img", { class: "shop-icon", alt: "" });
    void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
    const quantity = el("span", { class: "shop-qty" });
    const creditPrice = el("span", {});
    const goldPrice = el("span", {});
    const update = () => { // GameShopCell.UpdateText
      const q = this.quantity(item);
      quantity.textContent = String(q);
      creditPrice.textContent = item.CanBuyCredit ? n0(item.CreditPrice * q) : "-";
      goldPrice.textContent = item.CanBuyGold ? n0(item.GoldPrice * q) : "-";
    };
    update();
    const c = el("div", { class: "shop-cell" },
      el("div", { class: "shop-name", title: name }, name.length > 17 ? name.substring(0, 17) : name),
      el("div", { class: "shop-mid" },
        el("div", { class: "inv-cell shop-icon-cell", title: this.invWindow.describe(showItem(item), info) }, img),
        el("div", { class: "shop-meta" },
          el("div", {}, `Count ${item.Count}`),
          el("div", {}, `Stock ${item.Stock === 0 ? "∞" : item.Stock}`),
          el("div", { class: "shop-qty-row" },
            el("button", { title: "Less (Shift: 10)", onclick: (e: MouseEvent) => { this.quantityDown(item, e.shiftKey); update(); } }, "-"),
            quantity,
            el("button", { title: "More (Shift: 10)", onclick: (e: MouseEvent) => { this.quantityUp(item, e.shiftKey); update(); } }, "+")))),
      el("div", { class: "shop-price" }, el("span", { class: "shop-price-label" }, "Credits"), creditPrice),
      el("div", { class: "shop-price" }, el("span", { class: "shop-price-label" }, "Gold"), goldPrice),
      el("button", { class: "shop-buy", onclick: () => this.buyProduct(item) }, "Buy"));
    if (isNew(item)) c.classList.add("new");
    return c;
  }

  private quantity(item: GameShopItem): number {
    return this.quantities.get(item.GIndex) ?? 1;
  }

  /** GameShopCell quantityUp.Click (Quantity is a byte). */
  private quantityUp(item: GameShopItem, shift: boolean): void {
    let q = (this.quantity(item) + (shift ? 10 : 1)) & 0xff;
    const stack = Math.max(1, item.Info.StackSize), count = Math.max(1, item.Count);
    if ((q * count) / stack > 5) q = Math.trunc((5 * stack) / count) > 99 ? 99 : Math.trunc((5 * stack) / count) & 0xff;
    if (q >= 99) q = 99;
    if (item.Stock !== 0 && q > item.Stock) q = item.Stock;
    this.quantities.set(item.GIndex, q);
  }

  /** GameShopCell quantityDown.Click. */
  private quantityDown(item: GameShopItem, shift: boolean): void {
    let q = (this.quantity(item) - (shift ? 10 : 1)) & 0xff;
    if (q <= 1 || q > 99) q = 1;
    this.quantities.set(item.GIndex, q);
  }

  /** GameShopCell.BuyProduct: PType 0 = credit, 1 = gold (PlayerObject.GameshopBuy). */
  private buyProduct(item: GameShopItem): void {
    let pType = -1;
    if (this.payCredit && item.CanBuyCredit) pType = 0;
    else if (!this.payCredit && item.CanBuyGold) pType = 1;
    if (pType === -1) { this.chat("You MUST select a payment type!", ChatType.System); return; }
    const q = this.quantity(item);
    const name = itemFriendlyName(item.Info);
    const cost = (pType === 0 ? item.CreditPrice : item.GoldPrice) * q;
    if (cost > (pType === 0 ? this.credit() : this.gold())) { this.chat("You can't afford the selected item.", ChatType.System); return; }
    // ConfirmBuyItemCredits / ConfirmPurchaseItemGold
    if (!confirm(`Are you sure would you like to buy ${q} x\n${name}(${item.Count}) for ${n0(cost)} ${pType === 0 ? "Credits" : "Gold"}?`)) return;
    this.send(ClientPackets.GameshopBuy({ GIndex: item.GIndex, Quantity: q, PType: pType }));
  }
}
