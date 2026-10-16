// The NPC session: GameScene.NPCID / NPCTime / NPCRate / HideAddedStoreStats, NPCDialog.Visible and the page it shows
// (S.NPCResponse parsed by npcMarkup.ts), the goods lists (NPCGoodsDialog / NPCSubGoodsDialog / NPCCraftGoodsDialog,
// S.NPCPearlGoods) and the shared NPCDropDialog's target, type and lock (NpcDrop below). GameScene's NPC handlers:
// NPCResponse, NPCUpdate, NPCGoods, NPCPearlGoods, NPCSell, NPCRepair, NPCSRepair, NPCConsign, NPCRefine, NPCCheckRefine,
// NPCCollectRefine, NPCReplaceWedRing, NPCDisassemble, NPCDowngrade, NPCReset, RefineItem.
// game.ts feeds handle() (never consumes); the UI follows the events: `opened` / `page` (show the page), `closed`
// (NPCDialog.Hide: every NPC panel goes with it), `goods` (show a goods list), `refine` (show the RefineDialog) and
// the drop panel's own `shown` / `hidden` / `changed` / `consignPrice`. Talks are gated like GameScene.NPCTime (5 s
// until the server answers).

import type { ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { BindMode, ItemGrade, ItemType, MarketPanelType, PanelType } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { addedCount, awakeLevel, itemPrice, refineCost, repairCost, sellPrice, specialRepairCost } from "./items.ts";
import { itemFriendlyName } from "./gameInfo.ts";
import { Emitter } from "./emitter.ts";
import { EMPTY_PAGE, parsePage, type NpcPage } from "./npcMarkup.ts";
import { MIN_CONSIGNMENT } from "./market.ts"; // Globals.MinConsignment (defined once, in market.ts)

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  LowGold: "Not enough gold.",
  NotEnoughPearls: "You do not have enough Pearls.",
  YouNoBagSpace: "You do not have enough space.",
  CannotSellItem: "Cannot sell this item.",
  CannotCarryMoreGold: "Cannot carry anymore gold.",
  CannotRepairItem: "Cannot repair this item.",
  CannotConsignItem: "Cannot consign this item.",
  YouDontHaveEnoughGoldToRefine: (n: string) => `You don't have enough gold to refine your ${n}.`,
  YouHaventDepositedItemsToRefine: (n: string) => `You haven't deposited any items to refine your ${n} with.`,
  ItemHasntBeenRefinedNoChecking: (n: string) => `Your ${n} hasn't been refined so it doesn't need checking.`,
  ItemIsNotRing: (n: string) => `${n} isn't a ring.`,
};

/** The PanelTypes NPCDropDialog serves. */
export type DropType = typeof PanelType.Sell | typeof PanelType.Repair | typeof PanelType.SpecialRepair | typeof PanelType.Consign
  | typeof PanelType.Refine | typeof PanelType.CheckRefine | typeof PanelType.ReplaceWedRing
  | typeof PanelType.Disassemble | typeof PanelType.Downgrade | typeof PanelType.Reset;

/** A goods list the server sent (S.NPCGoods Buy / BuySub / Craft, S.NPCPearlGoods = Buy with usePearls). */
export interface NpcGoods { readonly type: number; readonly list: readonly UserItem[]; readonly usePearls: boolean }

/** NPCGoodsDialog.NewGoods + AddGoods: `goods` = every entry of a normal Buy list (empty otherwise), `display` = the cells
 *  (a normal Buy list shows one entry per ItemIndex; BuySub is sorted by price). */
export function goodsDisplay(type: number, usePearls: boolean, list: readonly UserItem[], info: (item: UserItem) => ItemInfo | undefined): { goods: UserItem[]; display: UserItem[] } {
  const goods: UserItem[] = [];
  const display: UserItem[] = [];
  if (type === PanelType.BuySub) list = [...list].sort((a, b) => itemPrice(a, info(a)) - itemPrice(b, info(b)));
  for (const item of list) {
    // Normal shops just want to show one of each item type
    if (type === PanelType.Buy && !usePearls) {
      goods.push(item);
      if (display.some((x) => x.ItemIndex === item.ItemIndex)) continue;
    }
    display.push(item);
  }
  return { goods, display };
}

