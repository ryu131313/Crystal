// NPC dialogs with the desktop artwork (Client/MirScenes/Dialogs/NPCDialogs.cs, Client/MirControls/MirGoodsCell.cs):
//   NPCDialog:       Prguse 384 / 385 (995 before the first page); S.NPCResponse page lines in 8 rows of 9pt Arial with the
//                    desktop markup: <text/@key> links (yellow, red on hover; C.CallNPC "[@key]", @Exit closes),
//                    {text/Colour} coloured text, (text/url) web links, [ITEM|MONSTER|NPC:idx(|name)] and <$ITEM:idx> info
//                    links (cyan, orange on hover, with the NPCDialog.ShowTooltip panel); Up / Down / PositionBar and the
//                    wheel scroll the rows. <<text/@key/Colour>> lines become BigButtons in the BigButtonDialog (Title 836-840
//                    backgrounds, Title 841 buttons tinted by the colour, scrolled 8 at a time). NameLabel, Help, Close and the
//                    animated QuestButton (hooks set by ui/questWindow.ts, which holds QuestListDialog). Talks are gated like
//                    GameScene.NPCTime (5 s until the server answers).
//   NPCGoodsDialog:  Prguse 1000 under the NPC dialog, 8 MirGoodsCells + scroll; click selects, double-click / Buy buys
//                    (C.BuyItem, a MirAmountBox for stackables). Buy lists show one cell per item kind; a kind with several
//                    entries (the buy-back list, [@BUYBACK]) opens the NPCSubGoodsDialog (PanelType.BuySub). The same class is
//                    the craft list (PanelType.Craft, npcServices.ts) and the pearl shop (UsePearls, S.NPCPearlGoods).
//   NPCDropDialog:   Prguse2 351 (Title 711 for Disassemble), one MirItemCell, Hold + Confirm, the price line; S.NPCSell /
//                    NPCRepair / NPCSRepair / NPCConsign here, and the PanelTypes npcServices.ts opens (refine, check refine,
//                    wedding ring, disassemble, downgrade, reset). An inventory click targets the item (or pick one and click
//                    the cell, like the desktop); Refine keeps the plain pick for the refine grid.
// Storage (StorageDialog) is ui/storageWindow.ts; refine / awakening / crafting are ui/npcServices.ts.
// Deliberate differences: the InventoryDialog is not moved beside the NPC dialog (InventoryWindow has no location API);
// (text/url) links also open https; the BigButton's ForeColour tint is a canvas multiply of the picture.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { BindMode, ItemGrade, ItemType, ItemTypeName, MarketPanelType, PanelType, RequiredClass, RequiredClassName, RequiredType, Stat } from "../protocol/enums.generated.ts";
import type { Session } from "../game/session.ts";
import type { Inventory } from "../game/inventory.ts";
import { type ItemInfos, itemPrice, repairPrice } from "../game/items.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { LibPath, monsterLibPath } from "../game/libraries.ts";
import type { CurrentMap } from "../game/currentMap.ts";
import { type GameInfo, type InfoKind, itemFriendlyName } from "../game/gameInfo.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, closeButton, setPicture, libPicture, libPictureNow, libSize, getUiRoot, uiSound, hideHint, type Size } from "./mir/controls.ts";
import { amountBox } from "./messageBox.ts";
import { showItemLabel, moveItemLabel, hideItemLabel } from "./itemLabel.ts";
import { dragY } from "./mir/controls.ts";

const BIG = /<<((.*?)\/(@.*?))>>/g;
// One pass over every inline token, in index order like NPCDialog.NewText's sorted match list.
const INLINE = /<((?:[^<>]*?)\/(?:@[^<>]*?))>|\{(.*?)\/(.*?)\}|\((.*?)\/(.*?)\)|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>/gi;

const MAXIMUM_LINES = 8; // NPCDialog.MaximumLines
const FONT_9 = "9pt Arial"; // NPCDialog.font (Settings.FontName 9F)
const MIN_CONSIGNMENT = 5000, MAX_CONSIGNMENT = 50000000; // Globals.MinConsignment / MaxConsignment

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  PriceGold: (n: number) => `Price: ${n} gold`,
  PricePearl: (n: number, s: string) => `Price: ${n} pearl${s}`,
  PurchaseAmount: "Purchase Amount:",
  LowGold: "Not enough gold.",
  NotEnoughPearls: "You do not have enough Pearls.",
  YouNoBagSpace: "You do not have enough space.",
  CannotSellItem: "Cannot sell this item.",
  CannotCarryMoreGold: "Cannot carry anymore gold.",
  CannotRepairItem: "Cannot repair this item.",
  CannotConsignItem: "Cannot consign this item.",
  ConsignmentPrice: "Consignment Price:",
  YouDontHaveEnoughGoldToRefine: (n: string) => `You don't have enough gold to refine your ${n}.`,
  YouHaventDepositedItemsToRefine: (n: string) => `You haven't deposited any items to refine your ${n} with.`,
  ItemHasntBeenRefinedNoChecking: (n: string) => `Your ${n} hasn't been refined so it doesn't need checking.`,
  ItemIsNotRing: (n: string) => `${n} isn't a ring.`,
  Sale: "Sale: ", Repair: "Repair: ", SpecialRepair: "S. Repair: ", Consignment: "Consignment: ",
  ItemWillBeDestroyed: "Item will be Destroyed\n\n\n\n\n\n\n\n         ", Downgrade: "Downgrade: ", Reset: "Reset: ",
  Refine: "Refine: ", CheckRefine: "Check Refine", ReplaceWedRing: "Replace: ", Gold2: " Gold",
  MonsterIndexTitle: (i: number) => `Monster Index: ${i}`, NPCIndexTitle: (i: number) => `NPC Index: ${i}`, ItemIndexTitle: (i: number) => `Item Index: ${i}`,
  MonsterInfoLoading: "Monster info is being loaded...", NPCInfoLoading: "NPC info is being loaded...", ItemInfoLoading: "Item info is being loaded...",
};

type LinkKind = "MONSTER" | "NPC" | "ITEM";
interface PageLink { span: HTMLElement; kind: LinkKind; idx: number; name: string | undefined }
interface BigButtonInfo { text: string; action: string; colour: string }

/** RequiredClass.ToLocalizedString (the enum name; a combination of flags reads "Warrior, Wizard" like Enum.ToString). */
function requiredClassText(value: number): string {
  const named = RequiredClassName[value];
  if (named) return named;
  const parts: string[] = [];
  for (const [k, v] of Object.entries(RequiredClass)) if ((v & (v - 1)) === 0 && (value & v) !== 0) parts.push(k);
  return parts.join(", ") || String(value);
}

/** C# float ToString for the NPCDropDialog prices (uint * NPCRate). */
export const floatText = (v: number) => String(Number(Math.fround(v).toPrecision(7)));
/** Stats.Count: the sum of |value| (UserItem.AddedStats.Count in the price formulas). */
export const addedCount = (item: UserItem) => Object.values(item.AddedStats).reduce((sum, v) => sum + Math.abs(v), 0);
/** Awake.GetAwakeLevel. */
export const awakeLevel = (item: UserItem) => item.AwakeLevels.length;

/** The element's top-left on the 1024 x 768 stage (DisplayLocation). */
function stagePoint(target: HTMLElement): [number, number] {
  const root = getUiRoot();
  const r = root.getBoundingClientRect();
  const s = r.width / (root.offsetWidth || 1) || 1;
  const a = target.getBoundingClientRect();
  return [Math.round((a.left - r.left) / s), Math.round((a.top - r.top) / s)];
}

/** Wheel notches (SystemInformation.MouseWheelScrollDelta): +1 = up. */
const wheelCount = (e: WheelEvent) => (e.deltaY < 0 ? 1 : e.deltaY > 0 ? -1 : 0);

// — ForeColour tint (DXManager draws the picture multiplied by the colour) —

const tints = new Map<string, Promise<string | null>>();

/** Library[index] multiplied by a CSS colour, as an object URL (null = no picture or not a known colour: Color.FromName
 *  gives an empty, fully transparent colour for an unknown name). */
