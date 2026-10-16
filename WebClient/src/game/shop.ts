// The game shop catalogue: GameScene.GameShopInfoList with GameScene.GameShopUpdate (S.GameShopInfo) and GameShopStock,
// GameShopDialog.New.Visible (an item dated within the last 7 days arrived) and GameShopCell.BuyProduct's send.
// S.GameDataChanged (gameData.ts) edits the list in place through GameDataHost.shopItems() (= editableItems()) and re-dispatches GameShopInfo.
// game.ts feeds handle(); the UI follows `added` / `stockChanged` and reads `items`. The money labels refresh is a window
// concern (GameShopDialog.Process).

import type { GameShopItem, ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { dateFromBinary } from "./itemChanges.ts";
import { Emitter } from "./emitter.ts";

const NEW_DAYS = 7;

/** GameScene.GameShopUpdate's `Date > CMain.Now.AddDays(-7)`. */
export function isNewShopItem(item: GameShopItem): boolean {
  return dateFromBinary(item.Date).getTime() > Date.now() - NEW_DAYS * 86400000;
}

/** The struck-through "was" gold price of `item` (GameShopItem.DealGoldPrice, DB v119), or 0 = none. */
export function dealGoldPrice(item: GameShopItem): number { return item.DealGoldPrice; }
/** The struck-through "was" credit price of `item` (GameShopItem.DealCreditPrice, DB v119), or 0 = none. */
export function dealCreditPrice(item: GameShopItem): number { return item.DealCreditPrice; }
/** True when `item` carries a "was" price in either currency (the new UI strikes it through next to the price). */
export function hasDealPrice(item: GameShopItem): boolean { return item.DealGoldPrice > 0 || item.DealCreditPrice > 0; }

/** PlayerObject.GameshopBuy PType. */
export const ShopPayType = { Credit: 0, Gold: 1 } as const;

export class Shop {
  /** GameScene.GameShopInfoList (arrival order; GameDataChanged removes / re-adds rows in place). */
  private readonly _items: GameShopItem[] = [];

  /** GameScene.GameShopInfo: every item the shop holds. */
  get items(): readonly GameShopItem[] { return this._items; }
  /** The same array, writable: only for gameData.ts (S.GameDataChanged edits it in place via GameDataHost.shopItems()). */
  editableItems(): GameShopItem[] { return this._items; }
  /** GameScene.GameShopUpdate: `item` was appended (after `hasNewItems` is set). */
  readonly added = new Emitter<{ item: GameShopItem }>();
  /** GameScene.GameShopStock: a listed item's stock changed or it sold out (removed). */
  readonly stockChanged = new Emitter<void>();
  private _hasNewItems = false;

  constructor(private readonly send: (f: Uint8Array) => void) {}

  /** GameShopDialog.New.Visible: an item dated within the last 7 days was listed (never cleared, like the desktop). */
  get hasNewItems(): boolean { return this._hasNewItems; }

  /** GameShopCell.BuyProduct's Yes: C.GameshopBuy (pType: ShopPayType). */
  buy(gIndex: number, quantity: number, pType: number): void {
    this.send(ClientPackets.GameshopBuy({ GIndex: gIndex, Quantity: quantity, PType: pType }));
  }

  /** Server packets; true = consumed (the classic chain returned on both). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GameShopInfo": // GameScene.GameShopUpdate
        p.Item.Stock = p.StockLevel;
        this._items.push(p.Item);
        if (isNewShopItem(p.Item)) this._hasNewItems = true;
        this.added.emit({ item: p.Item });
        return true;
      case "GameShopStock": { // GameScene.GameShopStock
        let found = false;
        for (let i = 0; i < this._items.length; i++) {
          const item = this._items[i]!;
          if (item.GIndex !== p.GIndex) continue;
          if (p.StockLevel === 0) { this._items.splice(i, 1); i--; }
          else item.Stock = p.StockLevel;
          found = true;
        }
        if (found) this.stockChanged.emit(); // the desktop refreshes an open shop per matching row (same result)
        return true;
      }
    }
    return false;
  }
}
