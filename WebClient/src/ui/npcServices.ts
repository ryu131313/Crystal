// NPC item services with the desktop artwork (Client/MirScenes/Dialogs/NPCDialogs.cs + GameScene handlers):
//   NPCDropDialog types Refine, CheckRefine, ReplaceWedRing, Disassemble, Downgrade, Reset: the shared panel is
//                 NpcWindow.drop (npcWindow.ts); this file opens it from the server packets.
//   RefineDialog: Prguse 1002 at (0, 225), Title 18, the 4 x 4 refine grid (items live in Inventory.refine, cells share
//                 the inventory pick); hiding it sends C.RefineCancel and the server hands the items back
//                 (S.RetrieveRefineItem).
//   NPCAwakeDialog: Title 710 (movable), base item cell, the glyph MirDropDownBox, the server's material list
//                 (S.AwakeningNeedMaterials) in cells 1-2 with their labels, material cells 3-4, extra cells 5-6, the gold
//                 label, Upgrade (C.Awakening); S.AwakeningLockedItem / S.Awakening answers.
//   NPCCraftGoodsDialog + CraftDialog: S.NPCGoods with PanelType.Craft lists recipes (S.NewRecipeInfo) in the goods list
//                 (npcWindow.ts GoodsDialog); CraftDialog (Prguse 1109, movable) shows 3 tool + 6 ingredient cells with the
//                 recipe's shadow items; pick tools / ingredients into them (or Auto Fill), Craft sends C.CraftItem with the
//                 inventory slots.
//   NPCGoodsDialog with UsePearls: S.NPCPearlGoods is the NPC session's Buy list (the desktop reuses the goods list).
// The state is in src/game: the NPC session (npcSession.ts: the drop panel, the goods lists, NPCRate, the refine /
// craft opens), the recipes (recipes.ts) and the awakening (awakening.ts); these windows are their views.
// Deliberate differences: awakening cells take items picked from the bag (the desktop also swaps between its cells);
// the needed-material cells show the item name as a hint instead of the full item label.

import type { ClientRecipeInfo, ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, PanelType, Stat } from "../protocol/enums.generated.ts";
import { REFINE_SLOTS, type Inventory } from "../game/inventory.ts";
import type { ItemInfos } from "../game/items.ts";
import type { NpcSession, NpcGoods } from "../game/npcSession.ts";
import type { Recipes } from "../game/recipes.ts";
import { AWAKE_CELLS, type Awakening } from "../game/awakening.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { GoodsDialog, type NpcWindow } from "./npcWindow.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, closeButton, setPicture, libSize } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { showItemLabel, moveItemLabel, hideItemLabel } from "./itemLabel.ts";

export interface NpcServicesOptions {
  stage: HTMLElement;
  send: (frame: Uint8Array) => void;
  inv: Inventory;
  infos: ItemInfos;
  invWindow: InventoryWindow;
  /** The NPC dialog (NPCDialog.Size.Height for the craft list). */
  npc: NpcWindow;
  /** The NPC session (NPCDialog.Visible / Hide, the drop panel, the goods lists, NPCRate). */
  session: NpcSession;
  /** GameScene.RecipeInfoList. */
  recipes: Recipes;
  /** NPCAwakeDialog's state. */
  awakening: Awakening;
  chat: (text: string, type: number) => void;
  /** UserObject.Stats[stat] (CraftDialog's Stat.CraftRatePercent). */
  stat?: (stat: number) => number;
}

const TOOL_COUNT = 3, INGREDIENT_COUNT = 6; // CraftDialog._toolCount / _ingredientCount

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  YouDoNotHaveEnoughGold: "You do not have enough gold.", CraftAmount: "Craft Amount:",
  YouDoNotHaveRequiredToolsOrIngredients: "You do not have the required tools or ingredients.",
};

/** The MirItemCell look for a cell this file draws itself (awakening / craft): 36 x 32 (or `size`), the item centred,
 *  the count label bottom-right, the lime border while hovered (or always with `border`). */
