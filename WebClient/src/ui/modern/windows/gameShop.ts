// Ported from play/hudGameShop.ts (GameShopMock): the new UI's game shop over the real shop model (src/game/shop.ts).
// Desktop counterparts: GameShopDialog + GameShopCell (Client/MirScenes/Dialogs/GameshopDialog.cs,
// Client/MirControls/MirGameShopCell.cs) and GameScene.GameShopUpdate / GameShopStock; the classic port is
// src/ui/gameShopWindow.ts (its rules are kept). Changes from the mock:
//  - the catalogue is Shop.items (S.GameShopInfo at login, S.GameShopStock, LiveDb edits through S.GameDataChanged =
//    the "gameDataApplied" UI event); items are resolved by GIndex on every render, the art / name / grade / tooltip
//    come from the item's own GameShopItem.Info (the shop carries its ItemInfo);
//  - wallets are Inventory.credit / Inventory.gold (S.UserInformation, Gained / Lose Credit / Gold), nothing local;
//  - sections All / Top sellers (TopItem) / Deals (Deal) / New (dated within 7 days, shown once one arrived, like the
//    desktop's New tab) / Wishlist, then the categories of what is left with counts; the class filter starts on the
//    user's class on the first open (GameShopDialog.Show) and matches GameShopItem.Class or "All"; the search
//    (debounced) matches the item's friendly name or category; sort Featured / Name / Credits / Gold; 12 cards a page;
//  - DEAL ribbon from GameShopItem.Deal; the struck-through "was" price is DealCreditPrice / DealGoldPrice (DB v119,
//    0 = none: no strike, no saving text) instead of the mock's made-up 25 %;
//  - stock: GameShopItem.Stock is what is LEFT (the server sends Stock - purchased: per player for iStock, else
//    server-wide); 0 = unlimited; S.GameShopStock 0 removes the item;
//  - quantity 1-99, at most 5 stacks (Quantity x Count / StackSize <= 5) and within stock (GameShopCell.quantityUp,
//    Shift = 10); credits or gold per CanBuyCredit / CanBuyGold;
//  - Buy asks with the desktop's confirm wording (+ "delivered by mail") and sends C.GameshopBuy; Gift sends
//    C.GameshopGift to a friend picked from the friends list (friendships are mutual since phase 2.7; online or not).
//    SERVER AUTHORITATIVE: nothing is deducted or added locally. The buttons stay locked until the server answers
//    (S.LoseCredit / S.LoseGold for the price) or ANSWER_MS passes (refusals are chat lines only: not enough
//    currency, out of stock, not a friend, mailbox full); only a charged purchase goes into the session's purchase list;
//  - the carousel (top items and deals) turns on the UI ticker only while the window is shown;
//  - the wishlist is a per-browser convenience (localStorage, ItemIndex keys, try / catch);
//  - HIDDEN (Crystal has no system for them): "Top up" credits, "added to your bag" (purchases arrive by mail),
//    Preview on the character (GameShopViewer is not ported), purchase history beyond this session's local list,
//    the mock's category descriptions (the item tooltip on hover tells the truth instead).
// Narrow layout (a layer below 1040 px): a narrower window, nav and carousel.

import type { GameShopItem, UserItem } from "../../../protocol/server.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { dealCreditPrice, dealGoldPrice, isNewShopItem, MAX_GIFT_QUANTITY, ShopPayType, type Shop } from "../../../game/shop.ts";
import type { Inventory } from "../../../game/inventory.ts";
import type { Friends } from "../../../game/friends.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  CLASSES, UiWindow, asButton, button, className, fmt, fmtCompact, fmtTimeOfDay, gradeVar, h, injectCss, later, plural, pretty,
  reducedMotion, restartAnim, searchBox, setText, svg, type Btn, type IconName,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.gameShop; the hotkey is its MENU_WINDOWS binding, GameShop = Y). */
export const GAME_SHOP_WINDOW = "gameShop";

/** Cards a page (4 x 3). */
const PAGE = 12;
/** The carousel turns every 5 s while shown (not while hovered or a product is open). */
const CAROUSEL_MS = 5000;
const FEATURED_MAX = 8;
/** How long a purchase waits for S.LoseCredit / S.LoseGold before the buttons unlock (refusals are chat lines only). */
const ANSWER_MS = 5000;
/** GameShopCell.quantityUp: at most 5 stacks a purchase (PlayerObject.GameshopPurchase returns silently above). */
const MAX_STACKS = 5;
const HISTORY_MAX = 20;
const WISH_STORE = "crystal.ui.shopWishlist";

/** GameShopItem.Class for every class. */
const ALL_CLASSES = "All";

type Section = "all" | "top" | "deals" | "new" | "wish";
type Sort = "featured" | "name" | "creditUp" | "creditDown" | "goldUp" | "goldDown";
type Pay = (typeof ShopPayType)[keyof typeof ShopPayType];

const SECTIONS: readonly [Section, string, IconName][] = [
  ["all", "All items", "square"], ["top", "Top sellers", "star"], ["deals", "Deals", "tag"], ["new", "New", "spark"], ["wish", "Wishlist", "heartFull"],
];
const SORTS: readonly [Sort, string][] = [
  ["featured", "Featured"], ["name", "Name"], ["creditUp", "Credits: low to high"], ["creditDown", "Credits: high to low"],
  ["goldUp", "Gold: low to high"], ["goldDown", "Gold: high to low"],
];

/** The shop's texts (desktop English where it has one). */
const T = {
  CantAfford: "You can't afford the selected item.",
  NoAnswer: "The shop did not charge you: see the chat for why.",
  Gone: "That item is no longer in the shop.",
  NoFriends: "Gifts go to friends: add a friend first.",
  Waiting: "Waiting for the shop...",
  TooBig: "This pack is too big to buy (more than 5 stacks).",
  ByMail: "Purchases are delivered by mail.",
};

/** A purchase sent to the server, waiting for its charge. */
interface Pending {
  gIndex: number; name: string; img: number; count: number; qty: number; cost: number; pType: Pay; to: string;
  /** The wallet before the send (the charge is the drop). */
  before: number;
  cancel: () => void;
}
/** One charged purchase of this session (local only: Crystal keeps no history the client can read). */
interface HistoryRow { img: number; name: string; qty: number; count: number; paid: string; to: string; at: number }

