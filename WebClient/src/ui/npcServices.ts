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
//   NPCGoodsDialog with UsePearls: S.NPCPearlGoods goes to NpcWindow.showPearlGoods (the desktop reuses the goods list).
// Deliberate differences: awakening cells take items picked from the bag (the desktop also swaps between its cells);
// the needed-material cells show the item name as a hint instead of the full item label.

import type { ClientRecipeInfo, ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { AwakeType, ChatType, ItemGrade, ItemType, PanelType, Stat } from "../protocol/enums.generated.ts";
import { REFINE_SLOTS, type Inventory } from "../game/inventory.ts";
import type { ItemInfos } from "../game/items.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { GoodsDialog, awakeLevel, type NpcWindow } from "./npcWindow.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, closeButton, setPicture, libSize } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { showItemLabel, moveItemLabel, hideItemLabel } from "./itemLabel.ts";

export interface NpcServicesOptions {
  stage: HTMLElement;
  send: (frame: Uint8Array) => void;
  inv: Inventory;
  infos: ItemInfos;
  invWindow: InventoryWindow;
  npc: NpcWindow;
  chat: (text: string, type: number) => void;
  /** UserObject.Stats[stat] (CraftDialog's Stat.CraftRatePercent). */
  stat?: (stat: number) => number;
  /** UserObject.PearlCount (S.UpdateIntelligentCreatureList, not ported yet): null = unknown, the server decides. */
  pearls?: () => number | null;
}

const TOOL_COUNT = 3, INGREDIENT_COUNT = 6; // CraftDialog._toolCount / _ingredientCount
const AWAKE_CELLS = 7; // NPCAwakeDialog.Items (0 base, 1-2 needed materials, 3-4 materials, 5-6 extra)

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  SelectUpgradeItem: "Select Upgrade Item.", SelectUpgradeType: "Select Upgrade Type.",
  NeedItemQuantity: (name: string, n: number) => `${name}\nQuantity: ${n}`,
  YouDoNotHaveEnoughGold: "You do not have enough gold.", CraftAmount: "Craft Amount:",
  YouDoNotHaveRequiredToolsOrIngredients: "You do not have the required tools or ingredients.",
};