function tintedUrl(library: string, index: number, colour: string): Promise<string | null> {
  const key = `${library}#${index}#${colour}`;
  let p = tints.get(key);
  if (!p) {
    p = (async () => {
      if (!CSS.supports("color", colour)) return null;
      const pic = await libPicture(library, index);
      if (!pic) return null;
      const img = new Image();
      img.src = pic.url;
      await img.decode();
      const canvas = document.createElement("canvas");
      canvas.width = pic.width;
      canvas.height = pic.height;
      const g = canvas.getContext("2d")!;
      g.drawImage(img, 0, 0);
      g.globalCompositeOperation = "multiply";
      g.fillStyle = colour;
      g.fillRect(0, 0, pic.width, pic.height);
      g.globalCompositeOperation = "destination-in";
      g.drawImage(img, 0, 0);
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve));
      return blob ? URL.createObjectURL(blob) : null;
    })().catch(() => null);
    tints.set(key, p);
  }
  return p;
}

/** BigButton (NPCDialogs.cs): Title 841 / 842 / 843 drawn with ForeColour, then Index + 3 on top in white; the text in
 *  bold 12pt over a black shadow 2 px right / down, centred in 237 px. */
class BigButton {
  readonly button: MirButton;
  private readonly base = document.createElement("div");
  private readonly overlay = document.createElement("div");
  private over = false;
  private down = false;

  constructor(parent: MirControl, info: BigButtonInfo, onClick: () => void) {
    this.button = new MirButton({ parent, library: Lib.Title, index: 841, hoverIndex: 842, pressedIndex: 843, drawImage: false, size: [0, 0], sound: SoundList.ButtonA, onClick });
    void libSize(Lib.Title, 841).then((s) => { this.button.size = s; });
    this.base.className = this.overlay.className = "mir-pic";
    this.button.el.append(this.base, this.overlay);
    const font = "bold 12pt Arial";
    new MirLabel({ parent: this.button, notControl: true, location: [2, 7], size: [237, 20], align: "center", foreColour: "#000", font, text: info.text });
    new MirLabel({ parent: this.button, notControl: true, location: [0, 5], size: [237, 20], align: "center", foreColour: "#fff", font, text: info.text });
    const b = this.button.el;
    b.addEventListener("pointerenter", () => { this.over = true; this.draw(info.colour); });
    b.addEventListener("pointerleave", () => { this.over = this.down = false; this.draw(info.colour); });
    b.addEventListener("pointerdown", (e) => { if (e.button === 0) { this.down = true; this.draw(info.colour); } });
    b.addEventListener("pointerup", () => { this.down = false; this.draw(info.colour); });
    this.draw(info.colour);
  }

  /** BigButton.DrawControl: MirButton.Index (hover / pressed) tinted, then Index + 3. */
  private draw(colour: string): void {
    const index = this.down && this.over ? 843 : this.over ? 842 : 841;
    const key = `${index}#${colour}`;
    this.base.dataset.tint = key;
    void tintedUrl(Lib.Title, index, colour).then((url) => {
      if (this.base.dataset.tint !== key) return;
      const pic = libPictureNow(Lib.Title, index);
      this.base.style.backgroundImage = url ? `url("${url}")` : "";
      this.base.style.width = (pic?.width ?? 0) + "px";
      this.base.style.height = (pic?.height ?? 0) + "px";
    });
    void setPicture(this.overlay, Lib.Title, index + 3);
  }
}

/** BigButtonDialog: the button backgrounds (Title 836 single, 838 / 839 / 840 first / middle / last), the 837 footer,
 *  up to 8 rows (MaximumRows) scrolled with Up / Down / the wheel. */
class BigButtonDialog {
  readonly control: MirControl;
  private buttons: BigButton[] = [];
  private scrollOffset = 0;
  height = 0;

  constructor(parent: MirControl) {
    this.control = new MirControl({ parent, visible: false });
    this.control.el.addEventListener("wheel", (e) => {
      e.preventDefault();
      e.stopPropagation();
      const count = wheelCount(e);
      if (count > 0) this.scrollUp(); else if (count < 0) this.scrollDown();
    }, { passive: false });
  }

  get visible(): boolean { return this.control.visible; }

  /** BigButtonDialog.Show (sizes come from the preloaded pictures). */
  show(list: BigButtonInfo[], minimumButtons: number, click: (action: string) => void, size: (index: number) => Size): void {
    this.control.el.replaceChildren();
    this.scrollOffset = 0;
    let width = 0, height = 0;
    const count = Math.max(minimumButtons, list.length);
    for (let i = 0; i < Math.min(count, 8); i++) {
      const index = count === 1 ? 836 : i === 0 ? 838 : i === count - 1 ? 840 : 839;
      new MirImage({ parent: this.control, library: Lib.Title, index, location: [list.length === 1 ? -1 : 0, height] });
      const [w, h] = size(index);
      width = w;
      height += h;
    }
    this.buttons = list.map((b) => new BigButton(this.control, b, () => click(b.action)));
    this.refreshButtons();
    new MirImage({ parent: this.control, library: Lib.Title, index: 837, location: [-1, height] });
    height += size(837)[1];
    if (list.length > 8) {
      new MirButton({ parent: this.control, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], sound: SoundList.ButtonA, location: [width - 26, 17], onClick: () => this.scrollUp() });
      new MirButton({ parent: this.control, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], sound: SoundList.ButtonA, location: [width - 26, height - 57], onClick: () => this.scrollDown() });
    }
    this.control.size = [width, height];
    this.height = height;
    this.control.visible = true;
  }

  hide(): void {
    this.control.visible = false;
    this.control.el.replaceChildren();
    this.buttons = [];
    this.height = 0;
  }

  /** BigButtonDialog.RefreshButtons: rows at (97, 7 + i * 40). */
  private refreshButtons(): void {
    for (const b of this.buttons) b.button.visible = false;
    for (let i = 0; i < Math.min(this.buttons.length, 8); i++) {
      const b = this.buttons[i + this.scrollOffset];
      if (!b) continue;
      b.button.visible = true;
      b.button.location = [97, 7 + i * 40];
    }
  }

  private scrollUp(): void {
    if (this.scrollOffset <= 0) return;
    this.scrollOffset--;
    this.refreshButtons();
  }

  private scrollDown(): void {
    if (this.scrollOffset + 8 >= this.buttons.length) return;
    this.scrollOffset++;
    this.refreshButtons();
  }
}

// — NPCGoodsDialog + MirGoodsCell —

/** What a goods list needs from the scene. */
export interface GoodsContext {
  inv: Inventory;
  infos: ItemInfos;
  invWindow: InventoryWindow;
  send: (frame: Uint8Array) => void;
  /** A system chat line. */
  chat: (text: string) => void;
  /** GameScene.NPCRate. */
  rate: () => number;
  /** GameScene.HideAddedStoreStats. */
  hideAdded: () => boolean;
  /** NPCDialog.Size.Height (NPCGoodsDialog.Show puts the list under it). */
  npcHeight: () => number;
  /** UserObject.PearlCount (null = unknown: the server decides). */
  pearls: () => number | null;
  /** GameScene.RecipeInfoList[item].Gold for the craft list. */
  recipeGold: (itemIndex: number) => number | undefined;
}

/** MirGoodsCell: 205 x 32; the item centred in the left 40 px, the count at (23, 17), the name at (44, 0), the price at
 *  (44, 14), the "new" icon (Prguse 550) at (190, 5); a lime border with a divider at x 40 when selected. */
class GoodsCell {
  readonly control: MirControl;
  private readonly icon = document.createElement("div");
  private readonly divider = document.createElement("div");
  private readonly nameLabel: MirLabel;
  private readonly countLabel: MirLabel;
  private readonly priceLabel: MirLabel;
  private readonly newIcon: MirImage;
  item: UserItem | null = null;

  constructor(parent: MirControl, location: readonly [number, number], ctx: GoodsContext, onClick: (cell: GoodsCell) => void, onDoubleClick: (cell: GoodsCell) => void) {
    this.control = new MirControl({ parent, location, size: [205, 32] });
    this.icon.className = "mir-pic";
    this.control.el.append(this.icon);
    this.nameLabel = new MirLabel({ parent: this.control, autoSize: true, notControl: true, location: [44, 0] });
    this.countLabel = new MirLabel({ parent: this.control, autoSize: true, notControl: true, location: [23, 17], foreColour: "#ffff00" });
    this.priceLabel = new MirLabel({ parent: this.control, autoSize: true, notControl: true, location: [44, 14] });
    this.newIcon = new MirImage({ parent: this.control, library: Lib.Prguse, index: 550, location: [190, 5], notControl: true, visible: false });
    Object.assign(this.divider.style, { position: "absolute", left: "40px", top: "-1px", width: "1px", height: "33px", background: "#00ff00", pointerEvents: "none" });
    this.divider.hidden = true;
    this.control.el.append(this.divider);
    const el = this.control.el;
    el.addEventListener("click", (e) => { e.stopPropagation(); uiSound(SoundList.ButtonC); onClick(this); });
    el.addEventListener("dblclick", (e) => { e.stopPropagation(); onDoubleClick(this); });
    // OnMouseEnter / OnMouseLeave: GameScene.CreateItemLabel(Item, hideAdded: HideAddedStoreStats)
    el.addEventListener("pointerenter", (e) => { if (this.item) showItemLabel(this.item, ctx.invWindow.labelContext(), e.clientX, e.clientY, { hideAdded: ctx.hideAdded() }); });
    el.addEventListener("pointermove", (e) => moveItemLabel(e.clientX, e.clientY));
    el.addEventListener("pointerleave", () => hideItemLabel());
  }