const CSS = `
.hudm .gswin { width: 1000px; }
.hudm .gswin .panel { height: 660px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .gswin .ic { width: 14px; height: 14px; }
/* Top bar */
.hudm .gswin .gsbar { display: flex; align-items: center; gap: 8px; padding: 9px 14px; flex: none; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gswin .wal { display: flex; align-items: center; gap: 7px; height: 28px; padding: 0 12px; border-radius: 14px; font-size: 13px; font-weight: 800; font-variant-numeric: tabular-nums; }
.hudm .gswin .wal small { font-weight: 600; color: var(--dim); }
.hudm .gswin .wal.cr { color: #fff; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), color-mix(in srgb, var(--ac) 15%, transparent)); border: 1px solid var(--ac); }
.hudm .gswin .wal.gd { color: var(--gold); background: color-mix(in srgb, var(--gold) 8%, transparent); border: 1px solid color-mix(in srgb, var(--gold) 40%, transparent); }
.hudm .gswin .wal.bump { animation: hudm-gsbump .6s ease-out; }
@keyframes hudm-gsbump { 30% { transform: scale(1.12); } }
.hudm .gswin .gem { width: 11px; height: 11px; transform: rotate(45deg); background: linear-gradient(135deg, #fff, var(--ac)); flex: none; }
.hudm .gswin .coin { width: 12px; height: 12px; border-radius: 50%; background: radial-gradient(circle at 35% 35%, #ffe9a0, #c9921f); flex: none; }
.hudm .gswin .gsbar .sp { flex: 1; }
.hudm .gswin .gsbar .ui-search { width: 210px; }
.hudm .gswin .gsbar .fld { height: 28px; }
.hudm .gswin .gsbar .btn { height: 28px; }
/* Carousel */
.hudm .gswin .hero { position: relative; height: 140px; margin: 10px 14px 0; flex: none; border-radius: var(--rad); overflow: hidden; border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent);
  background: radial-gradient(ellipse 50% 120% at 18% 50%, color-mix(in srgb, var(--ac) 35%, transparent), transparent 70%), linear-gradient(110deg, var(--pa), var(--pb) 60%, color-mix(in srgb, var(--ac) 18%, var(--pb))); }
.hudm .gswin .hero::before { content: ""; position: absolute; top: 0; bottom: 0; left: 0; width: 40%; pointer-events: none; background: linear-gradient(105deg, transparent, rgba(255,255,255,.07) 50%, transparent);
  transform: translateX(-100%); will-change: transform; animation: hudm-gsshine 8s linear infinite; }
@keyframes hudm-gsshine { 0% { transform: translateX(-100%); } 50%, 100% { transform: translateX(260%); } }
.hudm .gswin .slide { position: absolute; inset: 0; display: flex; align-items: center; gap: 24px; padding: 0 44px 0 44px; animation: hudm-gsslide .5s ease-out; }
@keyframes hudm-gsslide { from { opacity: 0; transform: translateX(30px); } }
.hudm .gswin .slide .big { position: relative; width: 100px; height: 100px; flex: none; display: flex; align-items: center; justify-content: center; }
.hudm .gswin .slide .big::before { content: ""; position: absolute; inset: -8px; border-radius: 50%;
  background: conic-gradient(from 0deg, transparent, color-mix(in srgb, var(--ac) 40%, transparent), transparent 30%, color-mix(in srgb, var(--ac) 40%, transparent), transparent 60%);
  animation: hudm-gsspin 8s linear infinite; }
@keyframes hudm-gsspin { to { transform: rotate(360deg); } }
.hudm .gswin .slide .big i { position: relative; width: 96px; height: 86px; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .gswin .slide .tx { flex: 1; min-width: 0; }
.hudm .gswin .slide .kick { font-size: 10px; font-weight: 800; letter-spacing: .2em; text-transform: uppercase; color: var(--ac); }
.hudm .gswin .slide h2 { margin: 4px 0; font-size: 24px; color: #fff; text-shadow: 0 2px 6px rgba(0,0,0,.7); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gswin .slide p { margin: 0; font-size: 12px; color: color-mix(in srgb, var(--tx) 80%, var(--dim)); }
.hudm .gswin .slide .cta { display: flex; flex-direction: column; align-items: flex-end; gap: 8px; }
.hudm .gswin .slide .price { display: flex; align-items: center; gap: 8px; font-size: 20px; font-weight: 800; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gswin .slide .price s, .hudm .gswin .prices s, .hudm .gswin .pv .total s { color: var(--dim); font-weight: 600; font-size: .7em; }
.hudm .gswin .slide .btn { height: 32px; padding: 0 20px; font-size: 12px; border-radius: 16px; }
.hudm .gswin .dots { position: absolute; left: 50%; bottom: 7px; transform: translateX(-50%); display: flex; gap: 6px; z-index: 2; }
.hudm .gswin .dots i { width: 7px; height: 7px; border-radius: 4px; cursor: pointer; background: rgba(255,255,255,.3); transition: width .2s; }
.hudm .gswin .dots i.on { width: 20px; background: var(--ac); }
.hudm .gswin .arw { position: absolute; top: 50%; z-index: 2; width: 26px; height: 26px; margin-top: -13px; padding: 0; border-radius: 50%; display: flex; align-items: center; justify-content: center; cursor: pointer;
  color: #fff; background: rgba(0,0,0,.45); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .gswin .arw:hover { background: color-mix(in srgb, var(--ac) 40%, #000); }
.hudm .gswin .arw.l { left: 8px; } .hudm .gswin .arw.r { right: 8px; }
/* Body */
.hudm .gswin .gbody { flex: 1; min-height: 0; display: flex; margin-top: 10px; }
.hudm .gswin .nav { width: 172px; flex: none; padding: 0 8px 10px 14px; display: flex; flex-direction: column; gap: 2px; overflow-y: auto; scrollbar-width: thin; }
.hudm .gswin .nav .it { display: flex; align-items: center; gap: 9px; flex: none; height: 28px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad); font: inherit; font-size: 12px; font-weight: 600;
  text-align: left; color: var(--dim); border: 1px solid transparent; background: none; }
.hudm .gswin .nav .it:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .gswin .nav .it.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gswin .nav .it.on svg { color: var(--ac); }
.hudm .gswin .nav .it span { flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gswin .nav .it i { font-style: normal; font-size: 10px; color: var(--dim); }
.hudm .gswin .nav hr { border: 0; height: 1px; margin: 6px 4px; flex: none; background: color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .gswin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 0 14px 10px 4px; }
.hudm .gswin .grid { flex: 1; min-height: 0; display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); grid-template-rows: repeat(3, minmax(0, 1fr)); gap: 8px; }
.hudm .gswin .pc { position: relative; display: flex; flex-direction: column; min-height: 0; border-radius: var(--rad); cursor: pointer; overflow: hidden; transition: transform .15s, border-color .15s;
  background: linear-gradient(180deg, color-mix(in srgb, var(--frl) 8%, transparent), color-mix(in srgb, var(--pb) 70%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gswin .pc:hover { transform: translateY(-2px); border-color: var(--ac); }
.hudm .gswin .pc .art { position: relative; flex: 1; min-height: 52px; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(circle at 50% 55%, color-mix(in srgb, var(--ac) 20%, transparent), transparent 62%); }
.hudm .gswin .pc .art i { width: 60px; height: 52px; background: center / contain no-repeat; image-rendering: pixelated; transition: transform .2s; }
.hudm .gswin .pc:hover .art i { transform: scale(1.08); }
.hudm .gswin .pc .cnt { position: absolute; right: 8px; bottom: 4px; font-size: 11px; font-weight: 800; color: #fff; text-shadow: 0 0 3px #000; }
.hudm .gswin .pc .low { position: absolute; left: 6px; bottom: 4px; font-size: 9px; font-weight: 800; padding: 1px 6px; border-radius: 6px; color: #fff; background: color-mix(in srgb, var(--bad) 75%, #000); }
.hudm .gswin .rib { position: absolute; left: -26px; top: 9px; z-index: 2; width: 92px; transform: rotate(-38deg); text-align: center; font-size: 9px; font-weight: 800; letter-spacing: .12em; color: #fff;
  background: linear-gradient(90deg, color-mix(in srgb, var(--bad) 80%, #000), var(--bad)); box-shadow: 0 2px 6px rgba(0,0,0,.5); }
.hudm .gswin .star { position: absolute; left: 7px; top: 6px; z-index: 2; color: var(--warn); }
.hudm .gswin .rib ~ .star { top: 30px; }
.hudm .gswin .star svg { fill: currentColor; stroke: #000; stroke-width: 1; width: 15px; height: 15px; }
.hudm .gswin .newb { position: absolute; left: 50%; top: 5px; transform: translateX(-50%); z-index: 2; font-size: 8px; font-weight: 800; letter-spacing: .14em; padding: 1px 6px; border-radius: 6px; color: #000; background: var(--ok); }
.hudm .gswin .heart { position: absolute; right: 6px; top: 6px; z-index: 2; width: 22px; height: 22px; padding: 0; display: flex; align-items: center; justify-content: center; border-radius: 50%; border: 0; cursor: pointer;
  color: var(--dim); background: rgba(0,0,0,.35); }
.hudm .gswin .heart:hover { color: #fff; } .hudm .gswin .heart.on { color: #ff6a8a; } .hudm .gswin .heart.on svg { fill: currentColor; }
.hudm .gswin .pc .meta { flex: none; padding: 5px 9px 7px; }
.hudm .gswin .pc .meta b { display: block; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gswin .pc .meta span { display: block; font-size: 10px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gswin .prices { display: flex; align-items: center; gap: 8px; margin-top: 3px; font-size: 12px; font-weight: 800; font-variant-numeric: tabular-nums; white-space: nowrap; overflow: hidden; }
.hudm .gswin .prices .c { display: flex; align-items: center; gap: 5px; color: #fff; } .hudm .gswin .prices .g { display: flex; align-items: center; gap: 4px; color: var(--gold); font-size: 11px; }
.hudm .gswin .gempty { grid-column: 1 / -1; grid-row: 1 / -1; display: flex; align-items: center; justify-content: center; color: var(--dim); }
.hudm .gswin .pager { flex: none; display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding-top: 8px; font-size: 11px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .gswin .pager .sum { margin-right: auto; }
/* Product view (an opaque scrim inside the window: no backdrop-filter over the live canvas) */
.hudm .gswin .modal { position: absolute; inset: 31px 0 0; z-index: 10; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,.7); animation: hudm-gsfade .2s; }
@keyframes hudm-gsfade { from { opacity: 0; } }
.hudm .gswin .pv { position: relative; width: 640px; max-width: calc(100% - 24px); display: flex; gap: 20px; padding: 20px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac);
  box-shadow: 0 0 0 1px #000, 0 20px 50px rgba(0,0,0,.7); animation: hudm-gspop .25s ease-out; }
@keyframes hudm-gspop { from { transform: scale(.95); opacity: 0; } }
.hudm .gswin .pv .close { position: absolute; right: 8px; top: 6px; width: 22px; height: 22px; padding: 0; border: 0; background: none; font: inherit; font-size: 18px; color: var(--dim); cursor: pointer; }
.hudm .gswin .pv .close:hover { color: #fff; }
.hudm .gswin .pv .stage { position: relative; width: 200px; height: 230px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 60%, color-mix(in srgb, var(--ac) 30%, transparent), transparent 65%), color-mix(in srgb, var(--pb) 80%, #000); }
.hudm .gswin .pv .stage::before { content: ""; position: absolute; width: 340px; height: 340px; pointer-events: none;
  background: repeating-conic-gradient(from 0deg, color-mix(in srgb, var(--ac) 20%, transparent) 0 10deg, transparent 10deg 30deg);
  animation: hudm-gsspin 20s linear infinite; mask: radial-gradient(circle, #000 20%, transparent 65%); }
.hudm .gswin .pv .stage i { position: relative; width: 140px; height: 130px; background: center / contain no-repeat; image-rendering: pixelated; animation: hudm-gsfloat 3s ease-in-out infinite; }
@keyframes hudm-gsfloat { 50% { transform: translateY(-8px); } }
.hudm .gswin .pv .info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 9px; }
.hudm .gswin .pv h2 { margin: 0; padding-right: 20px; font-size: 20px; }
.hudm .gswin .pv .tags { display: flex; gap: 5px; flex-wrap: wrap; }
.hudm .gswin .pv .tag { font-size: 9px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase; padding: 2px 7px; border-radius: 7px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .gswin .pv .tag.hot { color: #fff; background: color-mix(in srgb, var(--bad) 75%, #000); border-color: var(--bad); }
.hudm .gswin .pv .tag.new { color: #000; background: var(--ok); border-color: var(--ok); }
.hudm .gswin .pv .hint { font-size: 11px; color: var(--dim); }
.hudm .gswin .pv .pay { display: flex; gap: 6px; }
.hudm .gswin .pv .pay button { flex: 1; display: flex; align-items: center; justify-content: center; gap: 7px; height: 36px; border-radius: var(--rad); cursor: pointer; font: inherit; font-size: 14px; font-weight: 800;
  font-variant-numeric: tabular-nums; border: 1px solid var(--fr); color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .gswin .pv .pay button.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, var(--pb)); }
.hudm .gswin .pv .pay button.off { opacity: .35; cursor: not-allowed; }
.hudm .gswin .pv .row { display: flex; align-items: center; gap: 10px; font-size: 12px; color: var(--dim); }
.hudm .gswin .qty { display: flex; align-items: center; border: 1px solid var(--fr); border-radius: 14px; overflow: hidden; }
.hudm .gswin .qty button { width: 26px; height: 26px; padding: 0; border: 0; background: none; display: flex; align-items: center; justify-content: center; cursor: pointer; font: inherit; font-weight: 800; color: var(--tx); }
.hudm .gswin .qty button:hover { background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .gswin .qty span { min-width: 30px; text-align: center; font-weight: 800; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gswin .pv .total { margin-left: auto; font-size: 12px; color: var(--dim); }
.hudm .gswin .pv .total b { font-size: 17px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gswin .pv .total b.bad { color: var(--bad-soft); }
.hudm .gswin .pv .gift { display: flex; gap: 6px; align-items: center; padding: 7px 8px; border-radius: var(--rad); font-size: 12px; background: color-mix(in srgb, var(--frl) 6%, transparent);
  border: 1px dashed color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .gswin .pv .gift select { flex: 1; min-width: 0; }
.hudm .gswin .pv .note { min-height: 15px; font-size: 11px; color: var(--dim); }
.hudm .gswin .pv .note.bad { color: var(--bad-soft); }
.hudm .gswin .pv .acts { display: flex; gap: 6px; margin-top: auto; }
.hudm .gswin .pv .acts .btn { height: 34px; padding: 0 14px; border-radius: 17px; }
.hudm .gswin .pv .acts .btn.main { flex: 1; font-size: 13px; }
/* Purchase list, toast, confetti */
.hudm .gswin .hist { position: absolute; right: 14px; top: 78px; z-index: 9; width: 310px; max-height: 400px; overflow-y: auto; padding: 10px; border-radius: var(--rad); background: var(--page); border: 1px solid var(--ac);
  box-shadow: 0 10px 30px rgba(0,0,0,.7); }
.hudm .gswin .hist .hr { display: flex; align-items: center; gap: 8px; padding: 5px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .gswin .hist .hr i { width: 34px; height: 34px; flex: none; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .gswin .hist .hr span { flex: 1; min-width: 0; } .hudm .gswin .hist .hr small { color: var(--dim); }
.hudm .gswin .toast { position: absolute; left: 50%; bottom: 18px; z-index: 12; max-width: 80%; padding: 9px 18px; border-radius: 20px; font-size: 13px; font-weight: 700; color: #fff; pointer-events: none;
  transform: translateX(-50%); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 60%, #000), color-mix(in srgb, var(--ac) 35%, #000)); border: 1px solid var(--ac); }
.hudm .gswin .toast.bad { background: color-mix(in srgb, var(--bad) 40%, #000); border-color: var(--bad); }
.hudm .gswin .toast.show { animation: hudm-gstoast 3.4s ease-out forwards; }
@keyframes hudm-gstoast { 0% { opacity: 0; transform: translate(-50%, 16px); } 8%, 82% { opacity: 1; transform: translate(-50%, 0); } 100% { opacity: 0; transform: translate(-50%, 0); } }
.hudm .gswin .conf-p { position: absolute; z-index: 11; width: 8px; height: 12px; border-radius: 2px; pointer-events: none; animation: hudm-gsconf 1.4s cubic-bezier(.2,.7,.4,1) forwards; }
@keyframes hudm-gsconf { from { transform: translate(0, 0) rotate(0); opacity: 1; } to { transform: translate(var(--dx), var(--dy)) rotate(var(--rot)); opacity: 0; } }
/* Narrow layer (1024 px) */
.hudm.narrow .gswin { width: 900px; }
.hudm.narrow .gswin .nav { width: 140px; padding-left: 10px; }
.hudm.narrow .gswin .nav .it { gap: 6px; padding: 0 7px; }
.hudm.narrow .gswin .hero { height: 112px; }
.hudm.narrow .gswin .slide { gap: 16px; padding: 0 40px; }
.hudm.narrow .gswin .slide .big { width: 80px; height: 80px; } .hudm.narrow .gswin .slide .big i { width: 76px; height: 70px; }
.hudm.narrow .gswin .slide h2 { font-size: 19px; }
.hudm.narrow .gswin .gsbar .ui-search { width: 160px; }
.hudm.narrow .gswin .wal { padding: 0 9px; }
.hudm.narrow .gswin .wal small { display: none; }
`;