class ItemView {
  readonly control: MirControl;
  private readonly back = document.createElement("div");
  private readonly icon = document.createElement("div");
  private readonly count: MirLabel;
  private hovered = false;
  private alwaysBorder: boolean;
  private item: UserItem | null = null;
  private labelOpts: { hideDura?: boolean } = {};

  constructor(parent: MirControl, location: readonly [number, number], invWindow: InventoryWindow, onDown: () => void,
    size: readonly [number, number] = [36, 32], border = false) {
    this.control = new MirControl({ parent, location, size });
    this.alwaysBorder = border;
    this.back.className = this.icon.className = "mir-pic";
    this.control.el.append(this.back, this.icon);
    this.count = new MirLabel({ parent: this.control, autoSize: true, notControl: true, outline: false, foreColour: "#ffff00" });
    Object.assign(this.count.el.style, { left: "auto", top: "auto", right: "0px", bottom: "0px" });
    const el = this.control.el;
    el.addEventListener("contextmenu", (e) => e.preventDefault());
    el.addEventListener("mousedown", (e) => { e.stopPropagation(); if (e.button === 0) { hideItemLabel(); onDown(); } });
    el.addEventListener("pointerenter", (e) => {
      this.hovered = true;
      this.drawBorder();
      if (this.item) showItemLabel(this.item, invWindow.labelContext(), e.clientX, e.clientY, this.labelOpts);
    });
    el.addEventListener("pointermove", (e) => moveItemLabel(e.clientX, e.clientY));
    el.addEventListener("pointerleave", () => { this.hovered = false; this.drawBorder(); hideItemLabel(); });
    this.drawBorder();
  }

  private drawBorder(): void {
    this.control.el.style.outline = this.alwaysBorder || this.hovered ? "1px solid #00ff00" : "";
  }

  /** Draws Items[image] centred (dim = DimGray at 0.8), the craft background (Prguse 1121 at (-2, -1), 0.8) and the
   *  count text / colour; `label` is the item for the hover label. */
  set(image: number, dim: boolean, countText: string, countColour: string, label: UserItem | null, craftBack = false, labelOpts: { hideDura?: boolean } = {}): void {
    this.item = label;
    this.labelOpts = labelOpts;
    const [w, h] = this.control.size;
    if (this.icon.dataset.pic !== (image >= 0 ? `${Lib.Items}#${image}` : "")) {
      void setPicture(this.icon, image >= 0 ? Lib.Items : null, image).then((pic) => {
        if (!pic) return;
        this.icon.style.left = Math.trunc((w - pic.width) / 2) + "px";
        this.icon.style.top = Math.trunc((h - pic.height) / 2) + "px";
      });
    }
    this.icon.style.filter = dim ? "brightness(0.41)" : "";
    this.icon.style.opacity = dim ? "0.8" : "";
    void setPicture(this.back, craftBack && image >= 0 ? Lib.Prguse : null, 1121).then(() => {
      this.back.style.left = "-2px";
      this.back.style.top = "-1px";
    });
    this.back.style.opacity = "0.8";
    this.count.text = countText;
    this.count.visible = countText !== "";
    this.count.foreColour = countColour;
  }
}

/** MirDropDownBox (NPCAwakeDialog.SelectAwakeType): a dark box with the selected text, a Prguse2 207 drop button and
 *  up to 5 option rows (hover brown) under it. */
class DropDownBox {
  readonly control: MirControl;
  private readonly label: MirLabel;
  private readonly options: MirLabel[] = [];
  private items: readonly string[] = [];
  selectedIndex = -1;
  onChange: ((index: number) => void) | null = null;

  constructor(parent: MirControl, location: readonly [number, number], size: readonly [number, number]) {
    this.control = new MirControl({ parent, location, size, backColour: "rgb(6, 6, 6)" });
    this.label = new MirLabel({ parent: this.control, location: [0, 0], size: [size[0] - 16, 15], backColour: "rgb(6, 6, 6)" });
    this.label.onClick(() => this.toggle());
    new MirButton({ parent: this.control, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [size[0] - 18, 2], onClick: () => this.toggle() });
    for (let i = 0; i < 5; i++) {
      const o = new MirLabel({ parent: this.control, location: [0, 15 + i * 13], size: [size[0] - 16, 16], backColour: "rgb(20, 20, 20)", visible: false });
      o.el.addEventListener("pointerenter", () => { o.el.style.backgroundColor = "rgb(140, 70, 0)"; });
      o.el.addEventListener("pointerleave", () => { o.el.style.backgroundColor = "rgb(20, 20, 20)"; });
      o.onClick(() => { this.close(); if (i < this.items.length) this.onChange?.(i); });
      this.options.push(o);
    }
    this.control.el.addEventListener("pointerleave", () => this.close());
  }

