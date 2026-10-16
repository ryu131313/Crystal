// Ported from play/hudNpcBox.ts (NpcBoxMock): the new UI's NPC item box, a modern NPCDropDialog (Client/MirScenes/
// Dialogs/NPCDialogs.cs) for the NPC services that take ONE item from your bag: Disassemble, Downgrade (awakening),
// Reset (added stats), Replace wedding ring and Consign (integrator: Check refine moved to refine.ts, which shows the
// odds; Consign added with a price popup, as the classic drop panel). The classic port is src/ui/npcWindow.ts NpcDropDialog
// (opened by GameScene.NPCDisassemble / NPCDowngrade / NPCReset / NPCReplaceWedRing / NPCConsign); the state lives
// in the shared NPCDropDialog model (src/game/npcSession.ts NpcDrop: type, rate, the target and its bag lock).
//
// Changes from the mock:
//  - it opens ONLY from the server's answer (NpcDrop.shown with one of the box types: the server gates every service on
//    the NPC page key) and closes with the NPC session (NpcDrop.hidden: NPCDialog.Hide, a new page, another panel);
//    there are no service chips: the window shows the one service the NPC page opened (switching would need a page the
//    NPC may not have). Opened by hand without a session it says "Speak to ...";
//  - the altar is the real NPCDropDialog cell (core/itemCells.ts grid "npcDrop"): drag a bag item onto it (core/drag.ts
//    routes it to NpcDrop.take, which locks the bag cell), click a bag item (here or in the bag window) to put it there,
//    right-click the altar to take it back. Nothing leaves the bag; ineligible items are dimmed with the reason (the
//    classic / server checks: grade and not an awakening item, awakened, IsAdded and not unidentified, not rented,
//    rings that may be wedding rings);
//  - prices are the game's (items.ts disassemblePrice / downgradePrice / resetPrice; the ring = RequiredAmount x 10 x
//    the rate of S.NPCReplaceWedRing = Settings.ReplaceWedRingCost); gold is only read from Inventory;
//  - Replace wedding ring needs a wedding ring worn on the left hand (PlayerObject.ReplaceWeddingRing: RingL with
//    WeddingRing != -1), shown with the partner's name from the Relationship model;
//  - Disassemble, Downgrade and Reset are hold-to-confirm (destructive); the ring and Consign are a click;
//  - NO local outcomes: the request goes through NpcDrop.confirm, and the result is read from the server's packets
//    (S.GainedItem / S.DeleteItem for a disassembly, S.RefreshItem for downgrade / reset, S.EquipItem for the ring,
//    S.ConsignItem for a consignment); the server's chat line says the rest. While
//    the answer is due the confirm stays off (a few seconds at most: refusals only come as chat).
// HIDDEN (no client data): the mock's loot glyph preview (the server rolls from Envir.AwakeningDrops, which the client
// never gets: the window says what kind of material and the most it can give), the spark / shatter effects tied to
// local rolls, and repair / special repair (the merchant window's).
// Narrow layout (below a 1040 px layer): the bag strip shows 8 cells a row.

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { BindMode, EquipmentSlot, ItemGrade, ItemType, ItemTypeName, PanelType, StatName } from "../../../protocol/enums.generated.ts";
import { BELT_IDX } from "../../../game/inventory.ts";
import { addedCount, awakeLevel, disassemblePrice, downgradePrice, refineCost, resetPrice } from "../../../game/items.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import type { DropType } from "../../../game/npcSession.ts";
import { CONSIGNMENT_COST, MAX_CONSIGNMENT, MIN_CONSIGNMENT } from "../../../game/market.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, gradeName, h, holdButton, injectCss, later, npcName, plural, pretty, restartAnim, setText, svg,
  type Btn,
} from "../core/index.ts";
import { gradeVar } from "../core/styles.ts";
import type { CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const NPC_BOX_KEY = "npcBox";

/** The NPCDropDialog types this window serves (Sell / Repair / SpecialRepair: merchant.ts; Refine / CheckRefine: refine.ts,
 *  which shows the odds; Consign ([@CONSIGN], S.NPCConsign): here, as the classic drop panel, with a price popup). */
export const NPC_BOX_TYPES: ReadonlySet<number> = new Set<number>([
  PanelType.Disassemble, PanelType.Downgrade, PanelType.Reset, PanelType.ReplaceWedRing, PanelType.Consign,
]);

/** How long the confirm waits for the server's answer before it gives up (refusals only come as chat lines). */
const ANSWER_MS = 4000;

interface Service {
  /** Title, confirm label, icon, blurb. */
  readonly title: string;
  readonly verb: string;
  readonly icon: string;
  readonly blurb: string;
  /** Hold-to-confirm (destructive). */
  readonly hold: boolean;
}
/** Per service (icons: constant SVG path data, core/icons.ts svg()). */
const SVC: Readonly<Record<number, Service>> = {
  [PanelType.Disassemble]: { title: "Disassemble", verb: "Disassemble", icon: "M4 20l7-7M13 11l7-7M9 4l3 3-2 2-3-3zM15 14l3 3-2 2-3-3z",
    blurb: "Breaks a graded item down into awakening materials. The item is destroyed.", hold: true },
  [PanelType.Downgrade]: { title: "Downgrade awakening", verb: "Downgrade", icon: "M12 4v12M6 10l6 6 6-6M5 20h14",
    blurb: "Removes the last awakening level from an item.", hold: true },
  [PanelType.Reset]: { title: "Reset added stats", verb: "Reset", icon: "M4 12a8 8 0 1 0 3-6.2M4 4v4h4",
    blurb: "Wipes an item's added stats back to its base stats.", hold: true },
  [PanelType.ReplaceWedRing]: { title: "Replace wedding ring", verb: "Replace ring", icon: "M12 9a6 6 0 110 12 6 6 0 010-12zM9 3h6l-3 6z",
    blurb: "Moves your marriage bond onto another ring, which you then wear on your left hand.", hold: false },
  [PanelType.Consign]: { title: "Consign", verb: "Consign", icon: "M4 7h16v13H4zM8 7V4h8v3M9 12h6",
    blurb: `Lists an item at the trust merchant for a price you set (${fmt(MIN_CONSIGNMENT)} to ${fmt(MAX_CONSIGNMENT)} gold).`, hold: false },
};

/** ClientTextKeys / server lines the window repeats (English.json). */
const T = {
  ItemWillBeDestroyed: "The item will be destroyed.",
  NotWearingWeddingRing: "You need to wear your wedding ring on your left hand.",
};

/** NPCDropDialog.ItemCell_Click's / the server's checks for `type`: "" = it takes the item, else why not. */
function npcBoxRefusal(type: number, item: UserItem, info: ItemInfo | undefined): string {
  if (!info) return "Unknown item";
  switch (type) {
    case PanelType.Disassemble:
      if (info.Grade === ItemGrade.None || info.Type === ItemType.Awakening) return "Only graded items (Common or better)";
      if (info.Bind & BindMode.UnableToDisassemble) return "Can't be disassembled";
      if (item.Rental && (item.Rental.BindingFlags & BindMode.UnableToDisassemble)) return `Rented from ${item.Rental.OwnerName}`;
      return "";
    case PanelType.Downgrade:
      if (awakeLevel(item) === 0) return "Not awakened";
      if (item.Rental) return `Rented from ${item.Rental.OwnerName}`;
      return "";
    case PanelType.Reset:
      if (!(addedCount(item) > 0 || item.Slots.length > info.Slots)) return "No added stats to reset"; // UserItem.IsAdded
      if (info.NeedIdentify) return "Unidentified items can't be reset";
      if (item.Rental) return `Rented from ${item.Rental.OwnerName}`;
      return "";
    case PanelType.ReplaceWedRing:
      if (info.Type !== ItemType.Ring) return "Rings only";
      if (info.Bind & BindMode.NoWeddingRing) return "This ring can't be a wedding ring";
      if (info.Durability === 0) return "Can't be used here";
      return "";
    case PanelType.Consign: // NPCDropDialog.Confirm / PlayerObject.ConsignItem
      if ((info.Bind & BindMode.DontStore) || (info.Bind & BindMode.DontSell)) return "Can't be consigned";
      return "";
  }
  return "Not for this service";
}

/** The service's price for `item` (null = none). */
function npcBoxPrice(type: number, item: UserItem, info: ItemInfo | undefined, rate: number): number | null {
  switch (type) {
    case PanelType.Disassemble: return disassemblePrice(item, info);
    case PanelType.Downgrade: return downgradePrice(item, info);
    case PanelType.Reset: return resetPrice(item, info);
    case PanelType.ReplaceWedRing: return refineCost(info, rate); // RequiredAmount x 10 x ReplaceWedRingCost
    case PanelType.Consign: return CONSIGNMENT_COST; // Globals.ConsignmentCost, taken by the server on listing
  }
  return null;
}

const CSS = `
.hudm .nbx { width: 640px; }
.hudm .nbx .panel { display: flex; flex-direction: column; }
.hudm .nbx .blurb { display: flex; align-items: center; gap: 8px; padding: 10px 16px 0; font-size: 11.5px; color: var(--dim); }
.hudm .nbx .blurb svg { width: 16px; height: 16px; flex: none; fill: none; stroke: var(--ac); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .nbx .blurb b { color: var(--ac); }
.hudm .nbx .main { display: flex; gap: 14px; padding: 12px 14px; }
.hudm .nbx .altar { position: relative; width: 220px; height: 220px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: var(--rad);
  background: radial-gradient(circle at 50% 55%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 62%), radial-gradient(ellipse 70% 20% at 50% 88%, rgba(0,0,0,.6), transparent); }
.hudm .nbx .ring { position: absolute; width: 150px; height: 150px; border-radius: 50%; border: 1px dashed color-mix(in srgb, var(--ac) 45%, transparent); will-change: transform; animation: hudm-nbxspin 18s linear infinite; }
.hudm .nbx .ring.b { width: 180px; height: 180px; border-style: solid; border-color: color-mix(in srgb, var(--fr) 70%, transparent); animation-duration: 40s; animation-direction: reverse; }
@keyframes hudm-nbxspin { to { transform: rotate(360deg); } }
.hudm .nbx .altar.danger .ring { border-color: color-mix(in srgb, var(--bad) 60%, transparent); }
.hudm .nbx .slot { position: relative; z-index: 1; width: 78px; height: 78px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(circle at 50% 35%, var(--pc), #05070b 75%); border: 2px solid var(--fr); box-shadow: 0 0 0 1px #000, inset 0 0 14px #000; }
.hudm .nbx .slot .icell { border-radius: 50%; border: 0; background: transparent; box-shadow: none; }
.hudm .nbx .slot .icell::after { display: none; }
.hudm .nbx .slot .icell > i { transform: scale(1.4); }
.hudm .nbx .altar.full .slot { border-color: var(--gc, var(--ac)); }
.hudm .nbx .glow { position: absolute; z-index: 0; width: 120px; height: 120px; border-radius: 50%; pointer-events: none; opacity: 0;
  background: radial-gradient(circle, color-mix(in srgb, var(--gc, var(--ac)) 45%, transparent), transparent 70%); transition: opacity .3s; }
.hudm .nbx .altar.full .glow { opacity: 1; }
.hudm .nbx .altar .nm { position: absolute; bottom: 6px; left: 4px; right: 4px; text-align: center; font-size: 13px; font-weight: 700; color: var(--gc, #fff); text-shadow: 0 0 8px #000; }
.hudm .nbx .altar .nm small { display: block; font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .nbx .altar.work .slot { animation: hudm-nbxwork .8s ease-in-out; }
@keyframes hudm-nbxwork { 30% { transform: scale(1.1); } 60% { transform: scale(.95); } }
.hudm .nbx .res { position: absolute; left: 50%; top: 10px; translate: -50% 0; z-index: 3; max-width: 210px; white-space: normal; text-align: center; padding: 4px 12px; border-radius: 12px;
  font-size: 11.5px; font-weight: 700; color: #fff; background: rgba(8,12,20,.9); border: 1px solid var(--ac); }
.hudm .nbx .res.show { animation: hudm-nbxres 3.2s ease-out forwards; }
@keyframes hudm-nbxres { 0% { opacity: 0; transform: translateY(6px); } 10%, 85% { opacity: 1; transform: none; } 100% { opacity: 0; } }
.hudm .nbx .prev { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; }
.hudm .nbx .card { flex: 1; border-radius: var(--rad); padding: 10px 12px; background: rgba(0,0,0,.22); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); font-size: 12px; }
.hudm .nbx .card h4 { margin: 0 0 8px; font-size: 10px; letter-spacing: .16em; color: var(--ac); }
.hudm .nbx .row { display: flex; align-items: center; gap: 8px; min-height: 22px; }
.hudm .nbx .row > span:first-child { width: 96px; color: var(--dim); flex: none; }
.hudm .nbx .row b { color: #fff; font-variant-numeric: tabular-nums; } .hudm .nbx .row .ar { color: var(--ac); }
.hudm .nbx .row s { color: var(--dim); }
.hudm .nbx .stars { color: var(--ac); letter-spacing: 2px; } .hudm .nbx .stars s { color: var(--bad-soft); }
.hudm .nbx .warn { padding: 6px 10px; border-radius: var(--rad); font-size: 11px; color: #ffc8c0; background: rgba(120,20,20,.35); border: 1px solid #c44; margin-bottom: 6px; }
.hudm .nbx .hint { color: var(--dim); font-size: 11.5px; line-height: 1.5; white-space: pre-line; }
.hudm .nbx .pay { display: flex; align-items: center; gap: 10px; }
.hudm .nbx .cost { font-size: 15px; font-weight: 800; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .nbx .cost.poor { color: var(--bad-soft); }
.hudm .nbx .cost small { font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .nbx .pay .btn { margin-left: auto; min-width: 140px; height: 32px; }
.hudm .nbx .note { min-height: 14px; font-size: 11px; color: var(--warn); }
.hudm .nbx .bagl { display: flex; justify-content: space-between; padding: 0 16px; }
.hudm .nbx .bagl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .nbx .bagg { display: grid; grid-template-columns: repeat(12, 44px); gap: 4px; padding: 6px 14px 14px; max-height: 200px; overflow-y: auto; scrollbar-width: thin; }
.hudm .nbx .bagg .icell.no { opacity: .28; filter: grayscale(1); }
.hudm .nbx .bagg .icell.yes { border-color: color-mix(in srgb, var(--ac) 60%, var(--fr)); }
.hudm .nbx .closed { padding: 40px 20px; text-align: center; color: var(--dim); }
.hudm .nbx .closed b { display: block; color: #fff; font-size: 14px; margin-bottom: 6px; }
.hudm.narrow .nbx { width: 600px; }
.hudm.narrow .nbx .altar { width: 190px; height: 190px; }
.hudm.narrow .nbx .bagg { grid-template-columns: repeat(11, 44px); }
@media (prefers-reduced-motion: reduce) { .hudm .nbx .ring, .hudm .nbx .altar.work .slot, .hudm .nbx .res.show { animation: none; } }
`;

/** What the window waits for after a confirm: the item, the service and what has arrived so far. */
interface Pending { readonly id: bigint; readonly type: number; readonly name: string; gained: string }

class NpcBoxWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private pending: Pending | null = null;
  private stopWait: (() => void) | null = null;
  private stopBanner: (() => void) | null = null;
  private note = "";
  private bagCells: ItemCell[] = [];
  private shownType = -1;
  private previewKey = "";

  // — DOM, built once —
  /** The title bar's text node (titleBar() puts the text first, then the close cross). */
  private titleText: Node | null = null;
  private readonly closedView = h("div", "closed", h("b", "", "The NPC is not offering a service"), "Speak to a blacksmith or another NPC that offers it.");
  private readonly body = h("div", "");
  private readonly blurbIcon = h("span");
  private readonly blurbNpc = h("b");
  private readonly blurbText = h("span");
  private readonly altar = h("div", "altar");
  private readonly altarLabel = h("span");
  private readonly altarType = h("small");
  private readonly altarName = h("div", "nm", this.altarLabel, this.altarType);
  private readonly res = h("div", "res");
  private readonly card = h("div", "card");
  private readonly cost = h("div", "cost");
  private readonly noteEl = h("div", "note");
  private readonly payRow = h("div", "pay");
  private go: Btn | null = null;
  private goKey = "";
  private readonly bagCount = h("b");
  private readonly bagGrid = h("div", "bagg");
  private readonly altarCell: ItemCell;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "nbx", 200, 70);
    injectCss("win-npcbox", CSS);
    this.m = ui.ctx.models;
    const sig = ui.rt.signal;
    const drop = this.m.npcSession.drop;

    // The altar: the NPCDropDialog cell (drop a bag item on it; right-click takes it back).
    this.altarCell = ui.cells.cell("npcDrop", 0, { size: 76, filter: (item, info) => this.refusal(item, info) });
    this.altarCell.el.title = "Drag an item here, or click one in your bag · right-click to take it back";
    this.altar.append(h("div", "ring b"), h("div", "ring"), h("div", "glow"), h("div", "slot", this.altarCell.el), this.altarName, this.res);
    this.res.hidden = true;

    const head = h("div", "blurb", this.blurbIcon, h("span", "", this.blurbNpc, " · ", this.blurbText));
    this.payRow.append(this.cost);
    const prev = h("div", "prev", this.card, this.noteEl, this.payRow);
    this.body.append(head, h("div", "main", this.altar, prev),
      h("div", "lbl bagl", h("span", "", "Your bag"), this.bagCount), this.bagGrid);
    this.el.append(h("div", "panel", this.titleBarWith(), this.closedView, this.body));

    // A click on a bag item (this strip or the bag window) puts it on the altar (NPCDropDialog's bag-click target).
    const offClick = ui.drag.addClickHook((ref, item) => {
      if (!this.open || !drop.open || !NPC_BOX_TYPES.has(drop.type) || ref.grid !== "inv") return false;
      this.putOnAltar(ref, item);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    // Model events: re-render while shown, else on the next open (dirty-when-closed).
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.m.inventory.subscribe(() => this.invalidate()));
    on(drop.changed.on(() => this.invalidate()));
    on(drop.shown.on(() => { this.note = ""; this.invalidate(); }));
    // Confirm on the Consign panel: the price box (MirAmountBox ConsignmentPrice), then NpcDrop.consign (C.ConsignItem;
    // the cell stays locked until S.ConsignItem).
    on(drop.consignPrice.on(({ item }) => this.askConsignPrice(item)));
    on(this.m.relationship.changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.previewKey = ""; this.invalidate(); } }));
    on(ui.packets.on((p) => this.answer(p)));
  }

  private titleBarWith(): HTMLElement {
    const t = this.titleBar("NPC SERVICE", "Close (the item stays in your bag)");
    this.titleText = t.firstChild;
    return t;
  }

  private setTitle(text: string): void {
    if (this.titleText && this.titleText.nodeValue !== text) this.titleText.nodeValue = text;
  }

  protected override shown(): void { this.note = ""; }

  /** Every close path (cross, Esc, Close all, the NPC session ending): NPCDropDialog.Hide unlocks the target. */
  protected override closed(): void {
    this.endWait();
    this.stopBanner?.();
    this.stopBanner = null;
    this.res.hidden = true;
    const drop = this.m.npcSession.drop;
    if (drop.open && NPC_BOX_TYPES.has(drop.type)) drop.hide();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const drop = this.m.npcSession.drop;
    const live = drop.open && NPC_BOX_TYPES.has(drop.type);
    this.closedView.hidden = live;
    this.body.hidden = !live;
    if (!live) { this.setTitle("NPC SERVICE"); this.shownType = -1; return; }
    const type = drop.type, svc = SVC[type]!;
    if (this.shownType !== type) {
      this.shownType = type;
      this.setTitle(svc.title.toUpperCase());
      this.blurbIcon.replaceChildren(svg(svc.icon, ""));
      setText(this.blurbText, svc.blurb);
      this.altar.classList.toggle("danger", type === PanelType.Disassemble);
      this.goKey = "";
    }
    setText(this.blurbNpc, npcName(this.m.npcSession.npcName)[0] || "NPC");

    const item = drop.currentItem();
    const info = item ? this.m.itemInfos.get(item.ItemIndex) : undefined;
    this.altar.classList.toggle("full", !!item);
    if (info) this.altar.style.setProperty("--gc", gradeVar(info.Grade)); else this.altar.style.removeProperty("--gc");
    setText(this.altarLabel, item ? this.nameOf(item, info) : "");
    setText(this.altarType, info ? [gradeName(info.Grade), pretty(ItemTypeName[info.Type] ?? "")].filter(Boolean).join(" ") : "");
    this.altarName.hidden = !item;

    this.renderPreview(type, item, info);
    this.renderPay(type, svc, item, info);
    setText(this.noteEl, this.note);
    this.renderBag(type);
  }

  /** WHAT HAPPENS: built from the item's real fields (no rolls). */
  private renderPreview(type: DropType, item: UserItem | null, info: ItemInfo | undefined): void {
    const worn = type === PanelType.ReplaceWedRing ? this.wornWeddingRing() : null;
    const key = item ? [type, item.UniqueID, item.ItemIndex, item.AwakeLevels.length, JSON.stringify(item.AddedStats), item.Slots.length,
      item.RefineAdded, item.RefineSuccessChance, !!info, worn?.UniqueID ?? "", this.m.relationship.loverName, this.m.npcSession.drop.rate].join("|")
      : `${type}|${this.pending?.name ?? ""}|${worn?.UniqueID ?? ""}`;
    if (key === this.previewKey) return;
    this.previewKey = key;
    const card = this.card;
    const kids: (Node | string)[] = [h("h4", "", "WHAT HAPPENS")];
    const row = (label: string, ...rest: (Node | string)[]) => h("div", "row", h("span", "", label), ...rest);
    if (type === PanelType.ReplaceWedRing && !worn) kids.push(h("div", "warn", T.NotWearingWeddingRing));
    if (!item || !info) {
      kids.push(h("div", "hint", this.pending ? `Waiting for the server (${this.pending.name})…`
        : "Put an item on the altar to see the result and the price before you pay.\nItems this service can't take are dimmed in your bag (hover them for why)."));
      card.replaceChildren(...kids);
      return;
    }
    switch (type) {
      case PanelType.Disassemble: {
        // PlayerObject.DisassembleItem: Random.Next(Grade x RequiredAmount / 10 + Quality), at least 1
        const quality = addedCount(item) + awakeLevel(item) + 1;
        const most = Math.max(1, Math.trunc(info.Grade * info.RequiredAmount / 10) + quality - 1);
        kids.push(h("div", "warn", `${this.nameOf(item, info)}: ${T.ItemWillBeDestroyed}`),
          row("You get", h("b", "", `1 awakening material, ${most > 1 ? `1 to ${fmt(most)}` : "x1"}`)),
          row("Grade", h("b", "", gradeName(info.Grade) || "None"), h("span", "", " (sometimes one grade off)")));
        break;
      }
      case PanelType.Downgrade: {
        const lv = awakeLevel(item);
        kids.push(row("Awakening", h("span", "stars", "★".repeat(Math.max(0, lv - 1)), h("s", "", "★"))),
          row("Level", h("b", "", String(lv)), h("span", "ar", "→"), h("b", "", String(lv - 1))),
          row("Risk", h("span", "", "1 in 20: −1 max durability")));
        break;
      }
      case PanelType.Reset: {
        const added = Object.entries(item.AddedStats).filter(([, v]) => v);
        for (const [k, v] of added) kids.push(row(pretty(StatName[Number(k)] ?? `Stat ${k}`), h("s", "", `+${fmt(v)}`), h("span", "ar", "→"), h("b", "", "+0")));
        if (item.Slots.length > info.Slots) kids.push(row("Sockets", h("b", "", `${item.Slots.length} kept`)));
        kids.push(row("Risk", h("span", "", "1 in 20: −1 max durability")),
          h("div", "hint", "The item keeps its base stats, sockets, awakening and seal."));
        break;
      }
      case PanelType.ReplaceWedRing: {
        const lover = this.m.relationship.loverName;
        const wornInfo = worn ? this.m.itemInfos.get(worn.ItemIndex) : undefined;
        kids.push(row("Bond", h("b", "", lover || "your partner"), h("span", "ar", "→"), h("b", "", this.nameOf(item, info))));
        if (worn) kids.push(row("Old ring", h("b", "", this.nameOf(worn, wornInfo)), h("span", "", " goes to your bag")));
        kids.push(h("div", "hint", `The new ring is worn on your left hand. Price: the new ring's level (${info.RequiredAmount}) x 10 x ${fmt(this.m.npcSession.drop.rate)} gold.`));
        break;
      }
      case PanelType.Consign:
        kids.push(row("Listing fee", h("b", "", `${fmt(CONSIGNMENT_COST)} gold`)),
          h("div", "hint", "You set the price next. Sold items and unsold ones are collected at a trust merchant."));
        break;
    }
    card.replaceChildren(...kids);
  }

  /** The price against the gold you have, and the confirm (hold for destructive services). */
  private renderPay(type: DropType, svc: Service, item: UserItem | null, info: ItemInfo | undefined): void {
    const drop = this.m.npcSession.drop, gold = this.m.inventory.gold;
    const price = item ? npcBoxPrice(type, item, info, drop.rate) : null;
    const poor = price !== null && price > gold;
    if (price === null) setText(this.cost, item ? "Free" : "—");
    else { setText(this.cost, `${fmt(price)} `); this.cost.append(h("small", "", `/ ${fmt(gold)} gold`)); }
    this.cost.classList.toggle("poor", poor);
    const key = `${type}`;
    if (this.goKey !== key || !this.go) {
      this.goKey = key;
      this.go?.el.remove();
      this.go = svc.hold
        ? holdButton(`Hold to ${svc.verb.toLowerCase()}`, () => this.confirm(), { cls: "main", danger: true, ms: 1100 })
        : button(svc.verb, () => this.confirm(), { cls: "main" });
      this.payRow.append(this.go.el);
    }
    let why = "";
    if (this.pending) why = "Waiting for the server";
    else if (!item) why = "Put an item on the altar first";
    else if (poor) why = "Not enough gold";
    else if (type === PanelType.ReplaceWedRing && !this.wornWeddingRing()) why = T.NotWearingWeddingRing;
    this.go.setDisabled(!!why, why);
  }

  /** The bag strip: every bag cell, dimmed with the reason when this service can't take it. */
  private renderBag(type: number): void {
    const inv = this.m.inventory.inventory;
    const n = Math.max(0, inv.length - BELT_IDX);
    if (this.bagCells.length !== n) {
      for (const c of this.bagCells) c.dispose();
      this.bagCells = [];
      for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 44 }));
      this.bagGrid.replaceChildren(...this.bagCells.map((c) => c.el));
    }
    let fit = 0, used = 0;
    for (const c of this.bagCells) {
      const item = inv[c.ref.index];
      let why = "";
      if (item) {
        used++;
        why = npcBoxRefusal(type, item, this.m.itemInfos.get(item.ItemIndex));
        if (!why) fit++;
      }
      c.el.classList.toggle("no", !!item && !!why);
      c.el.classList.toggle("yes", !!item && !why);
      if (c.el.title !== why) c.el.title = why;
    }
    setText(this.bagCount, `${fit} ${plural(fit, "item")} this service takes · ${used}/${n}`);
  }

  // — Actions —

  private refusal(item: UserItem, info: ItemInfo | undefined): string {
    const drop = this.m.npcSession.drop;
    if (this.pending) return "Waiting for the server";
    return NPC_BOX_TYPES.has(drop.type) ? npcBoxRefusal(drop.type, item, info) : "";
  }

  /** A bag click: NpcDrop.take (locks the bag cell) when the service takes it, else the reason. */
  private putOnAltar(ref: CellRef, item: UserItem): void {
    const why = this.refusal(item, this.m.itemInfos.get(item.ItemIndex));
    if (why) { this.ui.drag.toast(why); return; }
    if (this.m.inventory.isLocked(item)) { this.ui.drag.toast("Waiting for the server"); return; }
    this.m.npcSession.drop.take(ref.index);
  }

  /** NPCDropDialog.Confirm: the model sends the request; the result comes from the server's packets (answer()). */
  private confirm(): void {
    const drop = this.m.npcSession.drop, item = drop.currentItem();
    if (!item || this.pending || !drop.open || !NPC_BOX_TYPES.has(drop.type)) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    const why = npcBoxRefusal(drop.type, item, info);
    if (why) { this.say(why); return; }
    const price = npcBoxPrice(drop.type, item, info, drop.rate);
    if (price !== null && price > this.m.inventory.gold) { this.say("Not enough gold."); return; }
    if (drop.type === PanelType.ReplaceWedRing && !this.wornWeddingRing()) { this.say(T.NotWearingWeddingRing); return; }
    if (drop.type === PanelType.Consign) { drop.confirm(); return; } // asks the price first (consignPrice -> askConsignPrice)
    this.pending = { id: item.UniqueID, type: drop.type, name: this.nameOf(item, info), gained: "" };
    this.note = "";
    restartAnim(this.altar, "work");
    drop.confirm();
    this.waitAnswer();
  }

  /** Refusals come only as chat lines: give up waiting after a few seconds (only while shown: closed() ends it). */
  private waitAnswer(): void {
    this.stopWait?.();
    this.stopWait = later(() => { this.stopWait = null; if (this.pending) { this.pending = null; this.say("See the chat for the server's answer."); } }, ANSWER_MS, this.signal);
    this.invalidate();
  }

  /** NpcDrop.consignPrice: the price popup, then C.ConsignItem while the item is still on the altar. */
  private askConsignPrice(item: UserItem): void {
    if (!this.open || this.pending) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    const q = this.ui.rt.popups.amount({
      title: "Consignment price", text: "Price in gold:", max: MAX_CONSIGNMENT, min: MIN_CONSIGNMENT, value: MIN_CONSIGNMENT,
      leftLabel: "gold under the price limit",
      item: info ? { name: this.nameOf(item, info), img: info.Image, count: item.Count } : undefined,
    });
    const off = () => q.close();
    this.signal.addEventListener("abort", off, { once: true });
    void q.then((price) => {
      this.signal.removeEventListener("abort", off);
      const drop = this.m.npcSession.drop;
      if (price === null || !this.open || this.pending || !drop.open || drop.type !== PanelType.Consign || drop.targetId !== item.UniqueID) return;
      this.pending = { id: item.UniqueID, type: PanelType.Consign, name: this.nameOf(item, info), gained: "" };
      this.note = "";
      restartAnim(this.altar, "work");
      drop.consign(item, price);
      this.waitAnswer();
    });
  }

  /** The server's answer to the pending request (the models have applied it already). Never consumes. */
  private answer(p: ServerPacket): void {
    const pd = this.pending;
    if (!pd) return;
    let done = "";
    switch (p.type) {
      case "GainedItem":
        if (pd.type === PanelType.Disassemble) {
          const info = this.m.itemInfos.get(p.Item.ItemIndex);
          pd.gained = `${this.nameOf(p.Item, info)}${p.Item.Count > 1 ? ` x${p.Item.Count}` : ""}`;
        }
        return;
      case "DeleteItem":
        if (p.UniqueID !== pd.id) return;
        done = pd.type === PanelType.Disassemble
          ? `${pd.name} broke down${pd.gained ? ` into ${pd.gained}` : ""}.`
          : `${pd.name} was destroyed.`;
        break;
      case "RefreshItem": {
        if (p.Item.UniqueID !== pd.id) return;
        if (pd.type === PanelType.Downgrade) done = `${pd.name} is now awakening level ${awakeLevel(p.Item)}.`;
        else if (pd.type === PanelType.Reset) done = `${pd.name}'s added stats were reset.`;
        else return; // the ring's RefreshItem follows S.EquipItem
        break;
      }
      case "ConsignItem":
        if (p.UniqueID !== pd.id || pd.type !== PanelType.Consign) return;
        done = p.Success ? `${pd.name} is listed at the trust merchant.` : `${pd.name} could not be consigned.`;
        break;
      case "EquipItem":
        if (p.UniqueID !== pd.id || pd.type !== PanelType.ReplaceWedRing) return;
        done = p.Success ? `Your marriage bond now lives in ${pd.name}.` : "The ring could not be replaced.";
        break;
      default: return;
    }
    this.endWait();
    this.pending = null;
    this.result(done);
    this.invalidate();
  }

  private endWait(): void {
    this.stopWait?.();
    this.stopWait = null;
    this.pending = null;
  }

  /** The result banner over the altar (the server's outcome). */
  private result(text: string): void {
    setText(this.res, text);
    this.res.hidden = false;
    restartAnim(this.res, "show");
    this.stopBanner?.();
    this.stopBanner = later(() => { this.stopBanner = null; this.res.hidden = true; }, 3300, this.signal);
  }

  private say(text: string): void {
    this.note = text;
    setText(this.noteEl, text);
  }

  /** PlayerObject.ReplaceWeddingRing: the ring worn on the left hand, when it is the wedding ring. */
  private wornWeddingRing(): UserItem | null {
    const r = this.m.inventory.equipment[EquipmentSlot.RingL] ?? null;
    return r && r.WeddingRing !== -1 ? r : null;
  }

  private nameOf(item: UserItem, info: ItemInfo | undefined): string {
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }
}

registerWindow(NPC_BOX_KEY, (host) => new NpcBoxWindow(host), { binds: [], watch: (host, open) => watchNpcBox(host, open) });

/** Opens the box when the server opens one of its NPCDropDialog services (NpcDrop.shown with a box type) and closes it
 *  when the panel hides or another panel type replaces it. The window is built lazily, so this lives outside it: the
 *  registry entry's watch hook runs it once per game at UI start. Returns its removal. */
function watchNpcBox(host: ModernUiHost, open: (show?: boolean) => boolean): () => void {
  const drop = host.ctx.models.npcSession.drop;
  const offs = [
    drop.shown.on(({ type }) => { if (NPC_BOX_TYPES.has(type)) open(true); else if (host.rt.isOpen(NPC_BOX_KEY)) open(false); }),
    drop.hidden.on(() => { if (host.rt.isOpen(NPC_BOX_KEY)) open(false); }),
  ];
  return () => { for (const off of offs) off(); };
}