  /** MirGoodsCell.Update + DrawItem. */
  set(item: UserItem, ctx: GoodsContext, selected: boolean, multipleAvailable: boolean, usePearls: boolean, recipe: boolean): void {
    this.item = item;
    const info = ctx.infos.get(item.ItemIndex);
    this.control.el.style.outline = selected ? "1px solid #00ff00" : "";
    this.divider.hidden = !selected;
    this.newIcon.visible = false;
    if (!info) { this.nameLabel.text = this.countLabel.text = this.priceLabel.text = ""; void setPicture(this.icon, null, -1); return; }
    this.nameLabel.text = itemFriendlyName(info) || info.Name;
    this.countLabel.text = item.Count <= 1 ? "" : String(item.Count);
    this.newIcon.visible = !item.IsShopItem || multipleAvailable;
    const rate = ctx.rate();
    const price = itemPrice(item, info);
    if (usePearls) this.priceLabel.text = T.PricePearl(Math.trunc(Math.fround(price * rate)), price > 1 ? "s" : "");
    else if (recipe) this.priceLabel.text = T.PriceGold(Math.trunc(Math.fround((ctx.recipeGold(item.ItemIndex) ?? 0) * rate)));
    else this.priceLabel.text = T.PriceGold(Math.trunc(Math.fround(price * rate)));
    if (this.icon.dataset.pic !== `${Lib.Items}#${info.Image}`) {
      void setPicture(this.icon, Lib.Items, info.Image).then((pic) => {
        if (!pic) return;
        // offSet = ((40 - size.Width) / 2, (32 - size.Height) / 2)
        this.icon.style.left = Math.trunc((40 - pic.width) / 2) + "px";
        this.icon.style.top = Math.trunc((32 - pic.height) / 2) + "px";
      });
    }
  }
}

/** NPCGoodsDialog (PanelType Buy / BuySub / Craft; UsePearls for S.NPCPearlGoods). */
export class GoodsDialog {
  readonly type: number;
  usePearls = false;
  /** Cell click (NPCCraftGoodsDialog: select the recipe for the CraftDialog). */
  onSelect: ((item: UserItem) => void) | null = null;
  /** NPCGoodsDialog.Hide (the craft list hides the CraftDialog). */
  onHide: (() => void) | null = null;
  /** GameScene.NPCSubGoodsDialog (the Buy list opens it for an item kind with several entries). */
  subGoods: GoodsDialog | null = null;
  private readonly ctx: GoodsContext;
  private readonly win: MirWindow;
  private readonly cells: GoodsCell[] = [];
  private readonly positionBar: MirButton;
  private goods: UserItem[] = [];
  private displayGoods: UserItem[] = [];
  private startIndex = 0;
  private selected: UserItem | null = null;

  constructor(type: number, ctx: GoodsContext) {
    this.type = type;
    this.ctx = ctx;
    // NPCGoodsDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 1000, location: [0, 224], sort: true });
    for (let i = 0; i < 8; i++) {
      this.cells.push(new GoodsCell(this.win, [10, 34 + i * 33], ctx, (cell) => this.cellClick(cell), () => { if (this.type !== PanelType.Craft) this.buyItem(); }));
    }
    closeButton(this.win, [217, 3], () => this.hide());
    new MirButton({ parent: this.win, library: Lib.Title, index: 312, hoverIndex: 313, pressedIndex: 314, location: [77, 304], sound: SoundList.ButtonA, visible: type !== PanelType.Craft, onClick: () => this.buyItem() });
    new MirImage({ parent: this.win, library: Lib.Title, index: type === PanelType.Craft ? 12 : 27, location: [20, 9] });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, location: [219, 35], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex === 0) return; this.startIndex--; this.update(); } });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [219, 284], sound: SoundList.ButtonA,
      onClick: () => {
        if (this.displayGoods.length <= 8 || this.startIndex === this.displayGoods.length - 8) return;
        this.startIndex++;
        this.update();
      } });
    this.positionBar = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [219, 49], sound: 0 });
    dragY(this.positionBar, (y) => this.positionBarMoving(y));
    // PositionBar.MouseUp += Update
    this.positionBar.el.addEventListener("pointerdown", () => window.addEventListener("pointerup", () => this.update(), { once: true }));
    // NPCGoodsPanel_MouseWheel
    this.win.el.addEventListener("wheel", (e) => {
      e.preventDefault();
      const count = wheelCount(e);
      if (this.startIndex === 0 && count >= 0) return;
      if (this.startIndex === this.displayGoods.length - 1 && count <= 0) return;
      this.startIndex -= count;
      this.update();
    }, { passive: false });
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  get visible(): boolean { return this.win.visible; }

  private cellClick(cell: GoodsCell): void {
    this.selected = cell.item;
    this.update();
    if (this.type === PanelType.Craft && cell.item) this.onSelect?.(cell.item);
  }

  /** NPCGoodsDialog.CheckSubGoods. */
  private checkSubGoods(): boolean {
    const sel = this.selected;
    if (!sel || !this.subGoods) return false;
    if (this.type === PanelType.Buy && !this.usePearls) {
      const list = this.goods.filter((x) => x.ItemIndex === sel.ItemIndex);
      if (list.length > 1 || this.subGoods.visible) {
        this.subGoods.newGoods(list);
        this.subGoods.show();
        return true;
      }
    }
    return false;
  }

  /** User.GetMaxGain for a bag purchase (bag space only: free slots and room in matching stacks; weight is the server's). */
  private maxGain(item: UserItem, stack: number): number {
    let room = 0;
    for (const slot of this.ctx.inv.inventory) {
      if (!slot) room += stack;
      else if (slot.ItemIndex === item.ItemIndex && slot.Count < stack) room += stack - slot.Count;
      if (room >= item.Count) return item.Count;
    }
    return Math.min(room, item.Count);
  }

  /** NPCGoodsDialog.BuyItem. */
  private buyItem(): void {
    const sel = this.selected;
    if (!sel) return;
    if (this.checkSubGoods()) return;
    const info = this.ctx.infos.get(sel.ItemIndex);
    if (!info) return;
    const send = (count: number) => this.ctx.send(ClientPackets.BuyItem({ ItemIndex: sel.UniqueID, Count: count, Type: PanelType.Buy }));
    if (info.StackSize > 1) {
      let maxQuantity = info.StackSize;
      const full = { ...sel, Count: info.StackSize };
      const fullPrice = itemPrice(full, info);
      const unit = Math.max(1, fullPrice / full.Count);
      if (this.usePearls) {
        const pearls = this.ctx.pearls();
        if (pearls !== null && fullPrice > pearls) {
          maxQuantity = Math.min(0xffff, Math.trunc(pearls / unit));
          if (maxQuantity === 0) { this.ctx.chat(T.NotEnoughPearls); return; }
        }
      } else if (fullPrice > this.ctx.inv.gold) {
        maxQuantity = Math.min(0xffff, Math.trunc(this.ctx.inv.gold / unit));
        if (maxQuantity === 0) { this.ctx.chat(T.LowGold); return; }
      }
      const gain = this.maxGain(full, info.StackSize);
      if (gain === 0) { this.ctx.chat(T.YouNoBagSpace); return; }
      if (gain < maxQuantity) maxQuantity = gain;
      amountBox(T.PurchaseAmount, info.Image, maxQuantity, (n) => { if (n > 0) send(n); }, 0, Math.min(sel.Count, maxQuantity));
      return;
    }
    if (this.usePearls) {
      const pearls = this.ctx.pearls();
      if (pearls !== null && info.Price > pearls) { this.ctx.chat(T.NotEnoughPearls); return; }
    } else if (info.Price > this.ctx.inv.gold) { this.ctx.chat(T.LowGold); return; }
    if (!this.ctx.inv.inventory.some((i) => !i)) { this.ctx.chat(T.YouNoBagSpace); return; }
    send(1);
  }

  /** NPCGoodsDialog.PositionBar_OnMoving. */
  private positionBarMoving(y: number): void {
    const barHeight = this.positionBar.size[1];
    if (y >= 282 - barHeight) y = 282 - barHeight;
    if (y < 49) y = 49;
    const h = 233 - barHeight;
    const start = Math.round((y - 49) / (h / Math.max(1, this.displayGoods.length - 8)));
    this.positionBar.location = [219, y];
    if (start === this.startIndex) return;
    this.startIndex = start;
    this.update();
  }

  /** NPCGoodsDialog.Update. */
  update(): void {
    const count = this.displayGoods.length;
    if (this.startIndex > count - 8) this.startIndex = count - 8;
    if (this.startIndex <= 0) this.startIndex = 0;
    if (count > 8) {
      this.positionBar.visible = true;
      const h = 233 - this.positionBar.size[1];
      this.positionBar.location = [219, 49 + Math.trunc((h / (count - 8)) * this.startIndex)];
    } else this.positionBar.visible = false;
    for (let i = 0; i < 8; i++) {
      const cell = this.cells[i]!;
      const item = this.displayGoods[i + this.startIndex];
      if (!item) { cell.control.visible = false; cell.item = null; continue; }
      cell.control.visible = true;
      const matching = this.goods.filter((x) => x.ItemIndex === item.ItemIndex);
      cell.set(item, this.ctx, this.selected === item, matching.length > 1 && matching.some((x) => !x.IsShopItem), this.usePearls, this.type === PanelType.Craft);
    }
  }

  /** NPCGoodsDialog.NewGoods + AddGoods. */
  newGoods(list: UserItem[]): void {
    this.goods = [];
    this.displayGoods = [];
    if (this.type === PanelType.BuySub) {
      this.startIndex = 0;
      this.selected = null;
      list = [...list].sort((a, b) => itemPrice(a, this.ctx.infos.get(a.ItemIndex)) - itemPrice(b, this.ctx.infos.get(b.ItemIndex)));
    }
    for (const item of list) {
      // Normal shops just want to show one of each item type
      if (this.type === PanelType.Buy && !this.usePearls) {
        this.goods.push(item);
        if (this.displayGoods.some((x) => x.ItemIndex === item.ItemIndex)) continue;
      }
      this.displayGoods.push(item);
    }
    if (this.subGoods?.visible) this.checkSubGoods();
    this.update();
  }

  /** NPCGoodsDialog.Show: under the NPC dialog, with the inventory. */
  show(): void {
    this.win.location = [this.win.location[0], this.ctx.npcHeight()];
    this.win.show();
    this.ctx.invWindow.toggle(true);
    this.update();
  }

  /** NPCGoodsDialog.Hide. */
  hide(): void {
    if (!this.win.visible) return;
    this.win.hide();
    hideItemLabel();
    this.onHide?.();
  }

  /** Redraws the cells (a recipe or the rate changed). */
  refresh(): void {
    if (this.win.visible) this.update();
  }
}