  setItems(items: readonly string[], selected: number): void {
    this.items = items;
    this.selectedIndex = selected;
    this.close();
    this.label.text = items.length === 0 ? "None" : selected < 0 ? " " : items[selected] ?? " ";
  }

  private toggle(): void {
    if (this.options[0]!.visible) { this.close(); return; }
    // OpenDropDown
    this.options.forEach((o, i) => { o.visible = i < this.items.length; o.text = this.items[i] ?? ""; });
    this.control.el.parentElement?.append(this.control.el);
  }

  private close(): void {
    for (const o of this.options) o.visible = false;
  }
}

export class NpcServices {
  private readonly o: Required<NpcServicesOptions>;

  // RefineDialog
  private readonly refine: MirWindow;

  // NPCAwakeDialog (state: Awakening)
  private readonly awake: MirWindow;
  private readonly awakeViews: ItemView[] = [];
  private readonly awakeType: DropDownBox;
  private readonly goldLabel: MirLabel;
  private readonly needLabels: MirLabel[];

  // NPCCraftGoodsDialog + CraftDialog (recipes: Recipes; the list: the session's PanelType.Craft goods)
  private readonly craftGoods: GoodsDialog;
  private readonly craft: MirWindow;
  private readonly craftViews: ItemView[] = [];
  private readonly recipeLabel: MirLabel;
  private readonly possibilityLabel: MirLabel;
  private readonly craftGoldLabel: MirLabel;
  private readonly craftButton: MirButton;
  private recipeItem: UserItem | null = null; // CraftDialog.RecipeItem
  /** CraftDialog.Selected: craft cell -> the inventory slot + UniqueID put there (insertion order = C.CraftItem.Slots). */
  private readonly selected = new Map<number, { slot: number; id: bigint }>();