/** NPCGoodsDialog.BuyItem's checks: a message, the MirAmountBox for a stack (max, initial), or one item. */
export type BuyPlan =
  | { readonly kind: "none" }
  | { readonly kind: "fail"; readonly text: string }
  | { readonly kind: "amount"; readonly image: number; readonly max: number; readonly initial: number }
  | { readonly kind: "one" };

interface NpcDeps {
  send: (f: Uint8Array) => void;
  now: () => number;
  inventory: Inventory;
  /** A system chat line (the drop panel's and the goods list's refusals). */
  chat: (text: string) => void;
  /** UserObject.PearlCount (not ported yet): null = unknown, the server decides. */
  pearls?: () => number | null;
}

/** NPCDropDialog's state: PType, TargetItem (its bag cell is OldCell, locked while it sits in the cell), Hold and the
 *  rate the panel was opened with. The UI draws the panel and answers `consignPrice` with consign(). */
export class NpcDrop {
  private _type: DropType = PanelType.Sell;
  private _rate = 1;
  private _hold = false;
  private _open = false;
  private target: bigint | null = null; // TargetItem
  /** NPCDropDialog.Show (after the type / rate are set). */
  readonly shown = new Emitter<{ type: DropType }>();
  /** NPCDropDialog.Hide (the target has been unlocked). */
  readonly hidden = new Emitter<void>();
  /** The target or Hold changed: redraw. */
  readonly changed = new Emitter<void>();
  /** Confirm on a Consign panel: ask the price (MirAmountBox ConsignmentPrice, MIN_CONSIGNMENT .. MAX_CONSIGNMENT), then consign(). */
  readonly consignPrice = new Emitter<{ item: UserItem; image: number }>();

  constructor(private readonly deps: NpcDeps) {}

  /** NPCDropDialog.PType. */
  get type(): DropType { return this._type; }
  /** GameScene.NPCRate as the panel last got it (Repair / SRepair / Refine / ReplaceWedRing pass one). */
  get rate(): number { return this._rate; }
  /** NPCDropDialog.Hold (Sell / Repair / SRepair / Consign: a targeted item is confirmed at once). */
  get hold(): boolean { return this._hold; }
  /** NPCDropDialog.Visible. */
  get open(): boolean { return this._open; }
  /** TargetItem's UniqueID. */
  get targetId(): bigint | null { return this.target; }

  private get inv(): Inventory { return this.deps.inventory; }

  /** TargetItem, or null; a target that has left the bag is dropped (NPCDropPanel_BeforeDraw draws an empty cell). */
  currentItem(): UserItem | null {
    const item = this.item();
    if (!item && this.target !== null) this.target = null;
    return item;
  }

  /** NPCDropDialog.Show (GameScene.NPCSell / NPCRepair / ... set PType first). */
  show(type: DropType, rate?: number): void {
    this.clearTarget();
    this._type = type;
    if (rate !== undefined) this._rate = rate;
    this._hold = false;
    this._open = true;
    this.shown.emit({ type });
  }

  /** NPCDropDialog.Hide: the targeted cell unlocks. */
  hide(): void {
    this.clearTarget();
    this._open = false;
    this.hidden.emit();
  }

  /** HoldButton. */
  toggleHold(): void {
    this._hold = !this._hold;
    this.changed.emit();
  }

  private item(): UserItem | null {
    if (this.target === null) return null;
    return this.inv.inventory.find((i) => i?.UniqueID === this.target) ?? null;
  }

  private setLock(id: bigint, on: boolean): void {
    if (on) this.inv.locked.add(String(id)); else this.inv.locked.delete(String(id));
  }

  private clearTarget(): void {
    if (this.target !== null) { this.setLock(this.target, false); this.target = null; this.inv.changed(); }
  }

  private friendly(item: UserItem): string {
    const info = this.inv.info(item);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }

  /** NPCDropDialog.ItemCell_Click with the bag slot `index` (-1 = nothing picked). */
  take(index: number): void {
    this.clearTarget();
    const item = index >= 0 ? this.inv.inventory[index] ?? null : null;
    if (!item || this.inv.isLocked(item)) { this.changed.emit(); return; }
    const info = this.inv.info(item);
    const type = this._type;
    if (type === PanelType.Disassemble || type === PanelType.Downgrade || type === PanelType.Reset) {
      const ok = type === PanelType.Disassemble ? !!info && info.Grade !== ItemGrade.None && info.Type !== ItemType.Awakening
        : type === PanelType.Downgrade ? awakeLevel(item) !== 0
        : addedCount(item) > 0 || (!!info && item.Slots.length > info.Slots); // UserItem.IsAdded
      if (ok) { this.target = item.UniqueID; this.setLock(item.UniqueID, true); this.inv.changed(); }
      this.changed.emit();
      return;
    }
    if (type !== PanelType.Sell && type !== PanelType.Consign && info && info.Durability === 0) { this.changed.emit(); return; }
    this.target = item.UniqueID;
    this.setLock(item.UniqueID, true);
    this.inv.changed();
    if (this._hold) this.confirm();
    else this.changed.emit();
  }

  /** NPCDropDialog.Confirm. */
  confirm(): void {
    const item = this.item();
    if (!item) return;
    const inv = this.inv;
    const chat = this.deps.chat;
    const info = inv.info(item);
    const bind = info?.Bind ?? 0;
    const index = inv.inventory.indexOf(item);
    switch (this._type) {
      case PanelType.Sell:
        if (bind & BindMode.DontSell) { chat(T.CannotSellItem); return; }
        if (inv.gold + sellPrice(item, info) > 0xffffffff) { chat(T.CannotCarryMoreGold); break; }
        this.release(); // the request locks it again until S.SellItem
        inv.npcAction(index, "sell");
        return;
      case PanelType.Repair:
        if (bind & BindMode.DontRepair) { chat(T.CannotRepairItem); return; }
        if (inv.gold < repairCost(item, info, this._rate)) { chat(T.LowGold); break; }
        this.release();
        inv.npcAction(index, "repair");
        return;
      case PanelType.SpecialRepair:
        if ((bind & BindMode.DontRepair) || (bind & BindMode.NoSRepair)) { chat(T.CannotRepairItem); return; }
        if (inv.gold < specialRepairCost(item, info, this._rate)) { chat(T.LowGold); break; }
        this.release();
        inv.npcAction(index, "srepair");
        return;
      case PanelType.Consign:
        if ((bind & BindMode.DontStore) || (bind & BindMode.DontSell)) { chat(T.CannotConsignItem); return; }
        this.consignPrice.emit({ item, image: info?.Image ?? -1 });
        return;
      case PanelType.Disassemble: this.deps.send(ClientPackets.DisassembleItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Downgrade: this.deps.send(ClientPackets.DowngradeAwakening({ UniqueID: item.UniqueID })); break;
      case PanelType.Reset: if (info && !info.NeedIdentify) this.deps.send(ClientPackets.ResetAddedItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Refine:
        if (inv.refine.some((i) => i)) {
          if (inv.gold >= refineCost(info, this._rate)) {
            this.deps.send(ClientPackets.RefineItem({ UniqueID: item.UniqueID }));
            this.target = null; // stays locked: S.RepairItem unlocks it, S.RefineItem removes it
            this.changed.emit();
            return;
          }
          chat(T.YouDontHaveEnoughGoldToRefine(this.friendly(item)));
          return;
        }
        chat(T.YouHaventDepositedItemsToRefine(this.friendly(item)));
        break;
      case PanelType.CheckRefine:
        if (item.RefineAdded === 0) { chat(T.ItemHasntBeenRefinedNoChecking(this.friendly(item))); return; }
        this.deps.send(ClientPackets.CheckRefine({ UniqueID: item.UniqueID }));
        break;
      case PanelType.ReplaceWedRing:
        if (info?.Type !== ItemType.Ring) { chat(T.ItemIsNotRing(this.friendly(item))); return; }
        this.deps.send(ClientPackets.ReplaceWedRing({ UniqueID: item.UniqueID }));
        break;
    }
    this.clearTarget();
    this.changed.emit();
  }

  /** The Consign price box's answer: C.ConsignItem (Type = Consign) if the item is still the target. */
  consign(item: UserItem, price: number): void {
    if (this.target !== item.UniqueID || price < MIN_CONSIGNMENT) return;
    this.release();
    this.inv.consign(item, price, MarketPanelType.Consign); // locked until S.ConsignItem
  }

  /** TargetItem = null with the lock handed over to the request. */
  private release(): void {
    if (this.target !== null) this.setLock(this.target, false);
    this.target = null;
  }
}

export class NpcSession {
  private _npcId = 0;
  private _npcName = "";
  private npcTime = 0; // GameScene.NPCTime
  private _rate = 1;   // GameScene.NPCRate
  // The classic web windows kept two copies of NPCRate (old NpcWindow.rate priced Buy / BuySub, old NpcServices.rate
  // priced Craft), each set by its own packets; kept apart so the classic lists reprice exactly as before.
  private _goodsRate = 1;
  private _craftRate = 1;
  private _hideAdded = false; // GameScene.HideAddedStoreStats
  private _open = false; // NPCDialog.Visible
  private _page: NpcPage = EMPTY_PAGE;
  private _lastPageKey = "";
  private _usePearls = false;
  private readonly goodsLists = new Map<number, readonly UserItem[]>();

  /** NPCDropDialog (shared by Sell / Repair / SRepair / Consign and the item services). */
  readonly drop: NpcDrop;
  /** The dialog opens (before the first `page`). */
  readonly opened = new Emitter<void>();
  /** GameScene.NPCResponse with something to show: the new page (after `opened`). */
  readonly page = new Emitter<NpcPage>();
  /** NPCDialog.Hide (also an empty page): every NPC panel hides with it. Fires even when the dialog was already hidden. */
  readonly closed = new Emitter<void>();
  /** GameScene.NPCGoods / NPCPearlGoods while the dialog is open: show the list. */
  readonly goods = new Emitter<NpcGoods>();
  /** GameScene.NPCRefine (not refining, dialog open): the RefineDialog shows (after the drop panel). */
  readonly refine = new Emitter<void>();

  constructor(private readonly deps: NpcDeps) {
    this.drop = new NpcDrop(deps);
  }

  /** GameScene.NPCID (the NPC the dialog talks to; S.NPCUpdate can change it). */
  get npcId(): number { return this._npcId; }
  /** The clicked NPC's name (NPCDialog.NameLabel shows the part before "_"). */
  get npcName(): string { return this._npcName; }
  /** GameScene.NPCRate (the last rate any NPC packet sent: NPCGoods, NPCPearlGoods, NPCRepair, NPCSRepair, NPCRefine,
   *  NPCReplaceWedRing while open). */
  get rate(): number { return this._rate; }
  /** The Buy / BuySub lists' rate: NPCGoods (any type), NPCRepair, NPCSRepair, NPCPearlGoods (old NpcWindow.rate). */
  get goodsRate(): number { return this._goodsRate; }
  /** The Craft list's rate: NPCRefine, NPCReplaceWedRing (open), NPCGoods Craft, NPCPearlGoods (old NpcServices.rate). */
  get craftRate(): number { return this._craftRate; }
  /** GameScene.HideAddedStoreStats (the goods' item labels). */
  get hideAdded(): boolean { return this._hideAdded; }
  /** NPCDialog.Visible. */
  get open(): boolean { return this._open; }
  /** The page on show (NPCDialog.CurrentLines + GameScene.BigButtons). */
  get currentPage(): NpcPage { return this._page; }
  /** The Key of the last C.CallNPC sent, without the brackets ("@Main", "@BUYBACK", ...): the buy-back list arrives as
   *  a plain PanelType.Buy S.NPCGoods, so a UI that labels it needs this. */
  get lastPageKey(): string { return this._lastPageKey; }
  /** The list last shown for PanelType Buy / BuySub / Craft (empty before one arrives). */
  goodsList(type: number): readonly UserItem[] { return this.goodsLists.get(type) ?? []; }
  /** The Buy list is the pearl shop (S.NPCPearlGoods: NPCGoodsDialog.UsePearls). */
  get usePearls(): boolean { return this._usePearls; }

  private callNpc(id: number, key: string): void {
    this._lastPageKey = key;
    this.deps.send(ClientPackets.CallNPC({ ObjectID: id, Key: `[${key}]` }));
  }

  /** GameScene MapControl click on an NPC: C.CallNPC "[@Main]" (not again within 5 s, or while its dialog is open). */
  talk(id: number, name: string): void {
    const t = this.deps.now();
    if (id === this._npcId && (t <= this.npcTime || this._open)) return;
    this.npcTime = t + 5000;
    this._npcId = id;
    this._npcName = name;
    this.callNpc(id, "@Main");
  }

  /** RollDialog.ReturnResult: CallNPC "[page]" on the last NPC (GameScene.NPCID), gated by NPCTime like a dialog link. */
  callPage(page: string): void {
    const t = this.deps.now();
    if (t <= this.npcTime) return;
    this.npcTime = t + 5000;
    this.callNpc(this._npcId, page);
  }

  /** NPCDialog.ButtonClicked: a page link or BigButton ("@Exit", any case, closes the dialog). */
  call(key: string): void {
    if (key.toLowerCase() === "@exit") { this.close(); return; }
    this.callPage(key);
  }

  /** NPCDialog.Hide: the drop panel, then everything listening to `closed`. */
  close(): void {
    this._open = false;
    this.drop.hide();
    this.closed.emit();
  }

  /** NPCGoodsDialog.BuyItem's checks for `item` (the Buy / BuySub / pearl list's selected cell). */
  buyPlan(item: UserItem, usePearls: boolean): BuyPlan {
    const inv = this.deps.inventory;
    const info = inv.info(item);
    if (!info) return { kind: "none" };
    if (info.StackSize > 1) {
      let maxQuantity = info.StackSize;
      const full = { ...item, Count: info.StackSize };
      const fullPrice = itemPrice(full, info);
      const unit = Math.max(1, fullPrice / full.Count);
      if (usePearls) {
        const pearls = this.pearls();
        if (pearls !== null && fullPrice > pearls) {
          maxQuantity = Math.min(0xffff, Math.trunc(pearls / unit));
          if (maxQuantity === 0) return { kind: "fail", text: T.NotEnoughPearls };
        }
      } else if (fullPrice > inv.gold) {
        maxQuantity = Math.min(0xffff, Math.trunc(inv.gold / unit));
        if (maxQuantity === 0) return { kind: "fail", text: T.LowGold };
      }
      const gain = this.maxGain(full, info.StackSize);
      if (gain === 0) return { kind: "fail", text: T.YouNoBagSpace };
      if (gain < maxQuantity) maxQuantity = gain;
      return { kind: "amount", image: info.Image, max: maxQuantity, initial: Math.min(item.Count, maxQuantity) };
    }
    if (usePearls) {
      const pearls = this.pearls();
      if (pearls !== null && info.Price > pearls) return { kind: "fail", text: T.NotEnoughPearls };
    } else if (info.Price > inv.gold) return { kind: "fail", text: T.LowGold };
    if (!inv.inventory.some((i) => !i)) return { kind: "fail", text: T.YouNoBagSpace };
    return { kind: "one" };
  }

  /** C.BuyItem (Type Buy; the server answers with the bag packets). */
  buy(item: UserItem, count: number): void {
    this.deps.send(ClientPackets.BuyItem({ ItemIndex: item.UniqueID, Count: count, Type: PanelType.Buy }));
  }

  private pearls(): number | null {
    return this.deps.pearls ? this.deps.pearls() : null;
  }

  /** User.GetMaxGain for a bag purchase (bag space only: free slots and room in matching stacks; weight is the server's). */
  private maxGain(item: UserItem, stack: number): number {
    let room = 0;
    for (const slot of this.deps.inventory.inventory) {
      if (!slot) room += stack;
      else if (slot.ItemIndex === item.ItemIndex && slot.Count < stack) room += stack - slot.Count;
      if (room >= item.Count) return item.Count;
    }
    return Math.min(room, item.Count);
  }

  private showGoods(type: number, list: readonly UserItem[], usePearls: boolean): void {
    this.goodsLists.set(type, list);
    if (type === PanelType.Buy) this._usePearls = usePearls;
    this.goods.emit({ type, list, usePearls });
  }

  /** Server packets (after Inventory / ItemChanges). Never consumes (the classic chain went on to the other handlers). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NPCResponse": {
        // GameScene.NPCResponse: NPCTime = 0, the goods / drop panels hide, then the page shows (or the dialog hides)
        this.npcTime = 0;
        this.drop.hide();
        const page = parsePage(p.Page);
        if (page.lines.length > 0 || page.bigButtons.length > 0) {
          this._page = page;
          const wasOpen = this._open;
          this._open = true;
          if (!wasOpen) this.opened.emit();
          this.page.emit(page);
        } else {
          this._page = EMPTY_PAGE;
          this.close();
        }
        break;
      }
      case "NPCUpdate": this._npcId = p.NPCID; break;
      case "NPCGoods":
        // GameScene.NPCGoods (Buy / BuySub: NPCGoodsDialog / NPCSubGoodsDialog; Craft: NPCCraftGoodsDialog)
        this._rate = this._goodsRate = p.Rate;
        if (p.Type === PanelType.Craft) this._craftRate = p.Rate;
        this._hideAdded = p.HideAddedStats;
        if (!this._open) break;
        if (p.Type === PanelType.Buy || p.Type === PanelType.BuySub || p.Type === PanelType.Craft) this.showGoods(p.Type, p.List, false);
        break;
      // GameScene.NPCPearlGoods: NPCGoodsDialog with UsePearls
      case "NPCPearlGoods":
        this._rate = this._goodsRate = this._craftRate = p.Rate;
        if (this._open) this.showGoods(PanelType.Buy, p.List, true);
        break;
      case "NPCSell": if (this._open) this.drop.show(PanelType.Sell); break;
      case "NPCRepair": this._rate = this._goodsRate = p.Rate; if (this._open) this.drop.show(PanelType.Repair, p.Rate); break;
      case "NPCSRepair": this._rate = this._goodsRate = p.Rate; if (this._open) this.drop.show(PanelType.SpecialRepair, p.Rate); break;
      case "NPCConsign": if (this._open) this.drop.show(PanelType.Consign); break; // GameScene.NPCConsign
      // GameScene.NPCRefine
      case "NPCRefine":
        this._rate = this._craftRate = p.Rate;
        if (!this._open) break;
        if (p.Refining) { this.drop.hide(); this.close(); }
        else { this.drop.show(PanelType.Refine, p.Rate); this.refine.emit(); } // NPCDropDialog BeforeDraw: RefineDialog.Show()
        break;
      case "NPCCheckRefine": if (this._open) this.drop.show(PanelType.CheckRefine); break; // GameScene.NPCCheckRefine
      case "NPCCollectRefine": if (this._open) this.close(); break; // GameScene.NPCCollectRefine
      case "NPCReplaceWedRing": // GameScene.NPCReplaceWedRing
        if (!this._open) break;
        this._rate = this._craftRate = p.Rate;
        this.drop.show(PanelType.ReplaceWedRing, p.Rate);
        break;
      case "NPCDisassemble": if (this._open) this.drop.show(PanelType.Disassemble); break; // GameScene.NPCDisassemble
      case "NPCDowngrade": if (this._open) this.drop.show(PanelType.Downgrade); break; // GameScene.NPCDowngrade
      case "NPCReset": if (this._open) this.drop.show(PanelType.Reset); break; // GameScene.NPCReset
      // GameScene.RefineItem: RefineReset + NPCDialog.Hide (Inventory removed the item and cleared the grid)
      case "RefineItem": this.close(); break;
    }
    return false;
  }
}