/** NPCAwakeDialog.getAwakeTypeText. */
const AWAKE_TYPE_TEXT: Record<number, string> = {
  [AwakeType.DC]: "Bravery Glyph", [AwakeType.MC]: "Magic Glyph", [AwakeType.SC]: "Soul Glyph",
  [AwakeType.AC]: "Protection Glyph", [AwakeType.MAC]: "EvilSlayer Glyph", [AwakeType.HPMP]: "Body Glyph",
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
  private items: string[] = [];
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

  setItems(items: string[], selected: number): void {
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
  private rate = 1; // GameScene.NPCRate (the NPC packets these panels follow)

  // RefineDialog
  private readonly refine: MirWindow;

  // NPCAwakeDialog
  private readonly awake: MirWindow;
  private readonly awakeItems: (UserItem | null)[] = new Array<UserItem | null>(AWAKE_CELLS).fill(null);
  private readonly awakeViews: ItemView[] = [];
  private readonly awakeType: DropDownBox;
  private readonly goldLabel: MirLabel;
  private readonly needLabels: MirLabel[];
  private materials: ({ info: ItemInfo; count: number } | null)[] = [null, null]; // ItemCells[1] / [2]
  private awakeTypes: { text: string; type: number }[] = [];
  private awakeSelected = 0;
  private currentAwakeType: number = AwakeType.None;
  /** Inventory items this port locked locally for the awakening cells (MirItemCell.Locked). */
  private readonly awakeLocks = new Set<string>();

  // NPCCraftGoodsDialog + CraftDialog
  private readonly recipes = new Map<number, ClientRecipeInfo>(); // GameScene.RecipeInfoList (by Item.ItemIndex)
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
    this.o = { stat: () => 0, pearls: () => null, ...options };
    const { invWindow, npc } = this.o;
    npc.pearlCount = () => this.o.pearls();

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
    new MirButton({ parent: this.awake, library: Lib.Title, index: 712, hoverIndex: 713, pressedIndex: 714, location: [115, 391], sound: SoundList.ButtonA, onClick: () => this.awaken() });
    closeButton(this.awake, [284, 4], () => this.hideAwake());
    const cellAt: [number, number][] = [[202, 91], [31, 316], [155, 316], [175, 199], [230, 199], [175, 256], [230, 256]];
    cellAt.forEach((at, i) => this.awakeViews.push(new ItemView(this.awake, at, invWindow, () => this.awakeCellClick(i))));
    this.awakeType = new DropDownBox(this.awake, [35, 141], [109, 14]);
    this.awakeType.onChange = (i) => { this.onAwakeTypeSelect(i); this.render(); };

    // NPCCraftGoodsDialog (NPCGoodsDialog with PanelType.Craft)
    this.craftGoods = new GoodsDialog(PanelType.Craft, {
      inv: this.o.inv, infos: this.o.infos, invWindow, send: this.o.send, chat: (t) => this.system(t), rate: () => this.rate,
      hideAdded: () => false, npcHeight: () => npc.dialogHeight, pearls: () => this.o.pearls(),
      recipeGold: (itemIndex) => this.recipes.get(itemIndex)?.Gold,
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

    // NPCDialog.Hide hides every NPC panel
    const prevClose = npc.onClose;
    npc.onClose = () => { prevClose(); this.closeAll(); };
    // Cells share the inventory window's pick: redraw with it (CraftDialog.BeforeDraw also hides the craft dialog with the bag)
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    // GameScene Escape: NPCAwakeDialog.Hide + RefineDialog.Hide
    window.addEventListener("keydown", (e) => {
      if (!matches(e, KeybindOptions.Closeall)) return;
      this.hideAwake();
      this.hideRefine();
    }, { signal });
    signal.addEventListener("abort", () => { this.refine.dispose(); this.awake.dispose(); this.craft.dispose(); });
    this.itemCellClick();
  }

  private get inv(): Inventory { return this.o.inv; }
  private info(item: UserItem): ItemInfo | undefined { return this.o.infos.get(item.ItemIndex); }
  private system(text: string): void { this.o.chat(text, ChatType.System); }
  private name(item: UserItem): string {
    const info = this.info(item);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }
  private findInv(id: bigint | null): number {
    return id === null ? -1 : this.inv.inventory.findIndex((i) => i?.UniqueID === id);
  }
  private setLock(id: bigint, on: boolean): void {
    if (on) this.inv.locked.add(String(id)); else this.inv.locked.delete(String(id));
  }

  /** Server packets for these panels (Inventory.handle has already run for the item packets). */
  handle(p: ServerPacket): void {
    const npc = this.o.npc;
    const npcVisible = npc.visible;
    switch (p.type) {
      case "NPCResponse": this.closeAll(); break; // GameScene.NPCResponse hides the panels
      // GameScene.NPCRefine
      case "NPCRefine":
        this.rate = p.Rate;
        if (!npcVisible) break;
        if (p.Refining) { npc.drop.hide(); npc.close(); }
        else { npc.drop.show(PanelType.Refine, p.Rate); this.showRefine(); } // NPCDropDialog BeforeDraw: RefineDialog.Show()
        break;
      case "NPCCheckRefine": if (npcVisible) npc.drop.show(PanelType.CheckRefine); break; // GameScene.NPCCheckRefine
      case "NPCCollectRefine": if (npcVisible) npc.close(); break; // GameScene.NPCCollectRefine
      case "NPCReplaceWedRing": // GameScene.NPCReplaceWedRing
        if (!npcVisible) break;
        this.rate = p.Rate;
        npc.drop.show(PanelType.ReplaceWedRing, p.Rate);
        break;
      case "NPCDisassemble": if (npcVisible) npc.drop.show(PanelType.Disassemble); break; // GameScene.NPCDisassemble
      case "NPCDowngrade": if (npcVisible) npc.drop.show(PanelType.Downgrade); break; // GameScene.NPCDowngrade
      case "NPCReset": if (npcVisible) npc.drop.show(PanelType.Reset); break; // GameScene.NPCReset
      // GameScene.RefineItem: RefineReset + NPCDialog.Hide (Inventory removed the item and cleared the grid)
      case "RefineItem": npc.close(); break;
      case "RefineCancel": this.render(); break;
      // MirScene.NewRecipeInfo
      case "NewRecipeInfo": this.recipes.set(p.Info.Item.ItemIndex, p.Info); this.craftGoods.refresh(); break;
      // GameScene.NPCGoods, PanelType.Craft (Buy / BuySub are npcWindow.ts)
      case "NPCGoods":
        if (p.Type !== PanelType.Craft) break;
        this.rate = p.Rate;
        if (!npcVisible) break;
        this.craftGoods.usePearls = false;
        this.craftGoods.newGoods(p.List);
        this.craftGoods.show();
        this.showCraft();
        break;
      case "CraftItem": if (p.Success) this.updateCraftCells(); break; // GameScene.CraftItem
      // GameScene.NPCPearlGoods: NPCGoodsDialog with UsePearls
      case "NPCPearlGoods": this.rate = p.Rate; npc.showPearlGoods(p.List, p.Rate); break;
      case "NPCAwakening": if (!this.awake.visible) this.showAwake(); break; // GameScene.NPCAwakening
      case "AwakeningNeedMaterials": this.setNeedItems(p.Materials, p.MaterialsCount); break;
      case "AwakeningLockedItem": // GameScene.AwakeningLockedItem
        if (this.findInv(p.UniqueID) < 0) break;
        this.setLock(p.UniqueID, p.Locked);
        if (!p.Locked) this.awakeLocks.delete(String(p.UniqueID));
        this.inv.changed();
        break;
      case "Awakening": this.awakening(p.result, p.removeID); break;
    }
  }

  /** NPCDialog.Hide / GameScene.NPCResponse: every panel here (the drop panel is NpcWindow's). */
  private closeAll(): void {
    this.craftGoods.hide();
    this.hideCraft();
    this.hideAwake();
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

  // — NPCAwakeDialog —

  /** NPCAwakeDialog.Show (+ InventoryDialog.Show). */
  private showAwake(): void {
    this.awake.show();
    this.o.invWindow.toggle(true);
    this.itemCellClick();
    this.render();
  }

  /** NPCAwakeDialog.Hide: unlock every cell's item through the server (S.AwakeningLockedItem echo), clear, reset. */
  private hideAwake(): void {
    if (!this.awake.visible) return;
    for (let i = 0; i < AWAKE_CELLS; i++) {
      const item = this.awakeItems[i];
      if (item) this.o.send(ClientPackets.AwakeningLockedItem({ UniqueID: item.UniqueID, Locked: false }));
      this.awakeItems[i] = null;
    }
    this.itemCellClick();
    this.awake.hide();
    hideItemLabel();
  }

  /** NPCAwakeDialog.ItemCellClear. */
  private itemCellClear(): void {
    this.materials = [null, null];
    for (const l of this.needLabels) l.text = "";
    this.goldLabel.text = "";
  }

  /** NPCAwakeDialog.ItemCell_Click: rebuild the glyph list for the base item. */
  private itemCellClick(): void {
    this.itemCellClear();
    this.awakeTypes = [];
    const base = this.awakeItems[0];
    if (!base) {
      this.awakeTypes.push({ text: T.SelectUpgradeItem, type: AwakeType.None });
      this.awakeSelected = 0;
      this.currentAwakeType = AwakeType.None;
    } else if (awakeLevel(base) === 0) {
      this.awakeTypes.push({ text: T.SelectUpgradeType, type: AwakeType.None });
      const t = this.info(base)?.Type;
      const types = t === ItemType.Weapon ? [AwakeType.DC, AwakeType.MC, AwakeType.SC]
        : t === ItemType.Helmet ? [AwakeType.AC, AwakeType.MAC] : [AwakeType.HPMP];
      for (const type of types) this.awakeTypes.push({ text: AWAKE_TYPE_TEXT[type]!, type });
    } else {
      this.awakeTypes.push({ text: AWAKE_TYPE_TEXT[base.AwakeType] ?? T.SelectUpgradeItem, type: base.AwakeType });
      if (this.currentAwakeType !== base.AwakeType) {
        this.currentAwakeType = base.AwakeType;
        this.onAwakeTypeSelect(0);
      }
    }
    if (this.awakeSelected >= this.awakeTypes.length) this.awakeSelected = 0;
    this.awakeType.setItems(this.awakeTypes.map((t) => t.text), this.awakeSelected);
  }

  /** NPCAwakeDialog.OnAwakeTypeSelect: ask the server for the materials of this glyph. */
  private onAwakeTypeSelect(index: number): void {
    this.awakeSelected = index;
    this.awakeType.setItems(this.awakeTypes.map((t) => t.text), index);
    const type = this.awakeTypes[index]?.type ?? AwakeType.None;
    this.currentAwakeType = type;
    const base = this.awakeItems[0];
    if (type !== AwakeType.None && base) this.o.send(ClientPackets.AwakeningNeedMaterials({ UniqueID: base.UniqueID, Type: type }));
  }

  /** NPCAwakeDialog.setNeedItems (a null list clears the cells; the desktop would throw). */
  private setNeedItems(materials: (ItemInfo | null)[] | null, counts: number[] | null): void {
    for (let i = 0; i < 2; i++) {
      const info = materials?.[i] ?? null, count = counts?.[i] ?? 0;
      this.materials[i] = info && count !== 0 ? { info, count } : null;
      this.needLabels[i]!.text = info && count !== 0 ? T.NeedItemQuantity(info.Name.replace(/[\d-]/g, ""), count) : "";
    }
    const base = this.awakeItems[0];
    if (base) {
      // UserItem.AwakeningPrice
      const grade = this.info(base)?.Grade ?? 0;
      this.goldLabel.text = String(1500 * (1 + awakeLevel(base) * 2) * grade);
    }
    this.render();
  }

  /** MirItemCell "To Awakening" with the picked inventory slot; a filled cell clicked with nothing picked goes back. */
  private awakeCellClick(slot: number): void {
    const from = this.o.invWindow.pickedInventorySlot;
    if (from < 0) {
      // "From AwakenItem" (the desktop sends a no-op C.MoveItem from == to to unlock; the lock is local, so just drop it)
      const item = this.awakeItems[slot];
      if (!item || slot === 1 || slot === 2) return;
      this.unlockAwake(item);
      this.awakeItems[slot] = null;
      if (slot === 0) this.itemCellClick();
      this.inv.changed();
      return;
    }
    this.o.invWindow.clearPick();
    const item = this.inv.inventory[from];
    if (!item || this.inv.isLocked(item)) return;
    const info = this.info(item);
    if (!info) return;
    const put = () => {
      this.awakeItems[slot] = item;
      this.setLock(item.UniqueID, true);
      this.awakeLocks.add(String(item.UniqueID));
    };
    switch (slot) {
      case 0: // base item
        if ((info.Type !== ItemType.Weapon && info.Type !== ItemType.Helmet && info.Type !== ItemType.Armour) || info.Grade === ItemGrade.None) return;
        if (this.awakeItems[0]) this.o.send(ClientPackets.AwakeningLockedItem({ UniqueID: this.awakeItems[0].UniqueID, Locked: false }));
        put();
        this.itemCellClick();
        this.onAwakeTypeSelect(0);
        break;
      case 3: case 4: // materials
        if (info.Type !== ItemType.Awakening || info.Shape >= 200 || this.awakeItems[slot]) return;
        put();
        break;
      case 5: case 6: // success-rate / protection items (Shape 200)
        if (info.Type !== ItemType.Awakening || info.Shape !== 200) return;
        if (this.awakeItems[slot]) this.unlockAwake(this.awakeItems[slot]!);
        put();
        break;
      default: return; // 1, 2 show the needed materials
    }
    this.inv.changed();
  }

  private unlockAwake(item: UserItem): void {
    this.setLock(item.UniqueID, false);
    this.awakeLocks.delete(String(item.UniqueID));
  }

  /** NPCAwakeDialog.CheckNeedMaterials. */
  private checkNeedMaterials(): boolean {
    const maxEqual = !this.materials[0] || !this.materials[1] ? 1 : 2;
    let equal = 0;
    for (const need of this.materials) {
      if (!need) continue;
      for (let j = 3; j < 5; j++) {
        const have = this.awakeItems[j];
        if (have && this.info(have)?.Name === need.info.Name && need.count <= have.Count) equal++;
      }
    }
    return equal >= maxEqual;
  }

  /** NPCAwakeDialog.Awakening. */
  private awaken(): void {
    if (!this.checkNeedMaterials()) return;
    const type = this.awakeTypes[this.awakeSelected]?.type ?? AwakeType.None;
    const base = this.awakeItems[0];
    if (type !== AwakeType.None && base) this.o.send(ClientPackets.Awakening({ UniqueID: base.UniqueID, Type: type, PositionIdx: 0 }));
  }

  /** GameScene.Awakening: hide the dialog, drop the destroyed item, unlock, report. */
  private awakening(result: number, removeID: bigint): void {
    this.hideAwake();
    if (removeID >= 0n) {
      const i = this.findInv(BigInt.asUintN(64, removeID));
      if (i >= 0) { this.setLock(this.inv.inventory[i]!.UniqueID, false); this.inv.inventory[i] = null; }
    }
    // The desktop unlocks every locked inventory cell; this port only clears the locks the awakening cells set.
    for (const id of this.awakeLocks) this.inv.locked.delete(id);
    this.awakeLocks.clear();
    this.inv.changed();
    const text: Record<number, string> = {
      [-4]: "You have not supplied enough materials.", [-3]: "Not enough gold.",
      [-2]: "Awakening already at maximum level.", [-1]: "Cannot awaken this item.",
    };
    if (text[result]) messageBox(this.o.stage, text[result]);
  }

  /** The awakening cells (MirItemCell GridType AwakenItem). */
  private renderAwake(): void {
    for (let slot = 0; slot < AWAKE_CELLS; slot++) {
      const view = this.awakeViews[slot]!;
      if (slot === 1 || slot === 2) {
        const need = this.materials[slot - 1];
        view.set(need ? need.info.Image : -1, false, need && need.info.StackSize > 1 ? String(need.count) : "", "#ffff00", null);
        view.control.hint = need ? need.info.Name : "";
        continue;
      }
      const item = this.awakeItems[slot];
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
    return this.recipeItem ? this.recipes.get(this.recipeItem.ItemIndex) : undefined;
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