  constructor(options: NpcServicesOptions) {
    this.o = { stat: () => 0, ...options };
    const { invWindow, npc, session, recipes, awakening } = this.o;

    // RefineDialog ctor
    this.refine = new MirWindow({ library: Lib.Prguse, index: 1002, location: [0, 225], sort: true });
    new MirImage({ parent: this.refine, library: Lib.Title, index: 18, location: [28, 8] });
    for (let x = 0; x < 4; x++) {
      for (let y = 0; y < 4; y++) {
        const idx = 4 * y + x;
        if (idx < REFINE_SLOTS) placeCell(invWindow.boundCell("refine", idx, [34, 32]), this.refine, [x * 34 + 12 + x, y * 32 + 37 + y]);
      }
    }

    // NPCAwakeDialog ctor (Location = (0, NPCDialog.Size.Height) when GameScene builds it)
    this.awake = new MirWindow({ library: Lib.Title, index: 710, location: [0, 0], movable: true, sort: true });
    void libSize(Lib.Prguse, 995).then(([, h]) => { if (!this.awake.visible) this.awake.location = [0, h]; });
    this.goldLabel = new MirLabel({ parent: this.awake, autoSize: true, location: [112, 354], notControl: true });
    this.needLabels = [
      new MirLabel({ parent: this.awake, autoSize: true, location: [67, 317], notControl: true }),
      new MirLabel({ parent: this.awake, autoSize: true, location: [192, 317], notControl: true }),
    ];
    new MirButton({ parent: this.awake, library: Lib.Title, index: 712, hoverIndex: 713, pressedIndex: 714, location: [115, 391], sound: SoundList.ButtonA, onClick: () => awakening.awaken() });
    closeButton(this.awake, [284, 4], () => awakening.hide());
    const cellAt: [number, number][] = [[202, 91], [31, 316], [155, 316], [175, 199], [230, 199], [175, 256], [230, 256]];
    cellAt.forEach((at, i) => this.awakeViews.push(new ItemView(this.awake, at, invWindow, () => this.awakeCellClick(i))));
    this.awakeType = new DropDownBox(this.awake, [35, 141], [109, 14]);
    this.awakeType.onChange = (i) => { awakening.selectType(i); this.render(); };

    // NPCCraftGoodsDialog (NPCGoodsDialog with PanelType.Craft)
    this.craftGoods = new GoodsDialog(PanelType.Craft, {
      inv: this.o.inv, infos: this.o.infos, invWindow, npc: session, chat: (t) => this.system(t), rate: () => session.craftRate,
      hideAdded: () => false, npcHeight: () => npc.dialogHeight,
      recipeGold: (itemIndex) => recipes.gold(itemIndex),
    });
    this.craftGoods.onSelect = (item) => this.selectRecipe(item);
    this.craftGoods.onHide = () => this.hideCraft();

    // CraftDialog ctor
    this.craft = new MirWindow({ library: Lib.Prguse, index: 1109, location: [0, 0], movable: true, sort: true });
    this.recipeLabel = new MirLabel({ parent: this.craft, autoSize: true, location: [22, 5], font: "bold 10pt Arial", foreColour: "#deb887", notControl: true });
    this.possibilityLabel = new MirLabel({ parent: this.craft, autoSize: true, location: [10, 135], notControl: true });
    this.craftGoldLabel = new MirLabel({ parent: this.craft, autoSize: true, location: [30, 190], notControl: true });
    for (let x = 0; x < TOOL_COUNT + INGREDIENT_COUNT; x++) {
      const at: [number, number] = x >= TOOL_COUNT ? [(x - TOOL_COUNT) * 40 + 52, 86] : [x * 44 + 108, 44];
      this.craftViews.push(new ItemView(this.craft, at, invWindow, () => this.craftCellClick(x), [35, 32], true));
    }
    closeButton(this.craft, [312, 3], () => this.hideCraft());
    new MirButton({ parent: this.craft, library: Lib.Title, index: 180, hoverIndex: 181, pressedIndex: 182, location: [165, 185], sound: SoundList.ButtonA, onClick: () => this.autoFill() });
    this.craftButton = new MirButton({ parent: this.craft, library: Lib.Title, index: 336, hoverIndex: 337, pressedIndex: 338, location: [215, 185], sound: SoundList.ButtonA, grayScale: true, enabled: false,
      onClick: () => this.craftItem() });

    const offs = [
      // NPCDialog.Hide hides every NPC panel
      session.closed.on(() => this.closeAll()),
      // GameScene.NPCGoods, PanelType.Craft (Buy / BuySub are npcWindow.ts)
      session.goods.on((g) => this.onGoods(g)),
      // GameScene.NPCRefine (the drop panel showed first): RefineDialog.Show()
      session.refine.on(() => this.showRefine()),
      // MirScene.NewRecipeInfo: the craft list's prices
      recipes.changed.on(() => this.craftGoods.refresh()),
      // NPCAwakeDialog
      awakening.shown.on(() => { this.awake.show(); this.o.invWindow.toggle(true); this.render(); }),
      awakening.hidden.on(() => { this.awake.hide(); hideItemLabel(); }),
      awakening.types.on(() => this.awakeType.setItems(awakening.typeTexts, awakening.selected)),
      awakening.changed.on(() => this.render()),
      awakening.result.on(({ text }) => { messageBox(this.o.stage, text); }),
    ];
    // Cells share the inventory window's pick: redraw with it (CraftDialog.BeforeDraw also hides the craft dialog with the
    // bag); a view concern, so it stays an onRender chain
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    // GameScene Escape: NPCAwakeDialog.Hide + RefineDialog.Hide
    window.addEventListener("keydown", (e) => {
      if (!matches(e, KeybindOptions.Closeall)) return;
      awakening.hide();
      this.hideRefine();
    }, { signal });
    signal.addEventListener("abort", () => { for (const off of offs) off(); this.refine.dispose(); this.awake.dispose(); this.craft.dispose(); });
    this.awakeType.setItems(awakening.typeTexts, awakening.selected); // NPCAwakeDialog ctor: ItemCell_Click
  }