// — Shop rules (pure) —

/** GameShopCell's name: ItemInfo.FriendlyName. */
function shopName(item: GameShopItem): string { return pretty(itemFriendlyName(item.Info)); }

/** CanBuyCredit / CanBuyGold. */
function canPay(item: GameShopItem, pay: Pay): boolean { return pay === ShopPayType.Credit ? item.CanBuyCredit : item.CanBuyGold; }
function priceOf(item: GameShopItem, pay: Pay): number { return pay === ShopPayType.Credit ? item.CreditPrice : item.GoldPrice; }
/** The struck-through "was" price (v119; 0 = none). Shown only when it is above today's price. */
function wasOf(item: GameShopItem, pay: Pay): number {
  const was = pay === ShopPayType.Credit ? dealCreditPrice(item) : dealGoldPrice(item);
  return was > priceOf(item, pay) ? was : 0;
}
/** The currency a card / slide leads with: credits when allowed, else gold. */
function leadPay(item: GameShopItem): Pay { return item.CanBuyCredit || !item.CanBuyGold ? ShopPayType.Credit : ShopPayType.Gold; }

/** GameShopCell.quantityUp's limits: 99, at most 5 stacks (Quantity x Count / StackSize), the stock left (0 = unlimited).
 *  0 = the pack alone is over 5 stacks (the server refuses every quantity). */
