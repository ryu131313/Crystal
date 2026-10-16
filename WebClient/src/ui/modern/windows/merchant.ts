// Ported from play/hudMerchant.ts (MerchantMock): the new UI's NPC merchant window over the real NPC session. Desktop
// counterparts: NPCGoodsDialog / NPCSubGoodsDialog (Buy, BuySub, pearl goods) and NPCDropDialog's Sell / Repair /
// SpecialRepair panels (Client/MirScenes/Dialogs/NPCDialogs.cs); the classic port is src/ui/npcWindow.ts (its buy guards
// are NpcSession.buyPlan, kept here). Changes from the mock:
//  - opens only from the server's answers while the NPC dialog is open (NpcSession.goods: S.NPCGoods Buy / BuySub,
//    S.NPCPearlGoods; NpcDrop.shown: S.NPCSell / S.NPCRepair / S.NPCSRepair) and closes with the NPC session (a new NPC
//    page that opens no merchant panel closes it, like GameScene.NPCResponse hiding the goods / drop panels). No menu
//    entry or hotkey. Every action is gated by the server on the current page key (PlayerObject.BuyItem / SellItem /
//    RepairItem), so a tab is live only while its panel is the one the server opened; another service is reached
//    through the current page's own link (npcSession.call: the server only accepts keys on the current page);
//  - Buy: the server's list (NpcSession goodsDisplay: one row per ItemIndex for a normal shop, the used offers of that
//    item folded under it = NPCSubGoodsDialog; BuySub sorted by price; pearl goods priced in pearls). Clicking a row
//    runs NpcSession.buyPlan (gold cap, bag-space GetMaxGain, pearls, the amount box for a stack) and adds it to a
//    cart; the cart re-checks gold and bag room for every line together and sends one C.BuyItem per stack through the
//    drag controller's SendPacer (paced; a purchase queued from a panel that has since changed is dropped). Used and
//    buy-back items are bought whole (the server clamps the count to the entry's own);
//  - Buy back: the S.NPCGoods list the server sends for [@BUYBACK] (NpcSession.lastPageKey), labelled as such; Crystal
//    keeps the player's last sales per NPC (GoodsBuyBackMaxStored, 20 by default, for GoodsBuyBackTime, 60 minutes);
//  - Sell: the bag (Inventory, belt included in Select junk only through the bag cells shown); picks are keyed by
//    UniqueID, DontSell items are dimmed, 'Select junk' marks outgrown Common gear, and Sell sends ONE C.SellItems
//    (Inventory.sellItems; the cells lock until each S.SellItem). A click on a bag item in the inventory window, or a
//    drag onto the "add" cell, picks it too (NPCDropDialog's bag-click target);
//  - Repair: the damaged items in the bag (the server repairs bag items only), per item or 'Repair all (bag)' as ONE
//    C.RepairItems (Inventory.repairItems, `special` = the panel the server opened: [@SREPAIR]); prices are
//    UserItem.RepairPrice() x NPCRate (x3 special), the normal repair's maximum-durability loss is
//    (MaxDura - CurrentDura) / 30 (PlayerObject.RepairItem);
//  - prices: src/game/items.ts (itemPrice / sellPrice / repairCost / specialRepairCost) with the NPC rate, truncated
//    like the server's uint casts; gold is only read from Inventory, nothing changes until the server answers.
// HIDDEN (Crystal has no system for them): repairing worn gear (Repair equipped / Repair all worn), the mock's local
// wallet and "sold this visit" list, its local sell / repair / buy-back outcomes. Pearls are not tracked client-side
// (UserObject.PearlCount is not ported): the pearl shop's checks are the server's.
// Narrow layout (a layer below 1040 px): the cart / side column stacks under the list.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { BindMode, ItemGrade, ItemType, PanelType, RequiredType, Stat } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import { BELT_IDX } from "../../../game/inventory.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { addedCount, itemPrice, repairPrice, sellPrice } from "../../../game/items.ts";
import { goodsDisplay } from "../../../game/npcSession.ts";
import type { NpcPage } from "../../../game/npcMarkup.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, chips, fmt, fmt1, gradeVar, h, injectCss, later, npcName, plural, searchBox, setStyle, setText, svg,
  type Btn, type Choice,
} from "../core/index.ts";
import type { ItemCell } from "../core/itemCells.ts";
import type { CustomGrid } from "../core/dragRoutes.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const MERCHANT_WINDOW = "merchant";

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  LowGold: "Not enough gold.",
  YouNoBagSpace: "You do not have enough space.",
  CannotSellItem: "Cannot sell this item.",
  CannotCarryMoreGold: "Cannot carry anymore gold.",
  CannotRepairItem: "Cannot repair this item.",
  PurchaseAmount: "Purchase Amount",
};

/** One request per C.BuyItem: the most a cart sends in one go (the pacer spreads them; MaxPacket is 50 per 5 s). */
const MAX_CART_SENDS = 40;
const MAX_GOLD = 0xffffffff; // uint Account.Gold

type Tab = "buy" | "sell" | "repair" | "buyback";
const TABS: readonly Tab[] = ["buy", "sell", "repair", "buyback"];
const TAB_NAME: Readonly<Record<Tab, string>> = { buy: "Buy", sell: "Sell", repair: "Repair", buyback: "Buy back" };
/** NPCScript keys (without brackets) whose page opens each tab's panel. */
const TAB_KEYS: Readonly<Record<Tab, readonly string[]>> = {
  buy: ["@BUY", "@BUYSELL", "@BUYNEW", "@BUYSELLNEW", "@BUYUSED", "@PEARLBUY"],
  sell: ["@SELL", "@BUYSELL", "@BUYSELLNEW"],
  repair: ["@REPAIR", "@SREPAIR"],
  buyback: ["@BUYBACK"],
};
const BUY_BACK_KEY = "@BUYBACK";
/** "[@Buy]" / "@buy" -> "@BUY". */
const normKey = (k: string): string => k.replace(/^\[|\]$/g, "").toUpperCase();

/** The merchant links on a page: normalised key -> { the raw key to call, the link text }. */
function pageLinks(page: NpcPage): Map<string, { key: string; text: string }> {
  const out = new Map<string, { key: string; text: string }>();
  for (const row of page.rows) for (const s of row) if (s.kind === "link") out.set(normKey(s.key), { key: s.key, text: s.text });
  for (const b of page.bigButtons) out.set(normKey(b.action), { key: b.action, text: b.text });
  return out;
}

// — The merchant session (DOM-free; one per UI runtime, built by the watch hook before the window exists) —

/** The goods list on show. */
interface GoodsPanel { readonly type: number; readonly list: readonly UserItem[]; readonly usePearls: boolean; readonly key: string }

/** Which merchant panels the server has open for the current NPC page, and the rate. Panels clear on every new page
 *  (GameScene.NPCResponse) and on NPCDialog.Hide. */
export class MerchantSession {
  /** A panel opened / cleared, or the rate changed. */
  readonly changed = new Emitter<void>();
  private _goods: GoodsPanel | null = null;
  private _sell = false;
  private _repair: number | null = null; // PanelType.Repair / SpecialRepair
  private _gen = 0;
  private _focus: Tab | null = null;
  private _focusSeq = 0;
  private stopClose: () => void = () => {};

  constructor(private readonly host: ModernUiHost) {
    const s = host.ctx.models.npcSession, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(s.page.on(() => this.newPage()));
    on(s.closed.on(() => this.end()));
    on(s.goods.on((g) => {
      if (g.type !== PanelType.Buy && g.type !== PanelType.BuySub) return; // Craft is the craft window's
      const key = normKey(s.lastPageKey);
      this._goods = { type: g.type, list: g.list, usePearls: g.usePearls, key };
      this.opened(key === BUY_BACK_KEY ? "buyback" : "buy");
    }));
    on(s.drop.shown.on(({ type }) => {
      if (type === PanelType.Sell) { this._sell = true; this.opened("sell"); }
      else if (type === PanelType.Repair || type === PanelType.SpecialRepair) { this._repair = type; this.opened("repair"); }
    }));
  }