  private get inv(): Inventory { return this.o.inv; }
  private info(item: UserItem): ItemInfo | undefined { return this.o.infos.get(item.ItemIndex); }
  private system(text: string): void { this.o.chat(text, ChatType.System); }
  private name(item: UserItem): string {
    const info = this.info(item);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }
  private setLock(id: bigint, on: boolean): void {
    if (on) this.inv.locked.add(String(id)); else this.inv.locked.delete(String(id));
  }

  /** The packets these windows still react to themselves (the NPC session, Recipes and Awakening models have the rest;
   *  Inventory.handle has already run for the item packets). */
  handle(p: ServerPacket): void {
    switch (p.type) {
      case "NPCResponse": this.closeAll(); break; // GameScene.NPCResponse hides the panels
      case "RefineCancel": this.render(); break;
      case "CraftItem": if (p.Success) this.updateCraftCells(); break; // GameScene.CraftItem
    }
  }

  /** GameScene.NPCGoods, PanelType.Craft: NPCCraftGoodsDialog + CraftDialog. */
  private onGoods(g: NpcGoods): void {
    if (g.type !== PanelType.Craft) return;
    this.craftGoods.usePearls = false;
    this.craftGoods.newGoods(g.list);
    this.craftGoods.show();
    this.showCraft();
  }

  /** NPCDialog.Hide / GameScene.NPCResponse: every panel here (the drop panel is NpcWindow's). */
  private closeAll(): void {
    this.craftGoods.hide();
    this.hideCraft();
    this.o.awakening.hide();
    this.hideRefine();
  }

  // — RefineDialog —

  private showRefine(): void {
    this.refine.show();
  }

  /** RefineDialog.Hide: C.RefineCancel (the server returns the items with S.RetrieveRefineItem). */
  private hideRefine(): void {
    if (!this.refine.visible) return;
    this.refine.hide();
    this.o.send(ClientPackets.RefineCancel());
  }

  // — NPCAwakeDialog (state: Awakening) —

  /** MirItemCell "To Awakening" with the picked inventory slot; a filled cell clicked with nothing picked goes back. */
  private awakeCellClick(slot: number): void {
    const from = this.o.invWindow.pickedInventorySlot;
    if (from < 0) { this.o.awakening.takeBack(slot); return; }
    this.o.invWindow.clearPick();
    this.o.awakening.put(slot, from);
  }

  /** The awakening cells (MirItemCell GridType AwakenItem). */
  private renderAwake(): void {
    const awakening = this.o.awakening;
    for (let i = 0; i < 2; i++) this.needLabels[i]!.text = awakening.needText(i);
    this.goldLabel.text = awakening.gold === null ? "" : String(awakening.gold);
    for (let slot = 0; slot < AWAKE_CELLS; slot++) {
      const view = this.awakeViews[slot]!;
      if (slot === 1 || slot === 2) {
        const need = awakening.materials[slot - 1];
        view.set(need ? need.info.Image : -1, false, need && need.info.StackSize > 1 ? String(need.count) : "", "#ffff00", null);
        view.control.hint = need ? need.info.Name : "";
        continue;
      }
      const item = awakening.items[slot] ?? null;
      const info = item ? this.info(item) : undefined;
      view.set(info ? info.Image : -1, false, item && info && info.StackSize > 1 ? String(item.Count) : "", "#ffff00", item);
    }
  }

  // — NPCCraftGoodsDialog + CraftDialog —

  /** CraftDialog.Show: under the inventory dialog. */
  private showCraft(): void {
    const root = this.o.invWindow.root;
    this.craft.location = [(parseInt(root.style.left) || 0) - 12, (parseInt(root.style.top) || 0) + 236];
    this.craft.show();
    this.render();
  }

  /** CraftDialog.Hide -> ResetCells. */
  private hideCraft(): void {
    if (!this.craft.visible) return;
    this.craft.hide();
    hideItemLabel();
    this.resetCells();
  }