function maxQuantity(item: GameShopItem): number {
  const stack = Math.max(1, item.Info.StackSize), count = Math.max(1, item.Count);
  let q = Math.min(MAX_GIFT_QUANTITY, Math.floor((MAX_STACKS * stack) / count));
  if (item.Stock > 0) q = Math.min(q, item.Stock);
  return Math.max(0, q);
}

/** GameShopCell.OnMouseMove's ShowItem (MaxDura = CurrentDura = Info.Durability, Count = Item.Count) for the tooltip. */
function showItem(item: GameShopItem): UserItem {
  return {
    UniqueID: 0n, ItemIndex: item.Info.Index, CurrentDura: item.Info.Durability, MaxDura: item.Info.Durability, Count: item.Count,
    SoulBoundId: -1, Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [],
    RefinedValue: 0, RefineAdded: 0, RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false,
    Sealed: null, GMMade: false,
  };
}

/** The stock badge text ("" = unlimited). */
function stockText(item: GameShopItem): string {
  if (item.Stock <= 0) return "";
  return item.iStock ? `${fmt(item.Stock)} left for you` : `Only ${fmt(item.Stock)} left`;
}

function picture(el: HTMLElement, img: number): void {
  void libPicture(Lib.Items, img).then((pic) => { if (pic) el.style.backgroundImage = `url(${pic.url})`; });
}

function priceKids(item: GameShopItem, pay: Pay, mult = 1): (Node | string)[] {
  const was = wasOf(item, pay);
  const kids: (Node | string)[] = [h("span", pay === ShopPayType.Credit ? "gem" : "coin")];
  if (was) kids.push(h("s", "", fmt(was * mult)));
  kids.push(fmt(priceOf(item, pay) * mult));
  return kids;
}

function loadWishlist(): Set<number> {
  try {
    const v = JSON.parse(localStorage.getItem(WISH_STORE) ?? "[]") as unknown;
    return new Set(Array.isArray(v) ? v.filter((x): x is number => typeof x === "number") : []);
  } catch { return new Set(); }
}

export class GameShopWindow extends UiWindow {
  private readonly shop: Shop;
  private readonly inv: Inventory;
  private readonly friends: Friends;

  // — Filters —
  private section: Section = "all";
  private category: string | null = null;
  /** GameShopItem.Class to show ("" = every class). */
  private cls = "";
  private query = "";
  private sort: Sort = "featured";
  private page = 0;
  private classSet = false;
  private readonly wish = loadWishlist();

  // — Carousel —
  private slide = 0;
  private hoverHero = false;

  // — Product view —
  private viewG: number | null = null;
  private viewBuiltFor: GameShopItem | null = null;
  private pay: Pay = ShopPayType.Credit;
  private qty = 1;
  private giftOpen = false;
  private giftTo = "";
  private friendsKey = "";
  private note = "";
  private noteBad = false;
  private pending: Pending | null = null;

  // — Purchase list (this session) —
  private readonly history: HistoryRow[] = [];
  private showHist = false;

  // — DOM —
  private readonly crEl = h("span");
  private readonly gdEl = h("span");
  private readonly crWal = h("div", "wal cr", h("span", "gem"), this.crEl, h("small", "", "credits"));
  private readonly gdWal = h("div", "wal gd", h("span", "coin"), this.gdEl);
  private lastCredit = -1;
  private lastGold = -1;
  private readonly classSel = h("select", "fld");
  private readonly sortSel = h("select", "fld");
  private readonly histBtn: Btn;
  private readonly search: ReturnType<typeof searchBox>;
  private readonly hero = h("div", "hero");
  private heroKey = "";
  private readonly nav = h("div", "nav");
  private navKey = "";
  private readonly grid = h("div", "grid");
  private gridKey = "";
  private readonly sumEl = h("span", "sum");
  private readonly pageEl = h("span");
  private readonly prevBtn: Btn;
  private readonly nextBtn: Btn;
  private readonly modal = h("div", "modal");
  private readonly hist = h("div", "hist");
  private readonly toastEl = h("div", "toast");
  private readonly panel: HTMLElement;
  private stopToast: (() => void) | null = null;