  /** The goods list (Buy / BuySub / pearls / buy back), or null. */
  get goods(): GoodsPanel | null { return this._goods; }
  /** S.NPCSell is open (Sell / BuySell pages). */
  get sell(): boolean { return this._sell; }
  /** PanelType.Repair or SpecialRepair while a repair page is open, else null. */
  get repair(): number | null { return this._repair; }
  /** Bumps on every new page / close: requests queued from an older panel are dropped. */
  get gen(): number { return this._gen; }
  /** The tab of the first panel the current page opened (the window switches to it once per page). */
  get focus(): Tab | null { return this._focus; }
  get focusSeq(): number { return this._focusSeq; }
  /** The Buy / BuySub / Repair rate (GameScene.NPCRate as NPCGoods / NPCRepair / NPCSRepair / NPCPearlGoods set it). */
  get rate(): number { return this.host.ctx.models.npcSession.goodsRate; }
  /** Any merchant panel open. */
  get active(): boolean { return !!this._goods || this._sell || this._repair !== null; }

  /** Whether `tab`'s panel is the one the server has open. */
  live(tab: Tab): boolean {
    switch (tab) {
      case "buy": return !!this._goods && this._goods.key !== BUY_BACK_KEY;
      case "buyback": return !!this._goods && this._goods.key === BUY_BACK_KEY;
      case "sell": return this._sell;
      case "repair": return this._repair !== null;
    }
  }

  private opened(tab: Tab): void {
    this.stopClose();
    if (!this._focus) { this._focus = tab; this._focusSeq++; }
    this.changed.emit();
    this.host.openWindow(MERCHANT_WINDOW, true);
  }

  /** GameScene.NPCResponse: the goods and drop panels hide; the window closes unless the page opens another panel
   *  (its packets follow the page at once; a short grace keeps the window from blinking between them). */
  private newPage(): void {
    this.clear();
    this.changed.emit();
    this.stopClose();
    if (!this.host.rt.isOpen(MERCHANT_WINDOW)) return;
    this.stopClose = later(() => { if (!this.active) this.host.openWindow(MERCHANT_WINDOW, false); }, 300, this.host.rt.signal);
  }

  /** NPCDialog.Hide: everything closes. */
  private end(): void {
    this.stopClose();
    this.clear();
    this.changed.emit();
    this.host.openWindow(MERCHANT_WINDOW, false);
  }

  private clear(): void {
    this._goods = null;
    this._sell = false;
    this._repair = null;
    this._focus = null;
    this._gen++;
  }
}

/** The runtime's merchant session (built on first use). */
export function merchantSession(host: ModernUiHost): MerchantSession {
  return host.rt.service("merchantSession", () => new MerchantSession(host));
}

// — Pure helpers —

/** The server's price for `count` of a goods entry: (uint)(goods.Price() x rate) with goods.Count = count. */
function buyPrice(item: UserItem, info: ItemInfo | undefined, count: number, rate: number): number {
  return Math.trunc(Math.fround(itemPrice({ ...item, Count: count }, info) * rate));
}
/** (uint)(RepairPrice() x rate), x3 for a special repair. */
function repairFee(item: UserItem, info: ItemInfo | undefined, rate: number, special: boolean): number {
  return Math.trunc(Math.fround(repairPrice(item, info) * (special ? 3 : 1) * rate));
}
/** PlayerObject.RepairItem: a normal repair takes (MaxDura - CurrentDura) / 30 off the maximum. */
const repairLoss = (item: UserItem): number => Math.trunc((item.MaxDura - item.CurrentDura) / 30);
/** Durability in the client's units (ItemLabel: thousandths). */
const dura = (n: number): string => fmt1(n / 1000);
const nameOf = (info: ItemInfo | undefined, item: UserItem): string => info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
const isStack = (info: ItemInfo | undefined): boolean => !!info && info.StackSize > 1;

type Cat = "Potions" | "Scrolls" | "Weapons" | "Armour" | "Jewellery" | "Other";
const CATS: readonly Cat[] = ["Potions", "Scrolls", "Weapons", "Armour", "Jewellery", "Other"];
/** ItemType -> goods category (client grouping; everything else is Other). */
const CAT_OF: Readonly<Partial<Record<number, Cat>>> = {
  [ItemType.Potion]: "Potions", [ItemType.Scroll]: "Scrolls", [ItemType.Amulet]: "Scrolls", [ItemType.Weapon]: "Weapons",
  [ItemType.Armour]: "Armour", [ItemType.Helmet]: "Armour", [ItemType.Boots]: "Armour", [ItemType.Belt]: "Armour",
  [ItemType.Necklace]: "Jewellery", [ItemType.Bracelet]: "Jewellery", [ItemType.Ring]: "Jewellery",
};
const catOf = (info: ItemInfo | undefined): Cat => (info && CAT_OF[info.Type]) || "Other";
const GEAR: readonly number[] = [ItemType.Weapon, ItemType.Armour, ItemType.Helmet, ItemType.Necklace, ItemType.Bracelet,
  ItemType.Ring, ItemType.Belt, ItemType.Boots];

/** A cart line: a goods entry and how many (one C.BuyItem per stack of it). */
interface CartLine { readonly item: UserItem; count: number; shown: UserItem }

const G_GOODS: CustomGrid = "x:mGoods", G_CART: CustomGrid = "x:mCart", G_SELL: CustomGrid = "x:mSell";