// — NPCDropDialog —

/** The PanelTypes NPCDropDialog serves. */
export type DropType = typeof PanelType.Sell | typeof PanelType.Repair | typeof PanelType.SpecialRepair | typeof PanelType.Consign
  | typeof PanelType.Refine | typeof PanelType.CheckRefine | typeof PanelType.ReplaceWedRing
  | typeof PanelType.Disassemble | typeof PanelType.Downgrade | typeof PanelType.Reset;

/** NPCDropDialog: one shared panel for every item service (GameScene.NPCDropDialog). TargetItem is locked in the bag
 *  (OldCell.Locked) while it sits in the cell. */
export class NpcDropDialog {
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string) => void;
  private readonly npcHeight: () => number;
  private readonly win: MirWindow;
  private readonly holdButton: MirButton;
  private readonly holdImage: MirImage;
  private readonly confirmButton: MirButton;
  private readonly infoLabel: MirLabel;
  private cell: HTMLElement | null = null;
  private cellItem: UserItem | null | undefined = undefined;
  private hook: ((index: number) => boolean) | null = null;
  pType: DropType = PanelType.Sell;
  /** GameScene.NPCRate for the prices. */
  rate = 1;
  private hold = false;
  private target: bigint | null = null; // TargetItem (its bag cell is OldCell)

  constructor(inv: Inventory, invWindow: InventoryWindow, send: (f: Uint8Array) => void, chat: (text: string) => void, npcHeight: () => number) {
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.npcHeight = npcHeight;
    // NPCDropDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 392, location: [264, 224], sort: true });
    // NPCDropPanel_Click: the (20, 55, 75, 75) area acts as the cell
    new MirControl({ parent: this.win, location: [20, 55], size: [75, 75], onClick: () => this.itemCellClick() });
    this.holdButton = new MirButton({ parent: this.win, library: Lib.Title, index: 293, hoverIndex: 294, pressedIndex: 295, location: [114, 36], sound: SoundList.ButtonA,
      onClick: () => { this.hold = !this.hold; this.render(); } });
    // AfterDraw: Hold draws Title 295 over the button
    this.holdImage = new MirImage({ parent: this.win, library: Lib.Title, index: 295, location: [114, 36], notControl: true, visible: false });
    this.confirmButton = new MirButton({ parent: this.win, library: Lib.Title, index: 290, hoverIndex: 291, pressedIndex: 292, location: [114, 62], sound: SoundList.ButtonA, onClick: () => this.confirm() });
    this.infoLabel = new MirLabel({ parent: this.win, autoSize: true, location: [30, 10], notControl: true });
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  get visible(): boolean { return this.win.visible; }

  /** NPCDropDialog.Show (GameScene.NPCSell / NPCRepair / ... set PType first). */
  show(type: DropType, rate?: number): void {
    this.clearTarget();
    this.pType = type;
    if (rate !== undefined) this.rate = rate;
    this.hold = false;
    this.invWindow.toggle(true);
    // A click on a bag item targets it; Refine keeps the plain pick for the refine grid.
    if (type !== PanelType.Refine) {
      this.hook = (index) => { this.takeTarget(index); return true; };
      this.invWindow.itemClickHook = this.hook;
    } else this.unhook();
    this.win.show();
    this.render();
  }

  /** NPCDropDialog.Hide: the targeted cell unlocks. */
  hide(): void {
    this.clearTarget();
    this.unhook();
    if (!this.win.visible) return;
    this.win.hide();
  }

  private unhook(): void {
    if (this.hook && this.invWindow.itemClickHook === this.hook) this.invWindow.itemClickHook = null;
    this.hook = null;
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

  /** The cell / panel click with GameScene.SelectedCell = the picked bag slot. */
  private itemCellClick(): void {
    const slot = this.invWindow.pickedInventorySlot;
    if (slot >= 0) this.invWindow.clearPick();
    this.takeTarget(slot);
  }

  /** NPCDropDialog.ItemCell_Click with the bag slot `index` (-1 = nothing picked). */
  private takeTarget(index: number): void {
    this.clearTarget();
    const item = index >= 0 ? this.inv.inventory[index] ?? null : null;
    if (!item || this.inv.isLocked(item)) { this.render(); return; }
    const info = this.inv.info(item);
    const type = this.pType;
    if (type === PanelType.Disassemble || type === PanelType.Downgrade || type === PanelType.Reset) {
      const ok = type === PanelType.Disassemble ? !!info && info.Grade !== ItemGrade.None && info.Type !== ItemType.Awakening
        : type === PanelType.Downgrade ? awakeLevel(item) !== 0
        : addedCount(item) > 0 || (!!info && item.Slots.length > info.Slots); // UserItem.IsAdded
      if (ok) { this.target = item.UniqueID; this.setLock(item.UniqueID, true); this.inv.changed(); }
      this.render();
      return;
    }
    if (type !== PanelType.Sell && type !== PanelType.Consign && info && info.Durability === 0) { this.render(); return; }
    this.target = item.UniqueID;
    this.setLock(item.UniqueID, true);
    this.inv.changed();
    if (this.hold) this.confirm();
    else this.render();
  }

  /** NPCDropDialog.Confirm. */
  private confirm(): void {
    const item = this.item();
    if (!item) return;
    const info = this.inv.info(item);
    const bind = info?.Bind ?? 0;
    const index = this.inv.inventory.indexOf(item);
    switch (this.pType) {
      case PanelType.Sell:
        if (bind & BindMode.DontSell) { this.chat(T.CannotSellItem); return; }
        if (this.inv.gold + Math.trunc(itemPrice(item, info) / 2) > 0xffffffff) { this.chat(T.CannotCarryMoreGold); break; }
        this.release(); // the request locks it again until S.SellItem
        this.inv.npcAction(index, "sell");
        return;
      case PanelType.Repair:
        if (bind & BindMode.DontRepair) { this.chat(T.CannotRepairItem); return; }
        if (this.inv.gold < repairPrice(item, info) * this.rate) { this.chat(T.LowGold); break; }
        this.release();
        this.inv.npcAction(index, "repair");
        return;
      case PanelType.SpecialRepair:
        if ((bind & BindMode.DontRepair) || (bind & BindMode.NoSRepair)) { this.chat(T.CannotRepairItem); return; }
        if (this.inv.gold < repairPrice(item, info) * 3 * this.rate) { this.chat(T.LowGold); break; }
        this.release();
        this.inv.npcAction(index, "srepair");
        return;
      case PanelType.Consign:
        if ((bind & BindMode.DontStore) || (bind & BindMode.DontSell)) { this.chat(T.CannotConsignItem); return; }
        amountBox(T.ConsignmentPrice, info?.Image ?? -1, MAX_CONSIGNMENT, (price) => {
          if (this.target !== item.UniqueID || price < MIN_CONSIGNMENT) return;
          this.release();
          this.inv.consign(item, price, MarketPanelType.Consign); // locked until S.ConsignItem
        }, MIN_CONSIGNMENT);
        return;
      case PanelType.Disassemble: this.send(ClientPackets.DisassembleItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Downgrade: this.send(ClientPackets.DowngradeAwakening({ UniqueID: item.UniqueID })); break;
      case PanelType.Reset: if (info && !info.NeedIdentify) this.send(ClientPackets.ResetAddedItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Refine:
        if (this.inv.refine.some((i) => i)) {
          if (this.inv.gold >= (info?.RequiredAmount ?? 0) * 10 * this.rate) {
            this.send(ClientPackets.RefineItem({ UniqueID: item.UniqueID }));
            this.target = null; // stays locked: S.RepairItem unlocks it, S.RefineItem removes it
            this.render();
            return;
          }
          this.chat(T.YouDontHaveEnoughGoldToRefine(this.friendly(item)));
          return;
        }
        this.chat(T.YouHaventDepositedItemsToRefine(this.friendly(item)));
        break;
      case PanelType.CheckRefine:
        if (item.RefineAdded === 0) { this.chat(T.ItemHasntBeenRefinedNoChecking(this.friendly(item))); return; }
        this.send(ClientPackets.CheckRefine({ UniqueID: item.UniqueID }));
        break;
      case PanelType.ReplaceWedRing:
        if (info?.Type !== ItemType.Ring) { this.chat(T.ItemIsNotRing(this.friendly(item))); return; }
        this.send(ClientPackets.ReplaceWedRing({ UniqueID: item.UniqueID }));
        break;
    }
    this.clearTarget();
    this.render();
  }

  /** TargetItem = null with the lock handed over to the request. */
  private release(): void {
    if (this.target !== null) this.setLock(this.target, false);
    this.target = null;
  }

  /** NPCDropPanel_BeforeDraw (+ AfterDraw's Hold picture). */
  render(): void {
    if (!this.win.visible) return;
    const item = this.item();
    if (!item && this.target !== null) this.target = null;
    const info = item ? this.inv.info(item) : undefined;
    let library: string = Lib.Prguse2, index = 351;
    let location: [number, number] = [264, this.npcHeight()];
    let confirm: [number, number, number] = [290, 291, 292], confirmAt: [number, number] = [114, 62];
    let infoAt: [number, number] = [30, 10], cellAt: [number, number] = [38, 72];
    let holdVisible = true;
    let text = "";
    let price: string | null = null;
    const r = this.rate;
    switch (this.pType) {
      case PanelType.Sell: text = T.Sale; if (item) price = String(Math.trunc(itemPrice(item, info) / 2)); break;
      case PanelType.Repair: text = T.Repair; if (item) price = floatText(repairPrice(item, info) * r); break;
      case PanelType.SpecialRepair: text = T.SpecialRepair; if (item) price = floatText(repairPrice(item, info) * 3 * r); break;
      case PanelType.Consign: text = T.Consignment; break;
      case PanelType.Disassemble: {
        text = T.ItemWillBeDestroyed;
        holdVisible = false;
        library = Lib.Title; index = 711; location = [234, 224];
        confirm = [715, 716, 717]; confirmAt = [62, 190]; infoAt = [44, 60]; cellAt = [83, 94];
        // UserItem.DisassemblePrice
        if (item) price = String(Math.trunc(1500 * (info?.Grade ?? 0) * Math.fround((addedCount(item) + awakeLevel(item)) * 0.1 + 1)));
        break;
      }
      case PanelType.Downgrade: text = T.Downgrade; holdVisible = false; if (item) price = String(3000 * (1 + (awakeLevel(item) + 1) * 2) * (info?.Grade ?? 0)); break;
      case PanelType.Reset: text = T.Reset; holdVisible = false; if (item) price = String(Math.trunc(3000 * (info?.Grade ?? 0) * Math.fround(addedCount(item) * 0.2 + 1))); break;
      case PanelType.Refine: text = T.Refine; holdVisible = false; if (item && info) price = floatText(info.RequiredAmount * 10 * r); break;
      case PanelType.CheckRefine: text = T.CheckRefine; holdVisible = false; break;
      case PanelType.ReplaceWedRing: text = T.ReplaceWedRing; holdVisible = false; if (item && info) price = floatText(info.RequiredAmount * 10 * r); break;
    }
    if (price !== null) text += price + T.Gold2;
    this.win.library = library;
    this.win.index = index;
    this.win.location = location;
    this.holdButton.visible = holdVisible;
    this.holdImage.visible = holdVisible && this.hold;
    this.confirmButton.setIndices(confirm[0], confirm[1], confirm[2]);
    this.confirmButton.location = confirmAt;
    this.infoLabel.text = text;
    this.infoLabel.location = infoAt;
    // ItemCell: TargetItem (a read-only MirItemCell whose click is ItemCell_Click)
    if (this.cellItem !== item || !this.cell) {
      this.cell?.remove();
      this.cellItem = item;
      this.cell = this.invWindow.viewCell(item);
      this.cell.addEventListener("mousedown", (e) => { if (e.button === 0) this.itemCellClick(); });
      placeCell(this.cell, this.win, cellAt);
    } else {
      this.cell.style.left = cellAt[0] + "px";
      this.cell.style.top = cellAt[1] + "px";
    }
    if (this.infoLabel.el.nextSibling !== null) this.win.el.append(this.infoLabel.el); // the label draws over the cell
  }
}

// — NPCDialog —

const SIZE_PICTURES: [string, number][] = [
  [Lib.Prguse, 995], [Lib.Prguse, 384], [Lib.Prguse, 385],
  [Lib.Title, 836], [Lib.Title, 837], [Lib.Title, 838], [Lib.Title, 839], [Lib.Title, 840],
];

export class NpcWindow {
  private readonly session: Session;
  private readonly now: () => number;
  private npcId = 0;
  private npcName = "";
  private npcTime = 0; // GameScene.NPCTime
  private rate = 1;    // GameScene.NPCRate
  private hideAdded = false; // GameScene.HideAddedStoreStats

  private readonly win: MirWindow;
  private readonly nameLabel: MirLabel;
  private readonly upButton: MirButton;
  private readonly downButton: MirButton;
  private readonly positionBar: MirButton;
  private readonly questButton: MirButton;
  private readonly bigButtonDialog: BigButtonDialog;
  private textControls: MirControl[] = [];
  private currentLines: string[] = [];
  private bigButtons: BigButtonInfo[] = [];
  private index = 0; // NPCDialog._index
  private height = 0; // NPCDialog.Size.Height
  private questFrame = 0;
  private readonly sizes = new Map<string, Size>();
  private readonly sizesReady: Promise<void>;
  private sizesLoaded = false;
  private pageSeq = 0;

  /** NPCGoodsDialog / NPCSubGoodsDialog (PanelType Buy / BuySub; also the pearl shop). */
  readonly goods: GoodsDialog;
  readonly subGoods: GoodsDialog;
  /** NPCDropDialog (shared with npcServices.ts). */
  readonly drop: NpcDropDialog;
  /** UserObject.PearlCount for the pearl shop (set by NpcServices). */
  pearlCount: () => number | null = () => null;
  /** HelpButton: HelpDialog.DisplayPage("Purchasing"). */
  onHelp: () => void = () => {}; // game.ts: HelpDialog.DisplayPage("Purchasing")

  // Link tooltip (NPCDialog._tooltipPanel / _tooltipImage / _tooltipTitle / _tooltipContent)
  private readonly gameInfo: GameInfo;
  private readonly tooltip: MirControl;
  private readonly tooltipImage: MirImage;
  private readonly tooltipTitle: MirLabel;
  private readonly tooltipContent: MirLabel;
  private tooltipLink: PageLink | null = null;
  private tooltipSeq = 0;
  private links: PageLink[] = [];
  /** MapControl.Index / Title, for GetNPCInfo's location line (CurrentMap). */
  private readonly currentMap: CurrentMap;
  private get mapIndex(): number { return this.currentMap.index; }
  private get mapTitle(): string { return this.currentMap.title; }

  private readonly invWindow: InventoryWindow;

  constructor(stage: HTMLElement, session: Session, inv: Inventory, infos: ItemInfos, invWindow: InventoryWindow, now: () => number, gameInfo: GameInfo,
    currentMap: CurrentMap) {
    void stage;
    this.currentMap = currentMap;
    this.session = session;
    this.now = now;
    this.gameInfo = gameInfo;
    this.sizesReady = Promise.all(SIZE_PICTURES.map(([lib, i]) => libSize(lib, i).then((s) => { this.sizes.set(`${lib}#${i}`, s); }))).then(() => {
      this.sizesLoaded = true;
      if (!this.height) this.height = this.size(Lib.Prguse, 995)[1];
    });

    // NPCDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 995, location: [0, 0], sort: true });
    this.win.el.addEventListener("mousedown", () => this.hideTooltip()); // MouseDown += HideTooltip
    this.win.el.addEventListener("wheel", (e) => { e.preventDefault(); this.mouseWheel(wheelCount(e)); }, { passive: false });
    this.nameLabel = new MirLabel({ parent: this.win, autoSize: true, location: [30, 6], font: "bold 10pt Arial", foreColour: "#deb887" });
    this.upButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], location: [417, 34], sound: SoundList.ButtonA, visible: false,
      onClick: () => { if (this.index <= 0) return; this.index--; this.newText(false); this.updatePositionBar(); } });
    this.downButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], location: [417, 175], sound: SoundList.ButtonA, visible: false,
      onClick: () => { if (this.index + MAXIMUM_LINES >= this.currentLines.length) return; this.index++; this.newText(false); this.updatePositionBar(); } });
    this.positionBar = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [417, 47], sound: 0, visible: false });
    dragY(this.positionBar, (y) => this.positionBarMoving(y));
    closeButton(this.win, [413, 3], () => this.close());
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 257, hoverIndex: 258, pressedIndex: 259, location: [390, 3], sound: SoundList.ButtonA, onClick: () => this.onHelp() });
    this.bigButtonDialog = new BigButtonDialog(this.win);
    // QuestButton: MirAnimatedButton Title 530 (10 frames, 130 ms, looped), hover 284, pressed 286
    this.questButton = new MirButton({ parent: this.win, library: Lib.Title, index: 530, hoverIndex: 284, pressedIndex: 286, size: [96, 25], sound: SoundList.ButtonA, visible: false,
      onClick: () => this.onQuestButton(this.npcId) });
    const questTimer = window.setInterval(() => {
      if (!this.questButton.visible || !this.win.visible) return;
      this.questFrame = (this.questFrame + 1) % 10;
      this.questButton.setIndices(530 + this.questFrame, 284, 286);
    }, 130);

    // Link tooltip panel (InitializeTooltipPanel): on the scene, above the windows
    this.tooltip = new MirControl({ parent: getUiRoot(), visible: false, notControl: true, backColour: "rgba(80, 60, 40, 0.94)", border: true, borderColour: "rgb(120, 100, 80)" });
    this.tooltip.el.style.zIndex = "99000";
    this.tooltipImage = new MirImage({ parent: this.tooltip, location: [10, 10], notControl: true, visible: false });
    this.tooltipTitle = new MirLabel({ parent: this.tooltip, autoSize: true, foreColour: "#ffff00", location: [70, 5], notControl: true });
    this.tooltipContent = new MirLabel({ parent: this.tooltip, autoSize: true, location: [70, 25], notControl: true });

    this.invWindow = invWindow;
    const ctx: GoodsContext = {
      inv, infos, invWindow, send: (f) => session.send(f), chat: (t) => invWindow.notify(t), rate: () => this.rate, hideAdded: () => this.hideAdded,
      npcHeight: () => this.dialogHeight, pearls: () => this.pearlCount(), recipeGold: () => undefined,
    };
    this.goods = new GoodsDialog(PanelType.Buy, ctx);
    this.subGoods = new GoodsDialog(PanelType.BuySub, ctx);
    this.goods.subGoods = this.subGoods;
    this.drop = new NpcDropDialog(inv, invWindow, (f) => session.send(f), (t) => invWindow.notify(t), () => this.dialogHeight);

    // A link's text and its open tooltip follow the info once it arrives (the desktop only fills them on the next page / hover).
    gameInfo.onChange((kind, index) => this.infoArrived(kind, index));
    // GameScene KeybindOptions.Closeall: if (NPCDialog.Visible) NPCDialog.Hide()
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Closeall) && this.visible) this.close();
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => { clearInterval(questTimer); this.win.dispose(); this.tooltip.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** NPCDialog.Size.Height (the goods / drop panels sit under it). */
  get dialogHeight(): number {
    return this.height || this.size(Lib.Prguse, 995)[1];
  }

  private size(lib: string, index: number): Size {
    return this.sizes.get(`${lib}#${index}`) ?? [0, 0];
  }

  // — Quests (NPCDialog.QuestButton / CheckQuestButtonDisplay; the quest list is src/ui/questWindow.ts, which sets the hooks) —
  /** NPCObject.GetAvailableQuests().Any() for the NPC (GameScene.NPCID). */
  questsAvailable: (npcId: number) => boolean = () => false;
  onQuestButton: (npcId: number) => void = () => {};
  /** GameScene.NPCID (the NPC the dialog talks to). */
  get currentNpcId(): number {
    return this.npcId;
  }
  /** NPCDialog.CheckQuestButtonDisplay: the name (before "_") and the quest button. */
  refreshQuestButton(): void {
    this.nameLabel.text = this.npcName.split("_")[0] ?? "";
    this.questButton.visible = this.questsAvailable(this.npcId);
  }

  /** GameScene MapControl click on an NPC: C.CallNPC "[@Main]" (not again within 5 s, or while its dialog is open). */
  talk(id: number, name: string): void {
    const t = this.now();
    if (id === this.npcId && (t <= this.npcTime || this.visible)) return;
    this.npcTime = t + 5000;
    this.npcId = id;
    this.npcName = name;
    this.session.send(ClientPackets.CallNPC({ ObjectID: id, Key: "[@Main]" }));
  }

  /** RollDialog.ReturnResult: CallNPC "[page]" on the last NPC (GameScene.NPCID), gated by NPCTime like a dialog link. */
  callPage(page: string): void {
    const t = this.now();
    if (t <= this.npcTime) return;
    this.npcTime = t + 5000;
    this.session.send(ClientPackets.CallNPC({ ObjectID: this.npcId, Key: `[${page}]` }));
  }

  /** NPCDialog.ButtonClicked. */
  private action(key: string): void {
    if (key.toLowerCase() === "@exit") { this.close(); return; }
    const t = this.now();
    if (t <= this.npcTime) return;
    this.npcTime = t + 5000;
    this.session.send(ClientPackets.CallNPC({ ObjectID: this.npcId, Key: `[${key}]` }));
  }

  /** NPCDialog.Hide: every NPC panel goes with it (the others chain onto onClose). */
  close(): void {
    if (this.win.visible) this.invWindow.location = [0, 0]; // NPCDialog.Hide
    this.win.hide();
    this.goods.hide();
    this.subGoods.hide();
    this.drop.hide();
    this.bigButtonDialog.hide();
    this.hideTooltip();
    this.onClose();
  }

  /** NPCDialog.Hide also hides the other NPC panels (refine, awakening, craft, storage, market, quests): they chain onto this. */
  onClose: () => void = () => {};

  handle(p: ServerPacket): void {
    switch (p.type) {
      // S.MapInformation / S.MapChanged: MapControl.Index / Title are in CurrentMap
      case "NPCResponse":
        // GameScene.NPCResponse
        this.npcTime = 0;
        this.goods.hide();
        this.subGoods.hide();
        this.drop.hide();
        this.showPage(p.Page);
        break;
      case "NPCUpdate": this.npcId = p.NPCID; break;
      case "NPCGoods":
        // GameScene.NPCGoods (PanelType.Craft is npcServices.ts)
        this.rate = p.Rate;
        this.hideAdded = p.HideAddedStats;
        if (!this.visible) break;
        if (p.Type === PanelType.Buy) { this.goods.usePearls = false; this.goods.newGoods(p.List); this.goods.show(); }
        else if (p.Type === PanelType.BuySub) { this.subGoods.usePearls = false; this.subGoods.newGoods(p.List); this.subGoods.show(); }
        break;
      case "NPCSell": if (this.visible) this.drop.show(PanelType.Sell); break;
      case "NPCRepair": this.rate = p.Rate; if (this.visible) this.drop.show(PanelType.Repair, p.Rate); break;
      case "NPCSRepair": this.rate = p.Rate; if (this.visible) this.drop.show(PanelType.SpecialRepair, p.Rate); break;
      case "NPCConsign": if (this.visible) this.drop.show(PanelType.Consign); break; // GameScene.NPCConsign
    }
  }

  /** GameScene.NPCPearlGoods: the goods list with UsePearls (called by NpcServices). */
  showPearlGoods(list: UserItem[], rate: number): void {
    this.rate = rate;
    if (!this.visible) return;
    this.goods.usePearls = true;
    this.goods.newGoods(list);
    this.goods.show();
  }

  // — Page (NPCDialog.NewText) —

  /** GameScene.NPCResponse: BigButtons.Clear, BigButtonDialog.Hide, NewText, then Show or Hide. */
  private showPage(page: string[]): void {
    this.hideTooltip();
    this.bigButtonDialog.hide();
    const lines = [...page];
    const big: BigButtonInfo[] = [];
    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i]!;
      const found: BigButtonInfo[] = [];
      for (const m of line.matchAll(BIG)) {
        const [action, colour] = m[3]!.split("/");
        found.push({ text: m[2]!, action: action!, colour: colour || "RoyalBlue" });
      }
      big.unshift(...found);
      const rest = line.replace(BIG, "");
      if (rest.trim() === "" && rest !== line) lines.splice(i, 1);
    }
    this.currentLines = lines;
    this.bigButtons = big;
    // Visible at once (an NPCGoods / NPCSell right behind the page checks NPCDialog.Visible); the layout needs the
    // picture sizes, which only the very first page may have to wait for.
    const seq = ++this.pageSeq;
    const layout = () => { if (seq === this.pageSeq) this.newText(true); };
    if (this.sizesLoaded) layout(); else void this.sizesReady.then(layout);
    if (lines.length > 0 || big.length > 0) this.show();
    else this.close();
  }

  /** NPCDialog.Show. */
  private show(): void {
    this.invWindow.location = [this.win.size[0] + 5, 0]; // NPCDialog.Show: InventoryDialog beside it
    this.win.show();
    this.refreshQuestButton();
  }

  /** NPCDialog.NewText: the BigButtonDialog (on a new page), the background, the scroll buttons and the visible rows. */
  private newText(resetIndex: boolean): void {
    const lines = this.currentLines;
    if (resetIndex) {
      this.index = 0;
      let height = this.size(Lib.Prguse, this.win.index)[1]; // Size = TrueSize
      this.updatePositionBar();
      if (this.bigButtons.length > 0) {
        let minimumButtons = 0;
        let at: [number, number];
        if (lines.join("").trim() === "") { at = [1, 27]; minimumButtons = 4; }
        else at = [1, height - 33];
        this.bigButtonDialog.control.location = at;
        this.bigButtonDialog.show(this.bigButtons, minimumButtons, (a) => this.action(a), (i) => this.size(Lib.Title, i));
        height = at[1] + this.bigButtonDialog.height;
      }
      this.height = height;
    }
    const scroll = lines.length > MAXIMUM_LINES;
    this.win.index = scroll ? 385 : 384;
    this.upButton.visible = this.downButton.visible = this.positionBar.visible = scroll;
    this.questButton.location = [172, this.height - 30];

    for (const c of this.textControls) c.dispose();
    this.textControls = [];
    this.links = [];
    const lastLine = scroll ? Math.min(MAXIMUM_LINES + this.index, lines.length) : lines.length;
    for (let i = this.index; i < lastLine; i++) {
      // TextLabel[i]: 420 x 20 at (8, 34 + row * 18), 9pt, WordBreak; the links draw over it
      const label = new MirControl({ parent: this.win, location: [8, 34 + (i - this.index) * 18], size: [420, 20], notControl: true, className: "mir-label" });
      label.el.style.font = FONT_9;
      this.renderLine(label.el, lines[i]!);
      this.textControls.push(label);
    }
    // keep the scroll / close buttons, the big buttons and the quest button above the rows
    for (const c of [this.upButton, this.downButton, this.positionBar, this.bigButtonDialog.control, this.questButton]) this.win.el.append(c.el);
  }

  /** NewButton / NewColour / NewLink inline on a row. */
  private renderLine(target: HTMLElement, line: string): void {
    let at = 0;
    for (const m of line.matchAll(INLINE)) {
      if (m.index! > at) target.append(line.slice(at, m.index));
      at = m.index! + m[0].length;
      if (m[1] !== undefined) {
        // NewButton: yellow, red while hovered, Sound ButtonC
        const slash = m[1].indexOf("/@");
        const text = m[1].slice(0, slash), key = m[1].slice(slash + 1);
        target.append(this.textButton(text, () => this.action(key)));
      } else if (m[2] !== undefined) {
        // NewColour: Color.FromName
        const span = document.createElement("span");
        span.textContent = m[2];
        span.style.color = CSS.supports("color", m[3]!) ? m[3]! : "transparent";
        target.append(span);
      } else if (m[4] !== undefined) {
        // NewButton with a link: opens http(s) addresses
        const url = m[5]!;
        target.append(this.textButton(m[4], () => { if (/^https?:\/\//i.test(url)) window.open(url, "_blank", "noopener,noreferrer"); }));
      } else {
        // NewLink: cyan, orange on hover, tooltip on enter, hidden on leave / click
        const kind = (m[6] ?? m[9])!.toUpperCase() as LinkKind, idx = Number(m[7] ?? m[10]), name = m[8];
        const span = document.createElement("span");
        span.textContent = this.linkName(kind, idx, name);
        Object.assign(span.style, { color: "#00ffff", pointerEvents: "auto" });
        const link: PageLink = { span, kind, idx, name };
        span.addEventListener("pointerenter", () => { span.style.color = "#ffa500"; this.showTooltip(link); });
        span.addEventListener("pointerleave", () => { span.style.color = "#00ffff"; this.hideTooltip(); });
        span.addEventListener("click", (e) => { e.stopPropagation(); this.hideTooltip(); });
        this.links.push(link);
        target.append(span);
      }
    }
    if (at < line.length) target.append(line.slice(at));
  }

  /** NPCDialog.NewButton: a yellow label, red under the mouse, yellow while pressed. */
  private textButton(text: string, onClick: () => void): HTMLSpanElement {
    const span = document.createElement("span");
    span.textContent = text;
    Object.assign(span.style, { color: "#ffff00", pointerEvents: "auto" });
    span.addEventListener("pointerenter", () => { span.style.color = "#ff0000"; });
    span.addEventListener("pointerleave", () => { span.style.color = "#ffff00"; });
    span.addEventListener("pointerdown", () => { span.style.color = "#ffff00"; });
    span.addEventListener("pointerup", () => { span.style.color = "#ff0000"; });
    span.addEventListener("click", (e) => { e.stopPropagation(); uiSound(SoundList.ButtonC); onClick(); });
    return span;
  }

  /** NPCDialog_MouseWheel. */
  private mouseWheel(count: number): void {
    const n = this.currentLines.length;
    if (this.index === 0 && count >= 0) return;
    if (this.index === n - 1 && count <= 0) return;
    if (n <= MAXIMUM_LINES) return;
    this.index -= count;
    if (this.index < 0) this.index = 0;
    if (this.index + MAXIMUM_LINES > n - 1) this.index = n - MAXIMUM_LINES;
    this.newText(false);
    this.updatePositionBar();
  }

  /** NPCDialog.PositionBar_OnMoving. */
  private positionBarMoving(y: number): void {
    if (y >= 155) y = 155;
    if (y <= 47) y = 47;
    const extra = this.currentLines.length - MAXIMUM_LINES;
    if (extra <= 0) return;
    const interval = Math.max(1, Math.trunc(108 / extra));
    this.index = Math.min(extra, Math.trunc((y - 47) / interval));
    this.newText(false);
    this.positionBar.location = [417, y];
  }

  /** NPCDialog.UpdatePositionBar. */
  private updatePositionBar(): void {
    const extra = this.currentLines.length - MAXIMUM_LINES;
    if (extra <= 0) return;
    const interval = Math.trunc(108 / extra);
    let y = 48 + this.index * interval;
    if (y >= 155) y = 155;
    if (y <= 47) y = 47;
    this.positionBar.location = [417, y];
  }

  // — Info links (NPCDialog.GetDisplayNameForLink / ShowTooltip / HideTooltip) —

  /** NPCDialog.GetDisplayNameForLink (requests a missing info). */
  private linkName(kind: LinkKind, idx: number, provided: string | undefined): string {
    if (provided) return provided;
    switch (kind) {
      case "ITEM": {
        const item = this.gameInfo.item(idx);
        if (item) return itemFriendlyName(item) || item.Name || `Item ${idx}`;
        this.gameInfo.requestItem(idx);
        return `Item ${idx}`;
      }
      case "MONSTER": {
        const monster = this.gameInfo.monster(idx);
        if (monster) return monster.GameName || monster.Name;
        this.gameInfo.requestMonster(idx);
        return `Monster ${idx}`;
      }
      case "NPC": {
        const npc = this.gameInfo.npc(idx);
        if (npc) return npc.Name;
        this.gameInfo.requestNPC(idx);
        return `NPC ${idx}`;
      }
    }
  }

  private infoArrived(kind: InfoKind, index: number): void {
    if (kind === "map") { if (this.tooltipLink?.kind === "NPC") this.showTooltip(this.tooltipLink); return; }
    const want: LinkKind | null = kind === "monster" ? "MONSTER" : kind === "npc" ? "NPC" : kind === "item" ? "ITEM" : null;
    if (!want) return;
    for (const link of this.links) {
      if (link.kind !== want || link.idx !== index) continue;
      if (!link.name) link.span.textContent = this.linkName(link.kind, link.idx, undefined);
      if (link === this.tooltipLink) this.showTooltip(link);
    }
  }

  /** NPCDialog.ShowTooltip: image (monster frame 20, NPC frame 0, item Items_Tooltip_32bit else Items) + title + info
   *  lines, at the link's DisplayLocation + 20; at least 200 wide. */
  private showTooltip(link: PageLink): void {
    this.tooltipLink = link;
    const { kind, idx } = link;
    let title = "", content = "";
    let image: { lib: string; index: number; fallback?: string } | null = null;
    switch (kind) {
      case "MONSTER": {
        const monster = this.gameInfo.monster(idx);
        if (monster) {
          title = monster.GameName || monster.Name;
          content = this.monsterInfo(idx);
          const body = monsterLibPath(monster.Image); // NPCDialog uses Monsters[Image]; the web helper also covers Pet / Gate / Siege / Dragon
          image = body === null ? null : { lib: body, index: 20 };
        } else {
          this.gameInfo.requestMonster(idx);
          title = T.MonsterIndexTitle(idx);
          content = T.MonsterInfoLoading;
        }
        break;
      }
      case "NPC": {
        const npc = this.gameInfo.npc(idx);
        if (npc) {
          title = npc.Name;
          content = this.npcInfo(idx);
          image = { lib: LibPath.npc(npc.Image), index: 0 };
        } else {
          this.gameInfo.requestNPC(idx);
          title = T.NPCIndexTitle(idx);
          content = T.NPCInfoLoading;
        }
        break;
      }
      case "ITEM": {
        const item = this.gameInfo.item(idx);
        if (item) {
          title = itemFriendlyName(item) || item.Name;
          content = this.itemInfo(idx);
          image = { lib: "Items_Tooltip_32bit", index: item.Image, fallback: Lib.Items };
        } else {
          this.gameInfo.requestItem(idx);
          title = T.ItemIndexTitle(idx);
          content = T.ItemInfoLoading;
        }
        break;
      }
    }
    this.tooltipTitle.text = title;
    this.tooltipContent.text = content;
    const anchor = stagePoint(link.span);
    this.tooltip.location = [anchor[0] + 20, anchor[1] + 20];
    this.tooltip.visible = true;
    this.tooltip.el.parentElement?.append(this.tooltip.el); // BringToFront
    const seq = ++this.tooltipSeq;
    const layout = (pic: { width: number; height: number } | null) => {
      if (seq !== this.tooltipSeq) return;
      const iw = pic?.width ?? 0, ih = pic?.height ?? 0;
      const offset = pic ? iw + 20 : 10;
      this.tooltipImage.visible = !!pic;
      this.tooltipTitle.location = [offset, 5];
      this.tooltipContent.location = [offset, 27];
      const tw = this.tooltipTitle.el.offsetWidth, th = this.tooltipTitle.el.offsetHeight;
      const cw = this.tooltipContent.el.offsetWidth, ch = this.tooltipContent.el.offsetHeight;
      const contentHeight = th + ch + 30;
      this.tooltip.size = [Math.max(200, Math.max(tw, cw) + offset + 20), Math.max(pic ? ih + 20 : contentHeight, contentHeight)];
    };
    if (!image) { layout(null); return; }
    const { lib, index, fallback } = image;
    layout(null);
    void libPicture(lib, index)
      .then((pic) => pic ? { lib, pic } : fallback ? libPicture(fallback, index).then((p) => p ? { lib: fallback, pic: p } : null) : null)
      .then((found) => {
        if (seq !== this.tooltipSeq || !found) return;
        this.tooltipImage.library = found.lib;
        this.tooltipImage.index = index;
        layout(found.pic);
      });
  }

  /** NPCDialog.HideTooltip. */
  private hideTooltip(): void {
    this.tooltip.visible = false;
    this.tooltipLink = null;
    this.tooltipSeq++;
    hideHint();
  }

  /** NPCDialog.GetMonsterInfo. */
  private monsterInfo(idx: number): string {
    const m = this.gameInfo.monster(idx);
    if (!m) { this.gameInfo.requestMonster(idx); return `Monster Index: ${idx}\n(Loading...)`; }
    const st = (s: number) => m.Stats[s] ?? 0;
    return [
      `Level: ${m.Level}`, `Health: ${st(Stat.HP)}`, `Experience: ${m.Experience}`,
      `DC: ${st(Stat.MinDC)}~${st(Stat.MaxDC)}`, `MC: ${st(Stat.MinMC)}~${st(Stat.MaxMC)}`,
      `AC: ${st(Stat.MinAC)}~${st(Stat.MaxAC)}`, `MAC: ${st(Stat.MinMAC)}~${st(Stat.MaxMAC)}`,
    ].join("\n");
  }

  /** NPCDialog.GetNPCInfo: the map title comes from the user's map or MapInfoList, else C.RequestMapInfo once. */
  private npcInfo(idx: number): string {
    const npc = this.gameInfo.npc(idx);
    if (!npc) { this.gameInfo.requestNPC(idx); return `NPC Index: ${idx}\n(Loading...)`; }
    const lines = [`Name: ${npc.Name}`];
    const mapName = this.mapIndex === npc.MapIndex ? this.mapTitle : npc.MapIndex > 0 ? this.gameInfo.map(npc.MapIndex)?.Title ?? "" : "";
    if (mapName.trim()) lines.push(`Location: ${mapName}`);
    else if (npc.MapIndex > 0) { this.gameInfo.requestMap(npc.MapIndex); lines.push("Location: Loading map..."); }
    else lines.push("Location: Unknown map");
    lines.push(`Coordinates: (${npc.Location.X}, ${npc.Location.Y})`);
    return lines.join("\n");
  }

  /** NPCDialog.GetItemInfo (ItemType / RequiredClass names stand in for ToLocalizedString). */
  private itemInfo(idx: number): string {
    const item = this.gameInfo.item(idx);
    if (!item) { this.gameInfo.requestItem(idx); return `Item Index: ${idx}\n(Loading...)`; }
    const lines: string[] = [];
    const type = ItemTypeName[item.Type];
    if (type) lines.push(`Type: ${type}`);
    if (item.RequiredType === RequiredType.Level && item.RequiredAmount > 0) lines.push(`Required Level : ${item.RequiredAmount}`);
    if (item.ClassBased) lines.push(`Class Required : ${requiredClassText(item.RequiredClass)}`);
    if (item.Weight > 0) lines.push(`Weight: ${item.Weight}`);
    return lines.join("\n");
  }
}