  // Product view parts (rebuilt when another product opens)
  private pvPay = new Map<Pay, HTMLButtonElement>();
  private pvQty = h("span");
  private pvTotal = h("b");
  private pvWas = h("s");
  private pvStock = h("span", "tag hot");
  private pvGiftRow = h("div", "gift");
  private pvGiftSel = h("select", "fld");
  private pvNote = h("div", "note");
  private pvBuy: Btn | null = null;
  private pvGift: Btn | null = null;
  private pvWish: Btn | null = null;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "gswin", 12, 20);
    injectCss("win-gameshop", CSS);
    const m = ui.ctx.models;
    this.shop = m.shop;
    this.inv = m.inventory;
    this.friends = m.friends;
    const sig = this.signal;

    // Top bar
    this.search = searchBox("Search the shop...", (q) => { this.query = q.toLowerCase(); this.page = 0; this.invalidate(); }, { signal: sig });
    this.sortSel.append(...SORTS.map(([v, label]) => Object.assign(h("option", "", label), { value: v })));
    this.sortSel.title = "Sort";
    this.listen(this.sortSel, "change", () => { this.sort = this.sortSel.value as Sort; this.page = 0; this.invalidate(); });
    this.classSel.append(Object.assign(h("option", "", "All classes"), { value: "" }), ...CLASSES.map((c) => Object.assign(h("option", "", className(c)), { value: className(c) })));
    this.classSel.title = "Class";
    this.listen(this.classSel, "change", () => { this.cls = this.classSel.value; this.category = null; this.page = 0; this.invalidate(); });
    this.histBtn = button("Purchases", () => { this.showHist = !this.showHist; this.renderHistory(); }, { title: "What you bought in this session" });
    const bar = h("div", "gsbar", this.crWal, this.gdWal, h("div", "sp"), this.search.el, this.sortSel, this.classSel, this.histBtn.el);

    // Carousel (pauses while hovered)
    this.listen(this.hero, "pointerenter", () => { this.hoverHero = true; });
    this.listen(this.hero, "pointerleave", () => { this.hoverHero = false; });

    // Pager
    this.prevBtn = button("", () => { if (this.page > 0) { this.page--; this.invalidate(); } }, { icon: "chevronLeft", title: "Previous page" });
    this.nextBtn = button("", () => { this.page++; this.invalidate(); }, { icon: "chevronRight", title: "Next page" });
    const main = h("div", "main", this.grid, h("div", "pager", this.sumEl, this.prevBtn.el, this.pageEl, this.nextBtn.el));

    this.modal.hidden = true;
    this.listen(this.modal, "pointerdown", (e) => { if (e.target === this.modal) this.closeView(); });
    this.hist.hidden = true;
    this.toastEl.hidden = true;
    this.panel = h("div", "panel", this.titleBar("GAME SHOP", "Close (Y)"), bar, this.hero, h("div", "gbody", this.nav, main), this.modal, this.hist, this.toastEl);
    this.el.append(this.panel);

    // The carousel turns only while shown (UI ticker)
    this.everyShown(CAROUSEL_MS, () => {
      if (this.hoverHero || this.viewG !== null) return;
      this.slide++;
      this.heroKey = "";
      this.renderHero();
    });

    // Model events: invalidate (re-rendered next frame while open, else on the next open)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.shop.added.on(() => this.invalidate()));
    on(this.shop.stockChanged.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "gameDataApplied" && e.shopChanged) { this.viewBuiltFor = null; this.invalidate(); } }));
    on(this.friends.changed.on(() => { if (this.viewG !== null) this.invalidate(); }));
    on(this.inv.subscribe(() => this.walletChanged()));
  }

  // — Lifecycle —

  protected override shown(): void {
    if (!this.classSet) {
      // GameShopDialog.Show: the class filter starts on the user's class
      this.classSet = true;
      const c = this.ui.ctx.player.user?.look.class;
      this.cls = c === undefined ? "" : className(c);
      this.classSel.value = this.cls;
      this.invalidate();
    }
    this.updateWallet(false);
  }

  protected override closed(): void {
    this.ui.tooltip.hide();
    this.closeView(false);
    this.showHist = false;
    this.hist.hidden = true;
    this.hoverHero = false;
  }

  protected override disposed(): void {
    this.pending?.cancel();
    this.pending = null;
  }

  override key(e: KeyboardEvent): boolean {
    if (e.key !== "Escape" || e.shiftKey || e.repeat) return false;
    if (this.viewG !== null) { this.closeView(); return true; }
    if (this.showHist) { this.showHist = false; this.hist.hidden = true; return true; }
    return false;
  }

  /** Opens the shop on one item (by GIndex); false when it is not listed. */
  openItem(gIndex: number): boolean {
    if (!this.find(gIndex)) return false;
    this.toggle(true);
    this.openView(gIndex);
    return true;
  }

  // — Data —

  private find(gIndex: number): GameShopItem | null {
    for (const it of this.shop.items) if (it.GIndex === gIndex) return it;
    return null;
  }

  private matchesBase(item: GameShopItem): boolean {
    if (this.cls && item.Class !== this.cls && item.Class !== ALL_CLASSES) return false;
    if (!this.query) return true;
    const q = this.query;
    return itemFriendlyName(item.Info).toLowerCase().includes(q) || shopName(item).toLowerCase().includes(q) || item.Category.toLowerCase().includes(q);
  }

  private matchesSection(item: GameShopItem, s: Section): boolean {
    switch (s) {
      case "all": return true;
      case "top": return item.TopItem;
      case "deals": return item.Deal;
      case "new": return isNewShopItem(item);
      case "wish": return this.wish.has(item.Info.Index);
    }
  }

  private sorted(list: GameShopItem[]): GameShopItem[] {
    const byName = (a: GameShopItem, b: GameShopItem) => itemFriendlyName(a.Info).localeCompare(itemFriendlyName(b.Info));
    const byPrice = (pay: Pay, dir: number) => (a: GameShopItem, b: GameShopItem) => {
      const ca = canPay(a, pay), cb = canPay(b, pay);
      if (ca !== cb) return ca ? -1 : 1; // items not sold for that currency go last
      return (priceOf(a, pay) - priceOf(b, pay)) * dir || byName(a, b);
    };
    switch (this.sort) {
      case "name": return list.sort(byName);
      case "creditUp": return list.sort(byPrice(ShopPayType.Credit, 1));
      case "creditDown": return list.sort(byPrice(ShopPayType.Credit, -1));
      case "goldUp": return list.sort(byPrice(ShopPayType.Gold, 1));
      case "goldDown": return list.sort(byPrice(ShopPayType.Gold, -1));
      default: return list.sort((a, b) => Number(b.TopItem) - Number(a.TopItem) || Number(b.Deal) - Number(a.Deal) || byName(a, b));
    }
  }

  // — Render —

  protected override render(): void {
    this.updateWallet(false);
    const base: GameShopItem[] = [];
    for (const it of this.shop.items) if (this.matchesBase(it)) base.push(it);
    if (this.section === "new" && !this.shop.hasNewItems) this.section = "all";
    this.renderNav(base);
    const inSection = base.filter((it) => this.matchesSection(it, this.section));
    if (this.category !== null && !inSection.some((it) => it.Category === this.category)) this.category = null;
    const list = this.sorted(this.category === null ? inSection : inSection.filter((it) => it.Category === this.category));
    this.renderGrid(list);
    this.renderHero();
    this.renderView();
    this.renderHistory();
  }

  private updateWallet(bump: boolean): void {
    const c = this.inv.credit, g = this.inv.gold;
    if (c !== this.lastCredit) {
      if (bump && this.lastCredit >= 0) restartAnim(this.crWal, "bump");
      this.lastCredit = c;
      setText(this.crEl, fmt(c));
      this.crWal.title = `${fmt(c)} credits`;
    }
    if (g !== this.lastGold) {
      if (bump && this.lastGold >= 0) restartAnim(this.gdWal, "bump");
      this.lastGold = g;
      setText(this.gdEl, fmt(g));
      this.gdWal.title = `${fmt(g)} gold`;
    }
  }

  /** Inventory changed (bag moves too): the wallets and a waiting purchase. Cheap unless money moved. */
  private walletChanged(): void {
    const p = this.pending;
    if (p) {
      const now = p.pType === ShopPayType.Credit ? this.inv.credit : this.inv.gold;
      if (now < p.before) this.finish(true);
    }
    if (this.inv.credit === this.lastCredit && this.inv.gold === this.lastGold) return;
    if (this.open) this.updateWallet(true);
    if (this.viewG !== null) this.invalidate(); // the total against the balance
  }

  private renderNav(base: readonly GameShopItem[]): void {
    const counts = new Map<Section, number>();
    for (const [s] of SECTIONS) counts.set(s, 0);
    const cats = new Map<string, number>();
    for (const it of base) {
      for (const [s] of SECTIONS) if (this.matchesSection(it, s)) counts.set(s, (counts.get(s) ?? 0) + 1);
      // GameShopDialog.GetCategories: the categories of the current section, in arrival order, "" left out
      if (it.Category !== "" && this.matchesSection(it, this.section)) cats.set(it.Category, (cats.get(it.Category) ?? 0) + 1);
    }
    const key = `${this.section}|${this.category ?? ""}|${this.shop.hasNewItems}|${[...counts.values()].join(",")}|${[...cats].join(",")}`;
    if (key === this.navKey) return;
    this.navKey = key;
    const entry = (label: string, icon: IconName | null, n: number, on: boolean, click: () => void) => {
      const b = h("button", "it" + (on ? " on" : ""), icon ? svg(icon) : null, h("span", "", label), h("i", "", fmt(n)));
      b.type = "button";
      b.title = label;
      b.addEventListener("click", click);
      return b;
    };
    const kids: Node[] = [];
    for (const [s, label, icon] of SECTIONS) {
      if (s === "new" && !this.shop.hasNewItems) continue; // GameShopDialog.New.Visible
      kids.push(entry(label, icon, counts.get(s) ?? 0, this.section === s && this.category === null, () => {
        this.section = s; this.category = null; this.page = 0; this.invalidate();
      }));
    }
    if (cats.size) kids.push(h("hr"));
    for (const [c, n] of cats) {
      kids.push(entry(pretty(c), null, n, this.category === c, () => {
        this.category = this.category === c ? null : c; this.page = 0; this.invalidate();
      }));
    }
    this.nav.replaceChildren(...kids);
  }

  private renderGrid(list: readonly GameShopItem[]): void {
    const pages = Math.max(1, Math.ceil(list.length / PAGE));
    this.page = Math.min(Math.max(0, this.page), pages - 1);
    setText(this.pageEl, `${this.page + 1} / ${pages}`);
    setText(this.sumEl, plural(list.length, "item"));
    this.prevBtn.setDisabled(this.page === 0);
    this.nextBtn.setDisabled(this.page >= pages - 1);
    const slice = list.slice(this.page * PAGE, this.page * PAGE + PAGE);
    let key = `${this.section}|${this.category ?? ""}|`;
    for (const it of slice) key += `${it.GIndex}:${it.Stock}:${it.GoldPrice}:${it.CreditPrice}:${it.DealGoldPrice}:${it.DealCreditPrice}:${Number(it.Deal)}${Number(it.TopItem)}:${this.wish.has(it.Info.Index) ? 1 : 0}:${it.Info.Index};`;
    if (key === this.gridKey) return;
    this.gridKey = key;
    this.ui.tooltip.hide();
    if (!slice.length) {
      const text = this.section === "wish" && this.category === null && !this.query
        ? "Tap the heart on anything you like to keep it here."
        : this.shop.items.length ? "Nothing matches." : "The shop has nothing for sale.";
      this.grid.replaceChildren(h("div", "gempty", text));
      return;
    }
    this.grid.replaceChildren(...slice.map((it) => this.card(it)));
  }

  private card(it: GameShopItem): HTMLElement {
    const art = h("i");
    picture(art, it.Info.Image);
    const wished = this.wish.has(it.Info.Index);
    const heart = h("button", "heart" + (wished ? " on" : ""), svg("heartFull"));
    heart.type = "button";
    heart.title = wished ? "Remove from the wishlist" : "Add to the wishlist";
    heart.addEventListener("click", (e) => { e.stopPropagation(); this.toggleWish(it.Info.Index); });
    const artBox = h("div", "art", art, heart);
    if (it.Count > 1) artBox.append(h("span", "cnt", `x${fmt(it.Count)}`));
    const st = stockText(it);
    if (st) artBox.append(h("span", "low", st));
    // the item tooltip (the shop's own ItemInfo)
    artBox.addEventListener("pointerenter", () => this.ui.tooltip.show(art, showItem(it), this.ui.labelContext(), { info: it.Info }));
    artBox.addEventListener("pointerleave", () => this.ui.tooltip.hide());
    const prices = h("div", "prices");
    if (it.CanBuyCredit) prices.append(h("span", "c", ...priceKids(it, ShopPayType.Credit)));
    if (it.CanBuyGold) {
      const was = wasOf(it, ShopPayType.Gold);
      prices.append(h("span", "g", h("span", "coin"), was ? h("s", "", fmtCompact(was)) : null, fmtCompact(it.GoldPrice)));
    }
    if (!it.CanBuyCredit && !it.CanBuyGold) prices.append(h("span", "", "Not for sale"));
    const name = h("b", "", shopName(it));
    name.style.color = gradeVar(it.Info.Grade);
    const card = asButton(h("div", "pc"), shopName(it));
    if (it.Deal) card.append(h("div", "rib", "DEAL"));
    if (it.TopItem) { const s = h("div", "star", svg("star")); s.title = "Top seller"; card.append(s); }
    if (isNewShopItem(it)) card.append(h("div", "newb", "NEW"));
    card.append(artBox, h("div", "meta", name, h("span", "", [it.Category ? pretty(it.Category) : "", it.Class && it.Class !== ALL_CLASSES ? it.Class : ""].filter(Boolean).join(" · ") || " "), prices));
    card.addEventListener("click", () => this.openView(it.GIndex));
    return card;
  }

  private featured(): GameShopItem[] {
    const out: GameShopItem[] = [];
    for (const it of this.shop.items) if ((it.TopItem || it.Deal) && this.matchesBase(it)) out.push(it);
    out.sort((a, b) => Number(b.Deal) - Number(a.Deal) || Number(b.TopItem) - Number(a.TopItem) || itemFriendlyName(a.Info).localeCompare(itemFriendlyName(b.Info)));
    return out.slice(0, FEATURED_MAX);
  }

  private renderHero(): void {
    const items = this.featured();
    this.hero.hidden = items.length === 0;
    if (!items.length) { this.heroKey = ""; return; }
    const at = ((this.slide % items.length) + items.length) % items.length;
    const it = items[at]!;
    const pay = leadPay(it);
    const key = `${at}|${items.map((x) => x.GIndex).join(",")}|${it.Stock}|${priceOf(it, pay)}|${wasOf(it, pay)}`;
    if (key === this.heroKey) return;
    this.heroKey = key;
    const big = h("i");
    picture(big, it.Info.Image);
    const was = wasOf(it, pay), price = priceOf(it, pay);
    const kick = it.Deal ? (was ? `Deal · save ${Math.round((1 - price / was) * 100)} %` : "Deal") : "Top seller";
    const sub = [it.Category ? pretty(it.Category) : "", it.Count > 1 ? `${fmt(it.Count)} in a pack` : "", stockText(it)].filter(Boolean).join(" · ");
    const view = button(it.Deal ? "Grab the deal" : "View", () => this.openView(it.GIndex), { cls: "main" });
    const dots = h("div", "dots");
    items.forEach((_, i) => {
      const d = h("i", i === at ? "on" : "");
      d.title = shopName(items[i]!);
      d.addEventListener("click", () => { this.slide = i; this.heroKey = ""; this.renderHero(); });
      dots.append(d);
    });
    const arrow = (cls: string, icon: IconName, step: number, title: string) => {
      const b = h("button", "arw " + cls, svg(icon));
      b.type = "button";
      b.title = title;
      b.addEventListener("click", () => { this.slide += step; this.heroKey = ""; this.renderHero(); });
      return b;
    };
    const title = h("h2", "", shopName(it));
    title.style.color = gradeVar(it.Info.Grade);
    this.hero.replaceChildren(
      h("div", "slide", h("div", "big", big), h("div", "tx", h("div", "kick", kick), title, h("p", "", sub)),
        h("div", "cta", h("div", "price", ...priceKids(it, pay)), view.el)),
      dots, arrow("l", "chevronLeft", -1, "Previous"), arrow("r", "chevronRight", 1, "Next"));
  }

  // — Product view —

  private openView(gIndex: number): void {
    const it = this.find(gIndex);
    if (!it) return;
    this.viewG = gIndex;
    this.viewBuiltFor = null;
    this.qty = 1;
    this.giftOpen = false;
    this.note = "";
    this.noteBad = false;
    this.pay = leadPay(it);
    this.showHist = false;
    this.hist.hidden = true;
    this.renderView();
  }

  private closeView(render = true): void {
    if (this.viewG === null) return;
    this.viewG = null;
    this.viewBuiltFor = null;
    this.ui.tooltip.hide();
    this.modal.hidden = true;
    this.modal.replaceChildren();
    if (render) this.invalidate();
  }

  private renderView(): void {
    if (this.viewG === null) { this.modal.hidden = true; return; }
    const it = this.find(this.viewG);
    if (!it) { this.closeView(false); this.toast(T.Gone, true); return; }
    if (this.viewBuiltFor !== it) this.buildView(it);
    this.syncView(it);
  }

  /** The product view's DOM for `it` (once per opened product; syncView() updates it). */
  private buildView(it: GameShopItem): void {
    this.viewBuiltFor = it;
    this.friendsKey = "";
    const big = h("i");
    picture(big, it.Info.Image);
    const stage = h("div", "stage", big);
    stage.addEventListener("pointerenter", () => this.ui.tooltip.show(stage, showItem(it), this.ui.labelContext(), { info: it.Info }));
    stage.addEventListener("pointerleave", () => this.ui.tooltip.hide());
    const close = h("button", "close", "×");
    close.type = "button";
    close.title = "Back to the shop (Esc)";
    close.addEventListener("click", () => this.closeView());
    const name = h("h2", "", shopName(it));
    name.style.color = gradeVar(it.Info.Grade);
    const tags = h("div", "tags");
    if (it.Category) tags.append(h("span", "tag", pretty(it.Category)));
    if (it.Class && it.Class !== ALL_CLASSES) tags.append(h("span", "tag", it.Class));
    if (it.Deal) tags.append(h("span", "tag hot", "Deal"));
    if (it.TopItem) tags.append(h("span", "tag", "Top seller"));
    if (isNewShopItem(it)) tags.append(h("span", "tag new", "New"));
    this.pvStock = h("span", "tag hot");
    tags.append(this.pvStock);
    const desc = h("div", "hint", `${it.Count > 1 ? `A pack of ${fmt(it.Count)}. ` : ""}${T.ByMail} Hover the picture for the item's details.`);

    this.pvPay = new Map();
    const pay = h("div", "pay");
    for (const p of [ShopPayType.Credit, ShopPayType.Gold] as Pay[]) {
      const b = h("button", "", ...priceKids(it, p));
      b.type = "button";
      b.title = canPay(it, p) ? (p === ShopPayType.Credit ? "Pay with credits" : "Pay with gold") : (p === ShopPayType.Credit ? "Not sold for credits" : "Not sold for gold");
      b.addEventListener("click", () => { if (!canPay(it, p) || this.pending) return; this.pay = p; this.renderView(); });
      this.pvPay.set(p, b);
      pay.append(b);
    }

    const minus = h("button", "", "−"), plus = h("button", "", "+");
    minus.type = plus.type = "button";
    minus.title = "Fewer (Shift: 10)";
    plus.title = "More (Shift: 10)";
    minus.addEventListener("click", (e) => this.setQty(this.qty - (e.shiftKey ? 10 : 1)));
    plus.addEventListener("click", (e) => this.setQty(this.qty + (e.shiftKey ? 10 : 1)));
    this.pvQty = h("span");
    this.pvTotal = h("b");
    this.pvWas = h("s");
    const row = h("div", "row", "Quantity", h("div", "qty", minus, this.pvQty, plus), h("div", "total", "Total ", this.pvWas, " ", this.pvTotal));

    this.pvGiftSel = h("select", "fld");
    this.pvGiftSel.title = "Your friends (gifts go to mutual friends, online or not)";
    this.pvGiftSel.addEventListener("change", () => { this.giftTo = this.pvGiftSel.value; this.syncView(it); });
    this.pvGiftRow = h("div", "gift", svg("send"), "Gift to", this.pvGiftSel, h("span", "hint", "· by mail"));

    this.pvNote = h("div", "note");
    this.pvBuy = button("Buy", () => void this.purchase(), { cls: "main" });
    this.pvGift = button("Gift", () => {
      this.giftOpen = !this.giftOpen;
      if (this.giftOpen) this.friends.refresh(); // C.RefreshFriends: online states for the picker
      this.syncView(it);
    }, { icon: "send", title: "Buy it for a friend (sent to their mailbox)" });
    this.pvWish = button("", () => { this.toggleWish(it.Info.Index); this.syncView(it); }, { icon: "heartFull" });
    const acts = h("div", "acts", this.pvBuy.el, this.pvGift.el, this.pvWish.el);

    this.modal.replaceChildren(h("div", "pv", close, stage, h("div", "info", name, tags, desc, pay, row, this.pvGiftRow, this.pvNote, acts)));
    this.modal.hidden = false;
  }

  /** The friends a gift can go to (the friend list; friendships are mutual, the server re-checks). Online first. */
  private giftFriends(): { name: string; online: boolean }[] {
    const out: { name: string; online: boolean }[] = [];
    for (const f of this.friends.list) if (!f.Blocked) out.push({ name: f.Name, online: f.Online });
    out.sort((a, b) => Number(b.online) - Number(a.online) || a.name.localeCompare(b.name));
    return out;
  }

  private syncView(it: GameShopItem): void {
    // quantity and currency within the rules
    if (!canPay(it, this.pay)) this.pay = canPay(it, ShopPayType.Credit) ? ShopPayType.Credit : ShopPayType.Gold;
    const max = maxQuantity(it);
    this.qty = Math.min(Math.max(1, this.qty), Math.max(1, max));
    for (const [p, b] of this.pvPay) { b.classList.toggle("on", p === this.pay && canPay(it, p)); b.classList.toggle("off", !canPay(it, p)); }
    setText(this.pvQty, String(this.qty));
    const st = stockText(it);
    setText(this.pvStock, st);
    this.pvStock.hidden = !st;

    const anyPay = it.CanBuyCredit || it.CanBuyGold;
    const price = priceOf(it, this.pay), was = wasOf(it, this.pay);
    const total = price * this.qty, have = this.pay === ShopPayType.Credit ? this.inv.credit : this.inv.gold;
    const cur = this.pay === ShopPayType.Credit ? "credits" : "gold";
    setText(this.pvTotal, anyPay ? `${fmt(total)} ${cur}` : "-");
    this.pvTotal.classList.toggle("bad", anyPay && total > have);
    setText(this.pvWas, was ? fmt(was * this.qty) : "");
    this.pvWas.hidden = !was;

    // gift picker
    const friends = this.giftFriends();
    const fkey = friends.map((f) => `${f.name}:${Number(f.online)}`).join(",");
    if (fkey !== this.friendsKey) {
      this.friendsKey = fkey;
      this.pvGiftSel.replaceChildren(...friends.map((f) => Object.assign(h("option", "", f.online ? `${f.name} (online)` : f.name), { value: f.name })));
    }
    if (!friends.some((f) => f.name === this.giftTo)) this.giftTo = friends[0]?.name ?? "";
    this.pvGiftSel.value = this.giftTo;
    if (!friends.length) this.giftOpen = false;
    this.pvGiftRow.hidden = !this.giftOpen;

    // buttons and the reason they are off
    const why = this.pending ? T.Waiting
      : !anyPay ? "This item is not for sale."
      : max === 0 ? T.TooBig
      : total > have ? T.CantAfford
      : this.giftOpen && !this.giftTo ? T.NoFriends
      : "";
    const buy = this.pvBuy!, gift = this.pvGift!, wish = this.pvWish!;
    buy.setLabel(this.pending ? T.Waiting : total > have && anyPay ? "Not enough" : this.giftOpen ? `Send gift to ${this.giftTo}` : "Buy");
    buy.setDisabled(why !== "", why);
    gift.setLabel(this.giftOpen ? "Buy for me" : "Gift");
    gift.setDisabled(!!this.pending || (!this.giftOpen && friends.length === 0), this.pending ? T.Waiting : T.NoFriends);
    wish.setLabel(this.wish.has(it.Info.Index) ? "Wishlisted" : "Wishlist");

    const note = this.note || (max === 0 ? T.TooBig : max < MAX_GIFT_QUANTITY && this.qty === max ? `At most ${fmt(max)} at a time.` : "");
    setText(this.pvNote, note);
    this.pvNote.classList.toggle("bad", this.noteBad || max === 0);
  }

  private setQty(q: number): void {
    if (this.pending || this.viewG === null) return;
    this.qty = q;
    this.note = "";
    this.noteBad = false;
    this.renderView();
  }

  // — Purchase (server authoritative) —

  /** GameShopCell.BuyProduct: the desktop's confirm, then C.GameshopBuy (or C.GameshopGift to the picked friend). */
  private async purchase(): Promise<void> {
    if (this.pending || this.viewG === null) return;
    const gIndex = this.viewG;
    let it = this.find(gIndex);
    if (!it) return;
    const pay = this.pay, qty = this.qty, to = this.giftOpen ? this.giftTo : "";
    if (!canPay(it, pay) || qty < 1 || qty > maxQuantity(it)) return;
    const cost = priceOf(it, pay) * qty;
    if (cost > (pay === ShopPayType.Credit ? this.inv.credit : this.inv.gold)) { this.setNote(T.CantAfford, true); return; } // the button is off; never trust it
    if (this.giftOpen && !to) { this.setNote(T.NoFriends, true); return; }
    const name = shopName(it), cur = pay === ShopPayType.Credit ? "Credits" : "Gold";
    // ConfirmBuyItemCredits / ConfirmPurchaseItemGold (desktop wording) + where it goes
    const text = to
      ? `Are you sure would you like to send ${qty} x \n${name}(${it.Count}) to ${to} for ${fmt(cost)} ${cur}?\nThe gift is delivered to their mailbox.`
      : `Are you sure would you like to buy ${qty} x \n${name}(${it.Count}) for ${fmt(cost)} ${cur}?\nIt will be delivered by mail.`;
    const ok = await this.ui.rt.popups.ask(text, { title: to ? "Send a gift" : "Game shop", item: { name, img: it.Info.Image, count: it.Count * qty } });
    if (!ok || this.pending || this.signal.aborted) return;
    // the shop may have changed while the box was up
    it = this.find(gIndex);
    if (!it || !canPay(it, pay) || qty > maxQuantity(it)) { this.setNote(T.Gone, true); return; }
    const cost2 = priceOf(it, pay) * qty;
    const before = pay === ShopPayType.Credit ? this.inv.credit : this.inv.gold;
    if (cost2 > before) { this.setNote(T.CantAfford, true); return; }
    if (to) {
      if (!this.shop.gift(it.GIndex, qty, pay, to)) { this.setNote(T.NoFriends, true); return; }
    } else {
      this.shop.buy(it.GIndex, qty, pay);
    }
    const cancel = later(() => this.finish(false), ANSWER_MS, this.signal);
    this.pending = { gIndex, name, img: it.Info.Image, count: it.Count, qty, cost: cost2, pType: pay, to, before, cancel };
    this.note = "";
    this.noteBad = false;
    this.renderView();
  }

  /** The server charged (true) or nothing came within ANSWER_MS (false: refused with a chat line, or a free item). */
  private finish(charged: boolean): void {
    const p = this.pending;
    if (!p) return;
    this.pending = null;
    p.cancel();
    const paid = `${fmt(p.cost)} ${p.pType === ShopPayType.Credit ? "credits" : "gold"}`;
    if (charged || p.cost === 0) {
      this.history.unshift({ img: p.img, name: p.name, qty: p.qty, count: p.count, paid, to: p.to, at: Date.now() });
      if (this.history.length > HISTORY_MAX) this.history.length = HISTORY_MAX;
      this.histBtn.setLabel(`Purchases (${this.history.length})`);
      const what = `${p.qty} x ${p.name}`;
      const msg = p.cost === 0 && !charged
        ? `Ordered ${what}: check your mail and the chat.`
        : p.to ? `Sent ${what} to ${p.to}: it is in their mailbox.` : `Bought ${what}: it arrives by mail.`;
      this.toast(msg, false);
      if (charged) this.celebrate();
      this.qty = 1;
      this.note = msg;
      this.noteBad = false;
    } else {
      this.note = T.NoAnswer;
      this.noteBad = true;
      this.toast(T.NoAnswer, true);
    }
    if (this.showHist) this.renderHistory();
    this.invalidate();
  }

  private setNote(text: string, bad: boolean): void {
    this.note = text;
    this.noteBad = bad;
    this.renderView();
  }

  // — Wishlist / purchase list / toast —

  private toggleWish(itemIndex: number): void {
    if (this.wish.has(itemIndex)) this.wish.delete(itemIndex); else this.wish.add(itemIndex);
    try { localStorage.setItem(WISH_STORE, JSON.stringify([...this.wish])); } catch { /* storage blocked: kept for this session only */ }
    this.invalidate();
  }

  private renderHistory(): void {
    this.hist.hidden = !this.showHist;
    if (!this.showHist) return;
    const rows: Node[] = [h("div", "lbl", "Bought this session")];
    if (!this.history.length) rows.push(h("div", "hr", h("small", "", "Nothing bought yet. Purchases arrive by mail.")));
    for (const x of this.history) {
      const pic = h("i");
      picture(pic, x.img);
      rows.push(h("div", "hr", pic, h("span", "", `${x.qty} x ${x.name}${x.count > 1 ? ` (${fmt(x.count)})` : ""}`, h("br"), h("small", "", x.to ? `Gift to ${x.to}` : "For you")),
        h("small", "", `${x.paid} · ${fmtTimeOfDay(x.at)}`)));
    }
    this.hist.replaceChildren(...rows);
  }

  private toast(text: string, bad: boolean): void {
    setText(this.toastEl, text);
    this.toastEl.classList.toggle("bad", bad);
    this.toastEl.hidden = false;
    restartAnim(this.toastEl, "show");
    this.stopToast?.();
    this.stopToast = later(() => { this.toastEl.hidden = true; this.toastEl.classList.remove("show"); }, 3500, this.signal);
  }

  /** A confetti burst for a charged purchase (none under prefers-reduced-motion). */
  private celebrate(): void {
    if (reducedMotion() || !this.open) return;
    const w = this.panel.clientWidth, hgt = this.panel.clientHeight;
    const colours = ["var(--ac)", "#fff", "var(--gold)", "#ff6a8a", "var(--ok)"];
    const bits: HTMLElement[] = [];
    for (let i = 0; i < 40; i++) {
      const c = h("div", "conf-p");
      const ang = Math.random() * Math.PI * 2, dist = 120 + Math.random() * 240;
      c.style.left = w / 2 + "px";
      c.style.top = hgt / 2 + "px";
      c.style.background = colours[i % colours.length]!;
      c.style.animationDelay = Math.random() * 0.15 + "s";
      c.style.setProperty("--dx", Math.cos(ang) * dist + "px");
      c.style.setProperty("--dy", Math.sin(ang) * dist + 120 + "px");
      c.style.setProperty("--rot", Math.random() * 720 - 360 + "deg");
      bits.push(c);
    }
    this.panel.append(...bits);
    later(() => { for (const c of bits) c.remove(); }, 1700, this.signal);
  }
}

registerWindow(GAME_SHOP_WINDOW, (host) => new GameShopWindow(host));