const CSS = `
.hudm .mwin { width: 860px; }
.hudm .mwin .panel { display: flex; flex-direction: column; }
.hudm .mwin .head { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mwin .npc { width: 40px; height: 40px; border-radius: 50%; flex: none; display: flex; align-items: center; justify-content: center; color: var(--ac);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); }
.hudm .mwin .npc svg.ic { width: 20px; height: 20px; }
.hudm .mwin .say { min-width: 0; } .hudm .mwin .say b { display: block; font-size: 15px; color: #fff; }
.hudm .mwin .say span { font-size: 11px; color: var(--dim); }
.hudm .mwin .wallet { margin-left: auto; display: flex; align-items: center; gap: 6px; padding: 5px 12px; border-radius: 14px; font-size: 13px; font-weight: 700; color: var(--gold);
  background: color-mix(in srgb, var(--gold) 8%, transparent); border: 1px solid color-mix(in srgb, var(--gold) 40%, transparent); font-variant-numeric: tabular-nums; }
.hudm .mwin .wallet svg.ic { width: 14px; height: 14px; }
.hudm .mwin .mtabs { display: flex; gap: 2px; padding: 8px 14px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mwin .mtabs button { padding: 6px 14px; cursor: pointer; font: inherit; font-size: 12px; font-weight: 600; color: var(--dim); background: none;
  border-radius: var(--rad) var(--rad) 0 0; border: 1px solid transparent; border-bottom: 0; }
.hudm .mwin .mtabs button:hover { color: var(--tx); }
.hudm .mwin .mtabs button.on { color: #fff; background: color-mix(in srgb, var(--ac) 16%, transparent); border-color: color-mix(in srgb, var(--ac) 50%, transparent); box-shadow: inset 0 2px 0 var(--ac); }
.hudm .mwin .mtabs button.off:not(.on) { font-style: italic; }
.hudm .mwin .mtabs i { font-style: normal; margin-left: 5px; font-size: 10px; padding: 0 5px; border-radius: 7px; background: var(--ac); color: #fff; }
.hudm .mwin .mbody { display: flex; height: 420px; min-height: 0; }
.hudm .mwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; min-height: 0; }
.hudm .mwin .side { width: 260px; flex: none; display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mwin .tools { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
.hudm .mwin .tools .ui-search { width: 180px; }
.hudm .mwin .sub { display: flex; justify-content: space-between; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .mwin .list { flex: 1; min-height: 0; overflow-y: auto; display: grid; grid-template-columns: 1fr 1fr; gap: 5px; align-content: start; padding-right: 3px; scrollbar-width: thin; }
.hudm .mwin .gd { display: flex; align-items: center; gap: 8px; padding: 4px 8px 4px 4px; border-radius: var(--rad); cursor: pointer; pointer-events: auto;
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .mwin .gd:hover { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 9%, transparent); }
.hudm .mwin .gd.var { margin-left: 18px; border-style: dashed; }
.hudm .mwin .gd.open { border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .mwin .gd .t { flex: 1; min-width: 0; }
.hudm .mwin .gd .t b { display: block; font-size: 12px; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mwin .gd .t span { font-size: 10px; color: var(--dim); } .hudm .mwin .gd .t span.bad { color: var(--bad-soft); }
.hudm .mwin .gd .pr { text-align: right; font-size: 12px; font-weight: 700; color: var(--gold); white-space: nowrap; font-variant-numeric: tabular-nums; }
.hudm .mwin .gd .pr small { display: block; font-size: 9px; font-weight: 600; color: var(--dim); }
.hudm .mwin .gd.cant .pr { color: var(--bad-soft); }
.hudm .mwin .cart { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; scrollbar-width: thin; }
.hudm .mwin .ci { display: flex; align-items: center; gap: 6px; padding: 3px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .mwin .ci .t { flex: 1; min-width: 0; font-size: 11px; } .hudm .mwin .ci .t b { display: block; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mwin .ci .t span { color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .mwin .qty { display: flex; align-items: center; border: 1px solid var(--fr); border-radius: 11px; overflow: hidden; }
.hudm .mwin .qty button { width: 20px; height: 20px; padding: 0; border: 0; background: none; cursor: pointer; font: inherit; font-weight: 700; color: var(--tx); }
.hudm .mwin .qty button:hover { background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .mwin .qty span { min-width: 28px; text-align: center; font-size: 11px; font-weight: 700; color: #fff; font-variant-numeric: tabular-nums; cursor: pointer; }
.hudm .mwin .x { padding: 0 4px; border: 0; background: none; cursor: pointer; font: inherit; color: var(--dim); } .hudm .mwin .x:hover { color: var(--bad-soft); }
.hudm .mwin .tot { display: flex; justify-content: space-between; align-items: baseline; padding-top: 6px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 12px; color: var(--dim); }
.hudm .mwin .tot b { font-size: 15px; color: var(--gold); font-variant-numeric: tabular-nums; } .hudm .mwin .tot b.bad { color: var(--bad-soft); }
.hudm .mwin .side .btn.big { width: 100%; }
.hudm .mwin .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .mwin .hint { font-size: 11px; color: var(--dim); line-height: 1.5; }
.hudm .mwin .bag { flex: 1; min-height: 0; display: grid; grid-template-columns: repeat(10, 40px); gap: 4px; align-content: start; overflow-y: auto; scrollbar-width: thin; }
.hudm .mwin .bag .icell.no { opacity: .3; filter: grayscale(1); cursor: not-allowed; }
.hudm .mwin .bag .icell.pk { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .mwin .bag .icell.junk::before { content: "junk"; position: absolute; left: 0; right: 0; bottom: 0; z-index: 2; font-size: 8px; font-weight: 700; text-align: center; color: #fff; background: rgba(120, 120, 130, .7); }
.hudm .mwin .addc { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .mwin .rlist { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; padding-right: 3px; scrollbar-width: thin; }
.hudm .mwin .rp { display: flex; align-items: center; gap: 10px; padding: 5px 8px 5px 5px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .mwin .rp.no { opacity: .55; }
.hudm .mwin .rp .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .mwin .rp .t b { color: var(--tx); } .hudm .mwin .rp .t small { color: var(--dim); }
.hudm .mwin .rp .t small.loss { color: var(--bad-soft); } .hudm .mwin .rp .t small.kept { color: var(--ok); }
.hudm .mwin .dura { position: relative; height: 6px; margin-top: 4px; border-radius: 3px; background: #04060a; overflow: hidden; }
.hudm .mwin .dura i { position: absolute; left: 0; top: 0; bottom: 0; background: var(--ok); }
.hudm .mwin .dura i.mid { background: var(--warn); } .hudm .mwin .dura i.low { background: var(--bad); }
.hudm .mwin .dura u { position: absolute; top: 0; bottom: 0; background: color-mix(in srgb, var(--ok) 35%, transparent); }
.hudm .mwin .dura em { position: absolute; top: 0; bottom: 0; background: color-mix(in srgb, var(--bad) 55%, transparent); }
.hudm .mwin .rp .cost { width: 84px; text-align: right; font-size: 12px; font-weight: 700; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .mwin .rp .why { width: 130px; text-align: right; font-size: 11px; color: var(--dim); }
.hudm .mwin .rsum { font-size: 11px; color: var(--dim); line-height: 1.5; } .hudm .mwin .rsum b { color: var(--tx); }
.hudm .mwin .off { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; text-align: center; color: var(--dim); font-size: 12px; }
.hudm .mwin .off .row { display: flex; gap: 6px; flex-wrap: wrap; justify-content: center; }
.hudm .mwin .empty { padding: 24px 8px; text-align: center; color: var(--dim); font-size: 12px; grid-column: 1 / -1; }
.hudm .mwin .sp { flex: 1; }
/* Narrow layer: one list column, the side column under the list. */
.hudm.narrow .mwin { width: 600px; }
.hudm.narrow .mwin .mbody { flex-direction: column; height: 520px; }
.hudm.narrow .mwin .side { width: auto; height: 190px; flex: none; border-left: 0; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .mwin .list { grid-template-columns: 1fr; }
.hudm.narrow .mwin .say span { display: none; }
`;

class MerchantWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: Tab = "buy";
  private seenFocus = -1;
  private note = "";
  // Buy
  private query = "";
  private cat: Cat | "All" = "All";
  private readonly cart = new Map<string, CartLine>();
  private cartGen = -1;
  private readonly open1 = new Set<number>(); // ItemIndexes whose used offers are unfolded
  private goodsKey = "";
  private chipsKey = "";
  private rowItems: UserItem[] = [];
  private rowCells: ItemCell[] = [];
  private cartCells: ItemCell[] = [];
  private pickCells: ItemCell[] = [];
  private offKey = "";
  private rowEls: { el: HTMLElement; item: UserItem; info: ItemInfo | undefined; price: number; req: HTMLElement }[] = [];
  private cartItems: UserItem[] = [];
  private cartKey = "";
  // Sell
  private readonly picks = new Set<bigint>();
  private sellCells: ItemCell[] = [];
  private pickKey = "";
  // Repair
  private repairKey = "";

  // — DOM, built once —
  private readonly npcLabel = h("b");
  private readonly npcSub = h("span");
  private readonly goldText = h("span");
  private readonly tabBtns = new Map<Tab, { el: HTMLButtonElement; badge: HTMLElement }>();
  private readonly buyMain = h("div", "main");
  private readonly buySide = h("div", "side");
  private readonly sellMain = h("div", "main");
  private readonly sellSide = h("div", "side");
  private readonly repMain = h("div", "main");
  private readonly repSide = h("div", "side");
  private readonly offView = h("div", "off");
  private readonly body = h("div", "mbody");
  private readonly noteEls: HTMLElement[] = [];
  // buy parts
  private readonly listTitle = h("span");
  private readonly listHint = h("span");
  private readonly chipsBox = h("div");
  private readonly list = h("div", "list");
  private readonly cartList = h("div", "cart");
  private readonly cartWeight = h("span");
  private readonly cartTotal = h("b");
  private readonly buyBtn: Btn;
  private readonly clearCartBtn: Btn;
  private readonly searchEl: ReturnType<typeof searchBox>;
  // sell parts
  private readonly bagGrid = h("div", "bag");
  private readonly pickList = h("div", "cart");
  private readonly pickCount = h("span");
  private readonly sellTotal = h("b");
  private readonly sellBtn: Btn;
  // repair parts
  private readonly repTitle = h("span");
  private readonly repList = h("div", "rlist");
  private readonly repExplain = h("div", "hint");
  private readonly repSwitch = h("div", "tools");
  private readonly repSum = h("div", "rsum");
  private readonly repTotal = h("b");
  private readonly repAllBtn: Btn;
  private repRows: { el: HTMLElement; cell: ItemCell }[] = [];

  constructor(private readonly ui: ModernUiHost, private readonly session: MerchantSession) {
    super(ui.rt, "mwin", 60, 40);
    injectCss("win-merchant", CSS);
    this.m = ui.ctx.models;
    const sig = this.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });

    // Head: the NPC, the rate, the gold (read from Inventory)
    const head = h("div", "head", h("div", "npc", svg("shop")), h("div", "say", this.npcLabel, this.npcSub),
      h("div", "wallet", svg("coin"), this.goldText));
    const tabBar = h("div", "mtabs");
    for (const t of TABS) {
      const badge = h("i");
      badge.hidden = true;
      const el = h("button", "", TAB_NAME[t], badge);
      el.type = "button";
      el.addEventListener("click", () => this.selectTab(t), { signal: sig });
      this.tabBtns.set(t, { el, badge });
      tabBar.append(el);
    }

    // Buy: search, category chips, the goods; the cart beside it
    this.searchEl = searchBox("Search goods…", (q) => { this.query = q.toLowerCase(); this.goodsKey = ""; this.invalidate(); }, { signal: sig });
    this.buyBtn = button("Buy", () => this.checkout(), { cls: "main big", icon: "coin" });
    this.clearCartBtn = button("Clear", () => { this.cart.clear(); this.invalidate(); }, { title: "Empty the cart" });
    this.list.addEventListener("click", (e) => this.goodsClick(e), { signal: sig });
    this.buyMain.append(h("div", "tools", this.searchEl.el, this.chipsBox), h("div", "sub", this.listTitle, this.listHint), this.list);
    this.buySide.append(h("div", "lbl sub", h("span", "", "Cart"), this.cartWeight), this.cartList,
      h("div", "tot", "Total", this.cartTotal), this.buyBtn.el, this.clearCartBtn.el, this.noteEl());

    // Sell: the bag (click to pick), Select junk / Clear; the picks beside it
    const junk = button("Select junk", () => this.selectJunk(), { title: "Pick outgrown Common gear (15+ levels under yours, no added stats) and candles" });
    const clear = button("Clear", () => { this.picks.clear(); this.invalidate(); });
    this.bagGrid.addEventListener("click", (e) => { const c = ui.cells.cellOf(e.target); if (c) this.togglePick(c.ref.index); }, { signal: sig });
    const addCell = ui.cells.cell(G_SELL, 0, { size: 34, empty: svg("plus", ""), signal: sig });
    this.sellBtn = button("Sell", () => this.sellPicked(), { cls: "main big", icon: "coin" });
    this.sellMain.append(h("div", "tools", h("span", "lbl", "Your bag · the merchant pays half the price"), h("span", "sp"), junk.el, clear.el), this.bagGrid,
      h("div", "hint", "Click items to pick them for sale. Picks are sold together in one go."));
    this.sellSide.append(h("div", "lbl sub", h("span", "", "Selling"), this.pickCount), this.pickList,
      h("div", "addc", addCell.el, "Drop or click items in your bag to add them"),
      h("div", "tot", "You get", this.sellTotal), this.sellBtn.el, this.noteEl());

    // Repair: the damaged bag items; the totals and Repair all beside them
    this.repAllBtn = button("Repair all (bag)", () => this.repairAll(), { cls: "main big", icon: "wrench" });
    this.repList.addEventListener("click", (e) => {
      const b = (e.target as HTMLElement).closest<HTMLElement>("[data-uid]");
      if (b && !b.classList.contains("dis")) this.repairOne(BigInt(b.dataset.uid!));
    }, { signal: sig });
    this.repMain.append(h("div", "sub", this.repTitle, h("span", "", "Worn gear is repaired only from your bag")), this.repList);
    this.repSide.append(this.repExplain, this.repSwitch, h("div", "sp"), this.repSum, h("div", "tot", "All damaged", this.repTotal), this.repAllBtn.el, this.noteEl());

    this.el.append(h("div", "panel", this.titleBar("MERCHANT", "Close"), head, tabBar, this.body));

    // Custom grids: the goods and cart icons (display only, never targets) and the sell "add" cell (a drop target)
    on(ui.drag.addTarget(G_GOODS, { item: (i) => this.rowItems[i] ?? null, accepts: () => null, drop: () => {} }));
    on(ui.drag.addTarget(G_CART, { item: (i) => this.cartItems[i] ?? null, accepts: () => null, drop: () => {} }));
    on(ui.drag.addTarget(G_SELL, {
      item: () => null,
      accepts: (from, item) => {
        if (from.grid !== "inv" || !this.session.sell) return null;
        return this.sellRefusal(item) || "";
      },
      verb: () => "Sell",
      drop: (from) => { this.togglePick(from.index, true); },
    }));
    // A click on a bag item in the inventory window picks it while the Sell panel shows (NPCDropDialog's bag click).
    on(ui.drag.addClickHook((ref, item) => {
      if (!this.open || this.tab !== "sell" || !this.session.sell || ref.grid !== "inv") return false;
      if (this.sellRefusal(item)) return false;
      this.togglePick(ref.index);
      return true;
    }));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    on(this.m.inventory.subscribe(() => this.invalidate()));
    on(this.session.changed.on(() => this.invalidate()));
    on(this.m.npcSession.page.on(() => this.invalidate())); // the current page's links (tabs)
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.goodsKey = this.chipsKey = this.cartKey = this.pickKey = this.repairKey = ""; this.invalidate(); } }));
  }

  private noteEl(): HTMLElement {
    const el = h("div", "note");
    this.noteEls.push(el);
    return el;
  }

  private say(text: string): void {
    this.note = text;
    for (const el of this.noteEls) setText(el, text);
  }

  protected override shown(): void { this.say(""); }

  protected override closed(): void {
    this.cart.clear();
    this.picks.clear();
    this.open1.clear();
    this.note = "";
  }

  // — Tabs —

  /** A tab shows when its panel is open or the current page links to it. */
  private links(tab: Tab): { key: string; text: string }[] {
    const all = pageLinks(this.m.npcSession.currentPage);
    const out: { key: string; text: string }[] = [];
    for (const k of TAB_KEYS[tab]) { const l = all.get(k); if (l) out.push(l); }
    return out;
  }

  private selectTab(t: Tab): void {
    if (this.tab === t) return;
    this.tab = t;
    this.say("");
    this.invalidate();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const s = this.session, npc = this.m.npcSession;
    if (s.focusSeq !== this.seenFocus) { this.seenFocus = s.focusSeq; if (s.focus) this.tab = s.focus; }
    if (s.gen !== this.cartGen) { this.cartGen = s.gen; this.cart.clear(); this.open1.clear(); this.picks.clear(); this.goodsKey = ""; }

    const [name, place] = npcName(npc.npcName || "Merchant");
    setText(this.npcLabel, name);
    const rate = s.rate;
    setText(this.npcSub, [place, rate !== 1 ? `prices x${fmt1(rate)}` : ""].filter(Boolean).join(" · "));
    setText(this.goldText, fmt(this.m.inventory.gold));

    const linkCache = new Map<Tab, { key: string; text: string }[]>();
    for (const t of TABS) {
      const live = s.live(t), links = live ? [] : this.links(t);
      linkCache.set(t, links);
      const b = this.tabBtns.get(t)!;
      b.el.hidden = !live && links.length === 0;
      b.el.classList.toggle("on", t === this.tab);
      b.el.classList.toggle("off", !live);
      b.el.title = live ? "" : "Opens the merchant's page for this";
      const n = t === "buy" || t === "buyback" ? (live ? this.cart.size : 0) : t === "sell" ? this.picks.size : 0;
      b.badge.hidden = !n;
      if (n) setText(b.badge, String(n));
    }
    if (this.tabBtns.get(this.tab)!.el.hidden) {
      const first = TABS.find((t) => s.live(t));
      if (first) this.tab = first;
    }

    let parts: HTMLElement[];
    if (!s.live(this.tab)) { this.renderOff(linkCache.get(this.tab) ?? []); parts = [this.offView]; }
    else if (this.tab === "buy" || this.tab === "buyback") { this.renderBuy(); parts = [this.buyMain, this.buySide]; }
    else if (this.tab === "sell") { this.renderSell(); parts = [this.sellMain, this.sellSide]; }
    else { this.renderRepair(); parts = [this.repMain, this.repSide]; }
    if (this.body.childElementCount !== parts.length || parts.some((p, i) => this.body.children[i] !== p)) this.body.replaceChildren(...parts);
    for (const el of this.noteEls) setText(el, this.note);
  }

  /** A tab whose panel the server has not opened: the current page's links to it, else where to find it. */
  private renderOff(links: { key: string; text: string }[]): void {
    const name = npcName(this.m.npcSession.npcName || "the merchant")[0];
    const key = `${this.tab}|${name}|${links.map((l) => `${l.key}:${l.text}`).join(",")}`;
    if (key === this.offKey) return;
    this.offKey = key;
    const btns = links.map((l) => button(l.text || TAB_NAME[this.tab], () => this.m.npcSession.call(l.key), { cls: "main" }).el);
    this.offView.replaceChildren(svg("talk"),
      h("div", "", links.length ? `${TAB_NAME[this.tab]} is on ${name}'s page:` : `Speak to ${name} and choose ${TAB_NAME[this.tab]} on the page.`),
      h("div", "row", ...btns));
  }

  // — Buy —

  private info(item: UserItem): ItemInfo | undefined { return this.m.itemInfos.get(item.ItemIndex); }

  private renderBuy(): void {
    const g = this.session.goods!, inv = this.m.inventory, rate = this.session.rate;
    const back = g.key === BUY_BACK_KEY;
    setText(this.listTitle, back ? "Buy back: your recent sales here" : g.usePearls ? "Pearl shop" : g.type === PanelType.BuySub ? "Used goods" : "Goods");
    setText(this.listHint, back ? "Kept up to 20 per merchant for about an hour (server settings)" : "Click to add to the cart");
    const { goods, display } = goodsDisplay(g.type, g.usePearls, g.list, (i) => this.info(i));

    // Chips (rebuilt only when the counts or the choice changed)
    const counts = new Map<Cat | "All", number>([["All", display.length]]);
    for (const it of display) { const c = catOf(this.info(it)); counts.set(c, (counts.get(c) ?? 0) + 1); }
    const cats = CATS.filter((c) => counts.get(c));
    if (this.cat !== "All" && !cats.includes(this.cat)) this.cat = "All";
    const ck = [this.cat, ...cats.map((c) => `${c}${counts.get(c)}`)].join(",");
    if (ck !== this.chipsKey) {
      this.chipsKey = ck;
      const choices: Choice<Cat | "All">[] = (["All", ...cats] as (Cat | "All")[]).map((c) => ({ value: c, label: `${c} ${counts.get(c) ?? 0}` }));
      this.chipsBox.hidden = cats.length < 2;
      this.chipsBox.replaceChildren(chips(choices, [this.cat], (sel) => {
        this.cat = sel.values().next().value ?? "All";
        this.goodsKey = this.chipsKey = "";
        this.invalidate();
      }, { multi: false, allowNone: false }).el);
    }

    // Rows (rebuilt only when the list, the filter or the unfolded offers changed)
    const key = `${this.session.gen}|${g.list.length}|${this.query}|${this.cat}|${[...this.open1].join(",")}|${rate}`;
    if (key !== this.goodsKey) {
      this.goodsKey = key;
      for (const c of this.rowCells) c.dispose();
      this.rowCells = [];
      this.rowItems = [];
      this.rowEls = [];
      const els: HTMLElement[] = [];
      for (const it of display) {
        const info = this.info(it);
        if (this.cat !== "All" && catOf(info) !== this.cat) continue;
        if (this.query && !nameOf(info, it).toLowerCase().includes(this.query)) continue;
        // NPCGoodsDialog.CheckSubGoods: a normal shop folds the other offers of an item kind (used goods) under it
        const offers = g.type === PanelType.Buy && !g.usePearls ? goods.filter((x) => x.ItemIndex === it.ItemIndex) : [it];
        if (offers.length > 1) {
          const unfolded = this.open1.has(it.ItemIndex);
          els.push(this.goodsRow(it, info, `${offers.length} offers · click to ${unfolded ? "fold" : "compare"}`, true, unfolded));
          if (unfolded) for (const o of offers) els.push(this.goodsRow(o, info, "", false, false, true));
        } else els.push(this.goodsRow(it, info, "", false, false));
      }
      if (!els.length) els.push(h("div", "empty", display.length ? "Nothing matches." : back ? "You haven't sold anything here lately." : "This merchant has nothing for sale."));
      this.list.replaceChildren(...els);
    }
    // Affordability and level (class toggles only)
    const level = this.ui.ctx.player.level;
    for (const r of this.rowEls) {
      r.el.classList.toggle("cant", !g.usePearls && r.price > inv.gold);
      if (r.info) r.req.classList.toggle("bad", r.info.RequiredType === RequiredType.Level && level < r.info.RequiredAmount);
    }
    this.renderCart(g.usePearls);
  }

  /** One goods row: icon (custom grid cell: hover shows the item tooltip), name, requirement / stack, price. */
  private goodsRow(item: UserItem, info: ItemInfo | undefined, extra: string, group: boolean, unfolded: boolean, variant = false): HTMLElement {
    const g = this.session.goods!, rate = this.session.rate;
    const idx = this.rowItems.push(item) - 1;
    const cell = this.ui.cells.cell(G_GOODS, idx, { size: 36, readOnly: true, signal: this.signal });
    this.rowCells.push(cell);
    const shop = item.IsShopItem && !variant;
    const each = isStack(info) && item.IsShopItem;
    const price = each ? buyPrice(item, info, 1, rate) : buyPrice(item, info, item.Count, rate);
    const bits: string[] = [];
    if (info?.RequiredType === RequiredType.Level && info.RequiredAmount > 0) bits.push(`Level ${info.RequiredAmount}`);
    const used = g.key === BUY_BACK_KEY ? "you sold this" : "used";
    if (!item.IsShopItem) bits.push(item.MaxDura > 0 && info && info.Durability > 0 ? `${used} · ${dura(item.CurrentDura)}/${dura(item.MaxDura)}` : used);
    if (each && info) bits.push(`stacks to ${info.StackSize}`);
    if (!each && item.Count > 1) bits.push(`x${fmt(item.Count)}`);
    if (extra) bits.push(extra);
    const name = h("b", "", nameOf(info, item));
    if (info?.Grade) setStyle(name, "color", gradeVar(info.Grade));
    const req = h("span", "", bits.join(" · ") || (shop ? "Any level" : ""));
    const el = h("div", "gd" + (variant ? " var" : "") + (unfolded ? " open" : ""), cell.el, h("div", "t", name, req),
      h("div", "pr", g.usePearls ? `${fmt(price)} ${price === 1 ? "pearl" : "pearls"}` : fmt(price), h("small", "", each ? "each" : "")));
    el.dataset.row = String(idx);
    if (group) el.dataset.group = String(item.ItemIndex);
    this.rowEls.push({ el, item, info, price, req });
    return el;
  }

  private goodsClick(e: MouseEvent): void {
    const row = (e.target as HTMLElement).closest<HTMLElement>(".gd");
    if (!row || !this.session.goods) return;
    if (row.dataset.group) {
      const idx = Number(row.dataset.group);
      if (this.open1.has(idx)) this.open1.delete(idx); else this.open1.add(idx);
      this.invalidate();
      return;
    }
    const item = this.rowItems[Number(row.dataset.row)];
    if (item) void this.addToCart(item);
  }

  /** The cart's sends, total, weight and bag check (GetMaxGain for every line together). */
  private cartPlan(skip?: string): { total: number; weight: number; sends: number; roomFor: (item: UserItem) => number } {
    const inv = this.m.inventory, rate = this.session.rate;
    let slots = 0;
    const partial = new Map<number, number>();
    for (const it of inv.inventory) {
      if (!it) { slots++; continue; }
      const info = this.info(it);
      if (isStack(info) && it.Count < info!.StackSize) partial.set(it.ItemIndex, (partial.get(it.ItemIndex) ?? 0) + info!.StackSize - it.Count);
    }
    let total = 0, weight = 0, sends = 0;
    for (const [k, l] of this.cart) {
      if (k === skip) continue;
      const info = this.info(l.item);
      total += this.linePrice(l, info, rate);
      weight += (info?.Weight ?? 0) * (isStack(info) ? 1 : l.count);
      sends += this.lineSends(l, info);
      if (isStack(info)) {
        const S = info!.StackSize, have = partial.get(l.item.ItemIndex) ?? 0;
        const need = Math.max(0, l.count - have), stacks = Math.ceil(need / S);
        slots -= stacks;
        partial.set(l.item.ItemIndex, have - Math.min(have, l.count) + (stacks * S - need));
      } else slots -= l.count;
    }
    const roomFor = (item: UserItem): number => {
      const info = this.info(item);
      if (!isStack(info)) return Math.max(0, slots);
      return Math.max(0, (partial.get(item.ItemIndex) ?? 0) + Math.max(0, slots) * info!.StackSize);
    };
    return { total, weight, sends, roomFor };
  }

  /** Shop stacks are priced per C.BuyItem (StackSize at most each); everything else is count x the single price. */
  private linePrice(l: CartLine, info: ItemInfo | undefined, rate: number): number {
    if (!l.item.IsShopItem) return buyPrice(l.item, info, l.item.Count, rate);
    if (!isStack(info)) return buyPrice(l.item, info, 1, rate) * l.count;
    const S = info!.StackSize;
    let total = 0;
    for (let left = l.count; left > 0; left -= S) total += buyPrice(l.item, info, Math.min(S, left), rate);
    return total;
  }

  private lineSends(l: CartLine, info: ItemInfo | undefined): number {
    if (!l.item.IsShopItem) return 1;
    return isStack(info) ? Math.ceil(l.count / info!.StackSize) : l.count;
  }

  /** A goods click: NPCGoodsDialog.BuyItem's checks (NpcSession.buyPlan), then the cart (amount box for a stack). */
  private async addToCart(item: UserItem): Promise<void> {
    const g = this.session.goods, gen = this.session.gen;
    if (!g) return;
    const info = this.info(item);
    if (!info) return;
    const k = String(item.UniqueID);
    const line = this.cart.get(k);
    const plan = this.m.npcSession.buyPlan(item, g.usePearls);
    if (plan.kind === "none") return;
    if (plan.kind === "fail") { this.say(plan.text); return; }
    if (!item.IsShopItem) { // used / buy-back entries are bought whole
      if (line) { this.say("Already in the cart."); return; }
      if (this.cartPlan().roomFor(item) < (isStack(info) ? item.Count : 1)) { this.say(T.YouNoBagSpace); return; }
      this.cart.set(k, { item, count: item.Count, shown: item });
      this.say("");
      this.invalidate();
      return;
    }
    const max = this.maxFor(item, info, g.usePearls);
    if (max <= 0) { this.say(this.cartPlan(k).roomFor(item) <= 0 ? T.YouNoBagSpace : T.LowGold); return; }
    if (!isStack(info)) { this.setCount(item, Math.min(max, (line?.count ?? 0) + 1)); return; }
    const n = await this.ui.rt.popups.amount({
      title: T.PurchaseAmount, item: { name: nameOf(info, item), img: info.Image }, max,
      value: Math.min(max, line ? line.count : info.StackSize),
      leftLabel: "more you could buy",
    });
    if (n === null || this.session.gen !== gen || !this.open) return;
    this.setCount(item, n);
  }

  /** The most of `item` the cart can hold: bag room (the other lines counted) and gold left (pearls: the server's). */
  private maxFor(item: UserItem, info: ItemInfo, usePearls: boolean): number {
    const k = String(item.UniqueID);
    const p = this.cartPlan(k);
    let max = Math.min(p.roomFor(item), isStack(info) ? info.StackSize * 10 : MAX_CART_SENDS);
    if (!usePearls) {
      const left = this.m.inventory.gold - p.total;
      const unit = Math.max(1, buyPrice(item, info, isStack(info) ? info.StackSize : 1, this.session.rate) / (isStack(info) ? info.StackSize : 1));
      max = Math.min(max, Math.floor(left / unit));
      while (max > 0 && this.linePrice({ item, count: max, shown: item }, info, this.session.rate) > left) max--;
    }
    return Math.max(0, max);
  }

  private setCount(item: UserItem, n: number): void {
    const k = String(item.UniqueID);
    if (n <= 0) this.cart.delete(k);
    else {
      const l = this.cart.get(k);
      if (l) { l.count = n; l.shown = { ...item, Count: n }; } else this.cart.set(k, { item, count: n, shown: { ...item, Count: n } });
    }
    this.say("");
    this.invalidate();
  }

  private renderCart(usePearls: boolean): void {
    const p = this.cartPlan(), inv = this.m.inventory;
    const key = [...this.cart.values()].map((l) => `${l.item.UniqueID}x${l.count}`).join(",") + `|${this.session.rate}`;
    if (key !== this.cartKey) {
      this.cartKey = key;
      for (const c of this.cartCells) c.dispose();
      this.cartCells = [];
      this.cartItems = [];
      const rows: HTMLElement[] = [];
      for (const l of this.cart.values()) {
        const info = this.info(l.item), idx = this.cartItems.push(l.shown) - 1;
        const cell = this.ui.cells.cell(G_CART, idx, { size: 30, readOnly: true, signal: this.signal });
        this.cartCells.push(cell);
        const price = this.linePrice(l, info, this.session.rate);
        let qty: HTMLElement;
        if (!l.item.IsShopItem) qty = h("span", "hint", "whole");
        else {
          const step = isStack(info) ? info!.StackSize : 1;
          const minus = h("button", "", "−"), plus = h("button", "", "+"), num = h("span", "", fmt(l.count));
          minus.type = plus.type = "button";
          minus.title = plus.title = isStack(info) ? "One stack less / more" : "One less / more";
          minus.addEventListener("click", () => this.setCount(l.item, l.count - step));
          plus.addEventListener("click", () => {
            if (!info) return;
            const max = this.maxFor(l.item, info, usePearls);
            if (max <= l.count) this.say("That's as many as you can carry or afford.");
            else this.setCount(l.item, Math.min(max, l.count + step));
          });
          num.title = isStack(info) ? "Click to type an amount" : "";
          if (isStack(info)) num.addEventListener("click", () => void this.addToCart(l.item));
          qty = h("div", "qty", minus, num, plus);
        }
        const x = h("button", "x", "×");
        x.type = "button";
        x.title = "Remove from the cart";
        x.addEventListener("click", () => this.setCount(l.item, 0));
        rows.push(h("div", "ci", cell.el, h("div", "t", h("b", "", nameOf(info, l.item)), h("span", "", usePearls ? `${fmt(price)} pearls` : fmt(price))), qty, x));
      }
      if (!rows.length) rows.push(h("div", "empty", "Click goods to add them here."));
      // plain row listeners (no window signal, whose abort step would keep every old row alive): they go with
      // their nodes when replaceChildren drops them; the row cells were disposed above
      this.cartList.replaceChildren(...rows);
    }
    // Weight is the server's check (CanGainItem); shown against what the bag has left (UserStats, as the bag window)
    const st = this.ui.ctx.player.stats;
    const left = st ? (st.stats[Stat.BagWeight] ?? 0) - st.currentBagWeight : Infinity;
    setText(this.cartWeight, this.cart.size ? `${fmt(p.weight)} weight · ${plural(p.sends, "purchase")}` : "");
    this.cartWeight.classList.toggle("bad", p.weight > left);
    this.cartWeight.title = Number.isFinite(left) ? `Your bag can take ${fmt(Math.max(0, left))} more weight` : "";
    const short = !usePearls && p.total > inv.gold;
    setText(this.cartTotal, usePearls ? `${fmt(p.total)} pearls` : fmt(p.total));
    this.cartTotal.classList.toggle("bad", short);
    this.buyBtn.setLabel(!this.cart.size ? "Buy" : short ? "Not enough gold" : usePearls ? `Buy for ${fmt(p.total)} pearls` : `Buy for ${fmt(p.total)}`);
    this.buyBtn.setDisabled(!this.cart.size || short || p.sends > MAX_CART_SENDS,
      p.sends > MAX_CART_SENDS ? `At most ${MAX_CART_SENDS} purchases at a time` : short ? T.LowGold : "");
    this.clearCartBtn.el.hidden = !this.cart.size;
  }

  /** Buy: re-checks the whole cart, then one C.BuyItem per stack through the pacer (dropped if the panel changed). */
  private checkout(): void {
    const g = this.session.goods;
    if (!g || !this.cart.size) return;
    const inv = this.m.inventory, p = this.cartPlan();
    if (!g.usePearls && p.total > inv.gold) { this.say(T.LowGold); return; }
    if (p.sends > MAX_CART_SENDS) { this.say(`At most ${MAX_CART_SENDS} purchases at a time.`); return; }
    // Bag room for everything (GetMaxGain over the whole cart): the plan's free slots must not go negative.
    let slots = 0;
    for (const it of inv.inventory) if (!it) slots++;
    if (this.slotsNeeded() > slots) { this.say(T.YouNoBagSpace); return; }
    const gen = this.session.gen, npc = this.m.npcSession;
    let sent = 0;
    for (const l of this.cart.values()) {
      const info = this.info(l.item), item = l.item;
      const counts: number[] = [];
      if (!item.IsShopItem) counts.push(item.Count);
      else if (isStack(info)) for (let left = l.count; left > 0; left -= info!.StackSize) counts.push(Math.min(info!.StackSize, left));
      else for (let i = 0; i < l.count; i++) counts.push(1);
      for (const n of counts) {
        this.ui.drag.request(() => { if (this.session.gen === gen && npc.open) npc.buy(item, n); });
        sent++;
      }
    }
    const kinds = this.cart.size;
    this.cart.clear();
    this.say(`Buying ${plural(kinds, "kind")} of goods (${plural(sent, "purchase")})…`);
    this.invalidate();
  }

  /** Free bag slots the cart needs (after topping up matching stacks). */
  private slotsNeeded(): number {
    const inv = this.m.inventory;
    const partial = new Map<number, number>();
    for (const it of inv.inventory) {
      if (!it) continue;
      const info = this.info(it);
      if (isStack(info) && it.Count < info!.StackSize) partial.set(it.ItemIndex, (partial.get(it.ItemIndex) ?? 0) + info!.StackSize - it.Count);
    }
    let need = 0;
    for (const l of this.cart.values()) {
      const info = this.info(l.item);
      if (!isStack(info)) { need += l.item.IsShopItem ? l.count : 1; continue; }
      const S = info!.StackSize, have = partial.get(l.item.ItemIndex) ?? 0;
      const rest = Math.max(0, l.count - have), stacks = Math.ceil(rest / S);
      need += stacks;
      partial.set(l.item.ItemIndex, have - Math.min(have, l.count) + (stacks * S - rest));
    }
    return need;
  }

  // — Sell —

  /** Why the merchant won't take it ("" = fine): DontSell, waiting for the server. */
  private sellRefusal(item: UserItem): string {
    const info = this.info(item);
    if (info && (info.Bind & BindMode.DontSell)) return T.CannotSellItem;
    if (this.m.inventory.isLocked(item)) return "Waiting for the server";
    return "";
  }

  /** Junk: Common / ungraded gear 15+ levels under yours with no added stats, and candles. Never belt items. */
  private isJunk(item: UserItem, index: number): boolean {
    if (index < BELT_IDX) return false;
    const info = this.info(item);
    if (!info || this.sellRefusal(item)) return false;
    if (info.Type === ItemType.Torch && /candle/i.test(info.Name)) return true;
    return GEAR.includes(info.Type) && info.Grade <= ItemGrade.Common && addedCount(item) === 0
      && info.RequiredType === RequiredType.Level && info.RequiredAmount + 15 <= this.ui.ctx.player.level;
  }

  private togglePick(index: number, add = false): void {
    if (!this.session.sell) return;
    const item = this.m.inventory.inventory[index];
    if (!item) return;
    const why = this.sellRefusal(item);
    if (why) { this.ui.drag.toast(why); return; }
    if (this.picks.has(item.UniqueID) && !add) this.picks.delete(item.UniqueID); else this.picks.add(item.UniqueID);
    this.say("");
    this.invalidate();
  }

  private selectJunk(): void {
    const inv = this.m.inventory.inventory;
    let n = 0;
    for (let i = 0; i < inv.length; i++) { const it = inv[i]; if (it && this.isJunk(it, i) && !this.picks.has(it.UniqueID)) { this.picks.add(it.UniqueID); n++; } }
    this.say(n ? `Picked ${plural(n, "item")}.` : "Nothing in your bag counts as junk.");
    this.invalidate();
  }

  private renderSell(): void {
    const inv = this.m.inventory, bag = inv.inventory;
    // The bag cells (rebuilt only when the bag size changed)
    const n = Math.max(0, bag.length - BELT_IDX);
    if (this.sellCells.length !== n) {
      for (const c of this.sellCells) c.dispose();
      this.sellCells = [];
      for (let i = 0; i < n; i++) this.sellCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 40, readOnly: true, signal: this.signal }));
      this.bagGrid.replaceChildren(...this.sellCells.map((c) => c.el));
    }
    // Picks that left the bag drop out
    const byId = new Map<bigint, { item: UserItem; index: number }>();
    bag.forEach((it, i) => { if (it) byId.set(it.UniqueID, { item: it, index: i }); });
    for (const id of [...this.picks]) if (!byId.has(id)) this.picks.delete(id);
    for (const c of this.sellCells) {
      const it = c.item;
      c.el.classList.toggle("no", !!it && !!((this.info(it)?.Bind ?? 0) & BindMode.DontSell));
      c.el.classList.toggle("pk", !!it && this.picks.has(it.UniqueID));
      c.el.classList.toggle("junk", !!it && this.isJunk(it, c.ref.index));
    }
    // The picks list
    let total = 0;
    const picked: { item: UserItem; index: number }[] = [];
    for (const id of this.picks) { const p = byId.get(id)!; picked.push(p); total += sellPrice(p.item, this.info(p.item)); }
    const key = picked.map((p) => `${p.item.UniqueID}:${p.index}:${p.item.Count}:${inv.isLocked(p.item)}`).join(",");
    if (key !== this.pickKey) {
      this.pickKey = key;
      for (const c of this.pickCells) c.dispose();
      this.pickCells = [];
      const rows = picked.map((p) => {
        const info = this.info(p.item);
        const cell = this.ui.cells.cell("inv", p.index, { size: 30, readOnly: true, signal: this.signal });
        this.pickCells.push(cell);
        const x = h("button", "x", "×");
        x.type = "button";
        x.title = "Don't sell this";
        x.addEventListener("click", () => { this.picks.delete(p.item.UniqueID); this.invalidate(); });
        return h("div", "ci", cell.el, h("div", "t", h("b", "", nameOf(info, p.item)), h("span", "", `+${fmt(sellPrice(p.item, info))}`)), x);
      });
      this.pickList.replaceChildren(...(rows.length ? rows : [h("div", "empty", "Click items in your bag to sell them.")]));
    }
    setText(this.pickCount, String(picked.length));
    setText(this.sellTotal, fmt(total));
    const over = inv.gold + total > MAX_GOLD;
    this.sellTotal.classList.toggle("bad", over);
    this.sellBtn.setLabel(picked.length ? `Sell ${plural(picked.length, "item")} for ${fmt(total)}` : "Sell");
    this.sellBtn.setDisabled(!picked.length || over, over ? T.CannotCarryMoreGold : "");
  }

  /** Sell: ONE C.SellItems for every pick (whole stacks); the cells lock until each S.SellItem. */
  private sellPicked(): void {
    if (!this.session.sell) return;
    const inv = this.m.inventory;
    const list: { uniqueId: bigint; count: number }[] = [];
    let total = 0;
    for (const id of this.picks) {
      const it = inv.inventory.find((x) => x?.UniqueID === id);
      if (!it || this.sellRefusal(it)) continue;
      list.push({ uniqueId: id, count: it.Count });
      total += sellPrice(it, this.info(it));
    }
    if (!list.length) return;
    if (inv.gold + total > MAX_GOLD) { this.say(T.CannotCarryMoreGold); return; }
    const n = inv.sellItems(list);
    this.picks.clear();
    this.say(n ? `Selling ${plural(n, "item")} for ${fmt(total)} gold…` : "Nothing could be sold.");
    this.invalidate();
  }

  // — Repair —

  private get special(): boolean { return this.session.repair === PanelType.SpecialRepair; }

  /** Why this item can't be repaired here ("" = fine): DontRepair, NoSRepair on a special repair. */
  private repairRefusal(info: ItemInfo | undefined): string {
    if (!info) return "";
    if ((info.Bind & BindMode.DontRepair) || (this.special && (info.Bind & BindMode.NoSRepair))) return T.CannotRepairItem;
    return "";
  }

  /** The damaged bag (and belt) items with durability, in bag order. */
  private damaged(): { item: UserItem; index: number; info: ItemInfo | undefined }[] {
    const out: { item: UserItem; index: number; info: ItemInfo | undefined }[] = [];
    this.m.inventory.inventory.forEach((item, index) => {
      if (!item) return;
      const info = this.info(item);
      if (!info || info.Durability === 0 || item.MaxDura === 0 || item.CurrentDura >= item.MaxDura) return;
      out.push({ item, index, info });
    });
    return out;
  }

  private renderRepair(): void {
    const special = this.special, rate = this.session.rate, inv = this.m.inventory;
    const rows = this.damaged();
    setText(this.repTitle, special ? "Special repair · the maximum is kept, three times the price" : "Repair · each repair wears the maximum down a little");
    const key = `${special}|${rate}|` + rows.map((r) => `${r.item.UniqueID}:${r.index}:${r.item.CurrentDura}:${r.item.MaxDura}:${inv.isLocked(r.item)}:`
      + `${repairFee(r.item, r.info, rate, special) > inv.gold}`).join(",");
    let total = 0, count = 0, lost = 0;
    for (const r of rows) if (!this.repairRefusal(r.info)) { total += repairFee(r.item, r.info, rate, special); count++; if (!special) lost += repairLoss(r.item); }
    if (key !== this.repairKey) {
      this.repairKey = key;
      for (const r of this.repRows) r.cell.dispose();
      this.repRows = [];
      const els: HTMLElement[] = [];
      for (const r of rows) {
        const { item, info } = r, why = this.repairRefusal(info);
        const cost = repairFee(item, info, rate, special), loss = special ? 0 : repairLoss(item), newMax = item.MaxDura - loss;
        const base = Math.max(1, info?.Durability ?? item.MaxDura, item.MaxDura);
        const pct = (n: number) => `${(n / base * 100).toFixed(1)}%`;
        const fill = h("i", item.CurrentDura * 5 < item.MaxDura ? "low" : item.CurrentDura * 2 < item.MaxDura ? "mid" : ""), back = h("u"), wear = h("em");
        setStyle(fill, "width", pct(item.CurrentDura));
        if (!why) { setStyle(back, "left", pct(item.CurrentDura)); setStyle(back, "width", pct(newMax - item.CurrentDura)); }
        if (loss) { setStyle(wear, "left", pct(newMax)); setStyle(wear, "width", pct(loss)); }
        const cell = this.ui.cells.cell("inv", r.index, { size: 36, readOnly: true, signal: this.signal });
        const locked = inv.isLocked(item);
        const end: HTMLElement[] = why ? [h("div", "why", why)] : [h("div", "cost", fmt(cost)), (() => {
          const b = button(locked ? "Waiting…" : "Repair", () => {}, { disabled: locked || cost > inv.gold });
          b.el.dataset.uid = String(item.UniqueID);
          if (cost > inv.gold) b.el.title = T.LowGold;
          return b.el;
        })()];
        const t = h("div", "t", h("b", "", nameOf(info, item)), " ",
          h("small", "", `${r.index < BELT_IDX ? "Belt" : "Bag"} · ${dura(item.CurrentDura)} / ${dura(item.MaxDura)}`),
          why ? "" : loss ? h("small", "loss", ` · max −${dura(loss)}`) : h("small", "kept", " · max kept"),
          h("div", "dura", fill, back, wear));
        const el = h("div", "rp" + (why ? " no" : ""), cell.el, t, ...end);
        this.repRows.push({ el, cell });
        els.push(el);
      }
      if (!els.length) els.push(h("div", "empty", "Nothing in your bag needs repairing. Take worn gear off to repair it here."));
      this.repList.replaceChildren(...els);
    }
    setText(this.repExplain, special
      ? "Special repair costs three times the price and keeps the maximum durability. Some items can't be special repaired."
      : "A normal repair restores durability but takes a thirtieth of what was missing off the maximum.");
    // The other kind of repair, when the current page links to it
    const other = this.links("repair").filter((l) => normKey(l.key) !== (special ? "@SREPAIR" : "@REPAIR"));
    const sk = other.map((l) => l.key).join(",");
    if (this.repSwitch.dataset.key !== sk) {
      this.repSwitch.dataset.key = sk;
      this.repSwitch.replaceChildren(...other.map((l) => button(l.text || "Repair", () => this.m.npcSession.call(l.key), { icon: "swap" }).el));
    }
    this.repSum.replaceChildren(h("div", "", h("b", "", String(count)), ` ${count === 1 ? "item" : "items"} to repair`),
      !special && lost ? h("div", "", "Maximum lost: ", h("b", "", dura(lost))) : "");
    setText(this.repTotal, fmt(total));
    this.repTotal.classList.toggle("bad", total > inv.gold);
    this.repAllBtn.setLabel(total > inv.gold ? "Not enough gold for all" : count ? `Repair all (bag) for ${fmt(total)}` : "Repair all (bag)");
    this.repAllBtn.setDisabled(!count || total > inv.gold, total > inv.gold ? "Repair items one at a time, or bring more gold" : "");
  }

  private repairOne(id: bigint): void {
    if (this.session.repair === null) return;
    const inv = this.m.inventory, item = inv.inventory.find((x) => x?.UniqueID === id);
    if (!item || inv.isLocked(item)) return;
    const info = this.info(item), why = this.repairRefusal(info);
    if (why) { this.say(why); return; }
    if (repairFee(item, info, this.session.rate, this.special) > inv.gold) { this.say(T.LowGold); return; }
    if (inv.repairItems([id], this.special)) this.say(`Repairing ${nameOf(info, item)}…`);
    this.invalidate();
  }

  /** Repair all (bag): ONE C.RepairItems for every damaged item the merchant takes (worn gear is not repaired here). */
  private repairAll(): void {
    if (this.session.repair === null) return;
    const inv = this.m.inventory, rate = this.session.rate, special = this.special;
    const ids: bigint[] = [];
    let total = 0;
    for (const r of this.damaged()) {
      if (this.repairRefusal(r.info) || inv.isLocked(r.item)) continue;
      ids.push(r.item.UniqueID);
      total += repairFee(r.item, r.info, rate, special);
    }
    if (!ids.length) return;
    if (total > inv.gold) { this.say(T.LowGold); return; }
    const n = inv.repairItems(ids, special);
    this.say(n ? `Repairing ${plural(n, "item")} for ${fmt(total)} gold…` : "");
    this.invalidate();
  }
}

registerWindow(MERCHANT_WINDOW, (host) => new MerchantWindow(host, merchantSession(host)), {
  binds: [],
  watch: (host) => { merchantSession(host); },
});