  /** CraftDialog.ResetCells: the selected inventory cells unlock. */
  private resetCells(clearRecipe = true): void {
    if (clearRecipe) this.recipeItem = null;
    for (const { id } of this.selected.values()) this.setLock(id, false);
    const had = this.selected.size > 0;
    this.selected.clear();
    if (had) this.inv.changed();
  }

  private get recipe(): ClientRecipeInfo | undefined {
    return this.recipeItem ? this.o.recipes.get(this.recipeItem.ItemIndex) : undefined;
  }

  /** CraftDialog.ShadowItems[i]: tools in 0..2, ingredients in 3..8. */
  private shadow(i: number): UserItem | null {
    const r = this.recipe;
    if (!r) return null;
    return (i < TOOL_COUNT ? r.Tools[i] : r.Ingredients[i - TOOL_COUNT]) ?? null;
  }

  /** The inventory item in craft cell `i` (CraftDialog.Slots), if it is still the one put there. */
  private craftCellItem(i: number): UserItem | null {
    const s = this.selected.get(i);
    if (!s) return null;
    const item = this.inv.inventory[s.slot];
    return item && item.UniqueID === s.id ? item : null;
  }

  /** NPCCraftGoodsDialog cell click: ResetCells + RefreshCraftCells, CraftDialog.Show. */
  private selectRecipe(item: UserItem): void {
    this.resetCells();
    this.recipeItem = item;
    if (!this.craft.visible) this.showCraft(); else this.render();
  }

  /** CraftDialog.Grid_Click with the picked inventory slot. */
  private craftCellClick(i: number): void {
    const shadow = this.shadow(i);
    if (!shadow) return;
    const from = this.o.invWindow.pickedInventorySlot;
    if (from < 0) return;
    const item = this.inv.inventory[from];
    if (!item || this.inv.isLocked(item)) return;
    if (item.ItemIndex !== shadow.ItemIndex || this.selected.has(i)) return;
    if (i >= TOOL_COUNT) {
      if (item.Count < shadow.Count) return;
      if (shadow.CurrentDura < shadow.MaxDura && item.CurrentDura < shadow.CurrentDura) return;
    } else if (item.CurrentDura < 1000) return;
    this.o.invWindow.clearPick();
    this.selected.set(i, { slot: from, id: item.UniqueID });
    this.setLock(item.UniqueID, true);
    this.inv.changed();
  }

  /** CraftDialog.AutoFill. */
  private autoFill(): void {
    this.resetCells(false);
    const r = this.recipe;
    if (!r) return;
    const inv = this.inv.inventory;
    r.Tools.forEach((tool, j) => {
      for (let i = 0; i < inv.length; i++) {
        const slot = inv[i];
        if (!slot || slot.ItemIndex !== tool.ItemIndex || slot.CurrentDura < 1000 || this.inv.isLocked(slot)) continue;
        this.selected.set(j, { slot: i, id: slot.UniqueID });
        this.setLock(slot.UniqueID, true);
        break;
      }
    });
    r.Ingredients.forEach((ingredient, k) => {
      for (let i = 0; i < inv.length; i++) {
        const slot = inv[i];
        if (!slot || slot.ItemIndex !== ingredient.ItemIndex || slot.Count < ingredient.Count) continue;
        if (ingredient.CurrentDura < ingredient.MaxDura && slot.CurrentDura < ingredient.CurrentDura) continue;
        if (this.inv.isLocked(slot)) continue;
        this.selected.set(TOOL_COUNT + k, { slot: i, id: slot.UniqueID });
        this.setLock(slot.UniqueID, true);
        break;
      }
    });
    this.inv.changed();
  }

  /** CraftDialog.HasCraftItems. */
  private hasCraftItems(count: number): boolean {
    for (let i = 0; i < TOOL_COUNT + INGREDIENT_COUNT; i++) {
      const shadow = this.shadow(i);
      if (!shadow) continue;
      const item = this.craftCellItem(i);
      if (!item) return false;
      if (i >= TOOL_COUNT ? item.Count < shadow.Count * count : Math.floor(item.CurrentDura / 1000) < count) return false;
    }
    return true;
  }

  /** CraftDialog.CraftItem (MirAmountBox for more than one). */
  private craftItem(): void {
    const r = this.recipe, recipeItem = this.recipeItem;
    if (!r || !recipeItem) return;
    if (this.selected.size < r.Tools.length + r.Ingredients.length) return;
    let max = 99;
    for (let i = 0; i < TOOL_COUNT + INGREDIENT_COUNT; i++) {
      const item = this.craftCellItem(i), shadow = this.shadow(i);
      if (!item) continue;
      const temp = i >= TOOL_COUNT ? Math.trunc(item.Count / Math.max(1, shadow?.Count ?? 1)) : Math.floor(item.CurrentDura / 1000);
      if (temp < max) max = temp;
    }
    const info = this.info(recipeItem);
    const perCraft = Math.trunc((info?.StackSize ?? 1) / Math.max(1, recipeItem.Count));
    if (max > perCraft) max = perCraft;
    const slots = [...this.selected.values()].map((s) => s.slot);
    if (max === 1 && r.Gold > this.inv.gold) { this.system(T.YouDoNotHaveEnoughGold); return; }
    if (max > 1) {
      amountBox(T.CraftAmount, info?.Image ?? -1, max, (amount) => {
        if (amount <= 0) return;
        if (!this.hasCraftItems(amount)) { this.system(T.YouDoNotHaveRequiredToolsOrIngredients); return; }
        if (r.Gold * amount > this.inv.gold) { this.system(T.YouDoNotHaveEnoughGold); return; }
        this.o.send(ClientPackets.CraftItem({ UniqueID: recipeItem.UniqueID, Count: amount, Slots: slots }));
      }, 0, max);
      return;
    }
    this.o.send(ClientPackets.CraftItem({ UniqueID: recipeItem.UniqueID, Count: 1, Slots: slots }));
  }

  /** CraftDialog.UpdateCraftCells: drop the selections whose item is gone, changed or worn out. */
  private updateCraftCells(): void {
    let changed = false;
    for (const [cell, s] of [...this.selected]) {
      const item = this.inv.inventory[s.slot];
      if (!item || item.UniqueID !== s.id || (item.MaxDura > 1000 && item.CurrentDura < 1000)) {
        this.setLock(s.id, false);
        this.selected.delete(cell);
        changed = true;
      }
    }
    if (changed) this.inv.changed(); else this.render();
  }

  /** CraftDialog.RefreshCraftCells + the cells' DrawControl (shadow items DimGray over Prguse 1121, "have/need" counts). */
  private renderCraft(): void {
    const r = this.recipe;
    let ready = !!r;
    for (let i = 0; i < TOOL_COUNT + INGREDIENT_COUNT; i++) {
      const view = this.craftViews[i]!;
      const item = this.craftCellItem(i), shadow = this.shadow(i);
      if (shadow && (!item || item.Count < shadow.Count)) ready = false;
      if (!shadow) {
        const info = item ? this.info(item) : undefined;
        view.set(info ? info.Image : -1, false, "", "#ffff00", item);
        continue;
      }
      const image = this.info(item ?? shadow)?.Image ?? -1;
      const short = !item || shadow.Count > item.Count;
      view.set(image, !item, `${item ? item.Count : 0}/${shadow.Count}`, short ? "#ff0000" : "#32cd32", item ?? shadow, true,
        item ? {} : { hideDura: shadow.CurrentDura === shadow.MaxDura });
    }
    this.craftButton.enabled = ready;
    this.craftButton.grayScale = !ready;
    if (r) {
      const rate = this.o.stat(Stat.CraftRatePercent);
      this.recipeLabel.text = this.name(r.Item);
      this.possibilityLabel.text = (rate > 0 ? `${Math.min(100, r.Chance + rate)}% (+${rate}%)` : `${r.Chance}%`) + " Chance of Success";
      this.craftGoldLabel.text = r.Gold.toLocaleString("en-US");
    } else this.recipeLabel.text = this.possibilityLabel.text = this.craftGoldLabel.text = "";
  }

  // — Redraw (chained on the inventory window's render) —

  render(): void {
    if (this.craft.visible && !this.o.invWindow.visible) this.hideCraft(); // CraftDialog.BeforeDraw
    if (this.awake.visible) this.renderAwake();
    if (this.craft.visible) this.renderCraft();
  }
}
