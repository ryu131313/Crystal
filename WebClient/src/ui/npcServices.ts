// NPC item services (Client/MirScenes/Dialogs/NPCDialogs.cs + GameScene handlers, DOM edition):
//   NPCDropDialog (the PanelTypes npcWindow.ts does not own): Refine, CheckRefine, ReplaceWedRing, Disassemble, Downgrade, Reset.
//                 Pick an inventory item and click the panel's cell (or, except for Refine, just click the item: the refine
//                 grid needs the plain pick), then Confirm.
//   RefineDialog: the 4 x 4 refine grid (items live in Inventory.refine, cells share the inventory pick); hiding it sends
//                 C.RefineCancel and the server hands the items back with S.RetrieveRefineItem.
//   NPCAwakeDialog: base item, the server's material list (S.AwakeningNeedMaterials), 2 material + 2 extra cells, glyph type,
//                 Awaken (C.Awakening); S.AwakeningLockedItem / S.Awakening answers.
//   NPCCraftGoodsDialog + CraftDialog: S.NPCGoods with PanelType.Craft lists recipes (S.NewRecipeInfo); pick tools /
//                 ingredients into the recipe cells (or Auto Fill), Craft sends C.CraftItem with the inventory slots.
//   NPCGoodsDialog with UsePearls: S.NPCPearlGoods, click to buy (C.BuyItem, Type = PanelType.Buy like the desktop).
// MirMessageBox / MirAmountBox: messageBox() for the awakening results, prompt() for amounts, chat lines for refusals.

import type { ClientRecipeInfo, ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { AwakeType, ChatType, ItemGrade, ItemType, PanelType, Stat } from "../protocol/enums.generated.ts";
import { REFINE_SLOTS, type Inventory } from "../game/inventory.ts";
import { type ItemInfos, itemPrice } from "../game/items.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { itemIcon, type InventoryWindow } from "./inventoryWindow.ts";
import type { NpcWindow } from "./npcWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

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

type DropType = typeof PanelType.Refine | typeof PanelType.CheckRefine | typeof PanelType.ReplaceWedRing
  | typeof PanelType.Disassemble | typeof PanelType.Downgrade | typeof PanelType.Reset;

const TOOL_COUNT = 3, INGREDIENT_COUNT = 6; // CraftDialog._toolCount / _ingredientCount
const AWAKE_CELLS = 7; // NPCAwakeDialog.Items (0 base, 1-2 needed materials, 3-4 materials, 5-6 extra)

/** Stats.Count: the sum of |value| (UserItem.AddedStats.Count in the price formulas). */
const addedCount = (item: UserItem) => Object.values(item.AddedStats).reduce((sum, v) => sum + Math.abs(v), 0);
/** Awake.GetAwakeLevel. */
const awakeLevel = (item: UserItem) => item.AwakeLevels.length;
/** C# float ToString for the NPCDropDialog prices (RequiredAmount * 10 * NPCRate). */
const floatText = (v: number) => String(Number(Math.fround(v).toPrecision(7)));

/** NPCAwakeDialog.getAwakeTypeText. */
const AWAKE_TYPE_TEXT: Record<number, string> = {
  [AwakeType.DC]: "Bravery Glyph", [AwakeType.MC]: "Magic Glyph", [AwakeType.SC]: "Soul Glyph",
  [AwakeType.AC]: "Protection Glyph", [AwakeType.MAC]: "EvilSlayer Glyph", [AwakeType.HPMP]: "Body Glyph",
};

export class NpcServices {
  private readonly o: Required<NpcServicesOptions>;
  private rate = 1; // GameScene.NPCRate (the NPC packets these panels follow)

  // NPCDropDialog
  private readonly drop = el("div", { class: "window npc-drop npc-service-drop", hidden: true });
  private dropType: DropType = PanelType.Refine;
  private target: bigint | null = null; // NPCDropDialog.TargetItem (its cell is OldCell, locked)
  private dropHook: ((index: number) => boolean) | null = null;

  // RefineDialog
  private readonly refine = el("div", { class: "window npc-refine", hidden: true });

  // NPCAwakeDialog
  private readonly awake = el("div", { class: "window npc-awake", hidden: true });
  private readonly awakeItems: (UserItem | null)[] = new Array<UserItem | null>(AWAKE_CELLS).fill(null);
  private materials: ({ info: ItemInfo; count: number } | null)[] = [null, null]; // ItemCells[1] / [2]
  private awakeTypes: { text: string; type: number }[] = [];
  private awakeSelected = 0;
  private currentAwakeType: number = AwakeType.None;
  private awakeGold = "";
  /** Inventory items this port locked locally for the awakening cells (MirItemCell.Locked). */
  private readonly awakeLocks = new Set<string>();

  // NPCCraftGoodsDialog + CraftDialog
  private readonly recipes = new Map<number, ClientRecipeInfo>(); // GameScene.RecipeInfoList (by Item.ItemIndex)
  private readonly craftGoods = el("div", { class: "window npc-goods npc-craft-goods", hidden: true });
  private craftList: UserItem[] = [];
  private readonly craft = el("div", { class: "window npc-craft", hidden: true });
  private recipeItem: UserItem | null = null; // CraftDialog.RecipeItem
  /** CraftDialog.Selected: craft cell -> the inventory slot + UniqueID put there (insertion order = C.CraftItem.Slots). */
  private readonly selected = new Map<number, { slot: number; id: bigint }>();

  // NPCGoodsDialog (UsePearls)
  private readonly pearlGoods = el("div", { class: "window npc-goods npc-pearl-goods", hidden: true });
  private pearlList: UserItem[] = [];

  constructor(options: NpcServicesOptions) {
    this.o = { stat: () => 0, pearls: () => null, ...options };
    const { stage, invWindow, npc } = this.o;
    for (const w of [this.drop, this.refine, this.awake, this.craftGoods, this.craft, this.pearlGoods]) {
      w.addEventListener("mousedown", (e) => e.stopPropagation());
      w.addEventListener("contextmenu", (e) => e.preventDefault());
      stage.append(w);
    }
    // NPCDialog.Hide hides every NPC panel
    const prevClose = npc.onClose;
    npc.onClose = () => { prevClose(); this.closeAll(); };
    // Cells share the inventory window's pick: redraw with it (CraftDialog.BeforeDraw also hides the craft dialog with the bag)
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    // GameScene Escape: NPCAwakeDialog.Hide + RefineDialog.Hide
    window.addEventListener("keydown", (e) => {
      if (!matches(e, KeybindOptions.Closeall)) return;
      this.hideAwake();
      this.hideRefine();
    }, { signal: gameSignal() });
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
    const npcVisible = this.o.npc.visible;
    switch (p.type) {
      case "NPCResponse": this.closeAll(); break; // GameScene.NPCResponse hides the panels
      // GameScene.NPCRefine
      case "NPCRefine":
        this.rate = p.Rate;
        if (!npcVisible) break;
        this.dropType = PanelType.Refine;
        if (p.Refining) { this.hideDrop(); this.o.npc.close(); }
        else this.showDrop(PanelType.Refine);
        break;
      case "NPCCheckRefine": if (npcVisible) this.showDrop(PanelType.CheckRefine); break; // GameScene.NPCCheckRefine
      case "NPCCollectRefine": if (npcVisible) this.o.npc.close(); break; // GameScene.NPCCollectRefine
      case "NPCReplaceWedRing": // GameScene.NPCReplaceWedRing
        if (!npcVisible) break;
        this.rate = p.Rate;
        this.showDrop(PanelType.ReplaceWedRing);
        break;
      case "NPCDisassemble": if (npcVisible) this.showDrop(PanelType.Disassemble); break; // GameScene.NPCDisassemble
      case "NPCDowngrade": if (npcVisible) this.showDrop(PanelType.Downgrade); break; // GameScene.NPCDowngrade
      case "NPCReset": if (npcVisible) this.showDrop(PanelType.Reset); break; // GameScene.NPCReset
      // GameScene.RefineItem: RefineReset + NPCDialog.Hide (Inventory removed the item and cleared the grid)
      case "RefineItem": this.o.npc.close(); break;
      case "RefineCancel": this.render(); break;
      // MirScene.NewRecipeInfo
      case "NewRecipeInfo": this.recipes.set(p.Info.Item.ItemIndex, p.Info); if (!this.craftGoods.hidden) this.renderCraftGoods(); break;
      // GameScene.NPCGoods, PanelType.Craft (Buy / BuySub are npcWindow.ts)
      case "NPCGoods":
        if (p.Type !== PanelType.Craft) break;
        this.rate = p.Rate;
        if (!npcVisible) break;
        this.craftList = p.List;
        this.pearlGoods.hidden = true;
        this.craftGoods.hidden = false;
        this.o.invWindow.toggle(true); // NPCGoodsDialog.Show
        this.renderCraftGoods();
        this.showCraft(); // CraftDialog.Show
        break;
      case "CraftItem": if (p.Success) this.updateCraftCells(); break; // GameScene.CraftItem
      // GameScene.NPCPearlGoods
      case "NPCPearlGoods":
        this.rate = p.Rate;
        if (!npcVisible) break;
        this.pearlList = p.List;
        this.hideCraftGoods();
        this.pearlGoods.hidden = false;
        this.o.invWindow.toggle(true);
        this.renderPearlGoods();
        break;
      case "NPCAwakening": if (this.awake.hidden) this.showAwake(); break; // GameScene.NPCAwakening
      case "AwakeningNeedMaterials": this.setNeedItems(p.Materials, p.MaterialsCount); break;
      case "AwakeningLockedItem": // GameScene.AwakeningLockedItem
        if (this.findInv(p.UniqueID) < 0) break;
        this.setLock(p.UniqueID, p.Locked);
        if (!p.Locked) this.awakeLocks.delete(String(p.UniqueID));
        this.inv.onChange();
        break;
      case "Awakening": this.awakening(p.result, p.removeID); break;
    }
  }

  /** NPCDialog.Hide / GameScene.NPCResponse: every panel here. */
  private closeAll(): void {
    this.hideDrop();
    this.hideCraftGoods();
    this.pearlGoods.hidden = true;
    this.hideAwake();
    this.hideRefine();
  }

  // — NPCDropDialog —

  /** NPCDropDialog.Show (+ RefineDialog.Show for Refine, from its BeforeDraw). */
  private showDrop(type: DropType): void {
    this.clearTarget();
    this.dropType = type;
    this.o.invWindow.toggle(true);
    // A click on an inventory item targets it (like npcWindow's sell panel); Refine keeps the plain pick for its grid.
    if (type !== PanelType.Refine) {
      this.dropHook = (index) => { this.takeTarget(index); return true; };
      this.o.invWindow.itemClickHook = this.dropHook;
    } else if (this.o.invWindow.itemClickHook === this.dropHook) this.o.invWindow.itemClickHook = null;
    this.drop.hidden = false;
    if (type === PanelType.Refine) this.refine.hidden = false;
    this.render();
  }

  /** NPCDropDialog.Hide: the targeted cell unlocks. */
  private hideDrop(): void {
    this.clearTarget();
    this.drop.hidden = true;
    if (this.dropHook && this.o.invWindow.itemClickHook === this.dropHook) this.o.invWindow.itemClickHook = null;
    this.dropHook = null;
  }

  private clearTarget(): void {
    if (this.target !== null) { this.setLock(this.target, false); this.inv.onChange(); }
    this.target = null;
  }

  /** NPCDropDialog.ItemCell_Click with GameScene.SelectedCell = inventory slot `index` (-1 = nothing picked). */
  private takeTarget(index: number): void {
    this.clearTarget();
    const item = index >= 0 ? this.inv.inventory[index] ?? null : null;
    if (!item || this.inv.isLocked(item)) { this.render(); return; }
    const info = this.info(item);
    const type = this.dropType;
    if (type === PanelType.Disassemble || type === PanelType.Downgrade || type === PanelType.Reset) {
      const ok = type === PanelType.Disassemble ? !!info && info.Grade !== ItemGrade.None && info.Type !== ItemType.Awakening
        : type === PanelType.Downgrade ? awakeLevel(item) !== 0
        : addedCount(item) > 0 || (!!info && item.Slots.length > info.Slots); // UserItem.IsAdded
      if (ok) this.target = item.UniqueID;
    } else if (!(info && info.Durability === 0)) {
      this.target = item.UniqueID; // the other panels refuse items without durability
    }
    if (this.target !== null) { this.setLock(this.target, true); this.inv.onChange(); }
    this.render();
  }

  /** The panel's cell: takes the picked inventory item (NPCDropDialog.ItemCell_Click). */
  private dropCellClick(): void {
    const slot = this.o.invWindow.pickedInventorySlot;
    if (slot >= 0) this.o.invWindow.clearPick();
    this.takeTarget(slot);
  }

  /** NPCDropDialog.Confirm for this file's PanelTypes. */
  private confirm(): void {
    const index = this.findInv(this.target);
    const item = index >= 0 ? this.inv.inventory[index]! : null;
    if (!item) { this.clearTarget(); this.render(); return; }
    const info = this.info(item);
    const send = this.o.send;
    switch (this.dropType) {
      case PanelType.Disassemble: send(ClientPackets.DisassembleItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Downgrade: send(ClientPackets.DowngradeAwakening({ UniqueID: item.UniqueID })); break;
      case PanelType.Reset: if (info && !info.NeedIdentify) send(ClientPackets.ResetAddedItem({ UniqueID: item.UniqueID })); break;
      case PanelType.Refine: {
        if (!this.inv.refine.some((i) => i)) { this.system(`You haven't deposited any items to refine your ${this.name(item)} with.`); return; }
        if (this.inv.gold < (info?.RequiredAmount ?? 0) * 10 * this.rate) { this.system(`You don't have enough gold to refine your ${this.name(item)}.`); return; }
        send(ClientPackets.RefineItem({ UniqueID: item.UniqueID }));
        this.target = null; // stays locked: the server's S.RepairItem unlocks it, S.RefineItem removes it
        this.render();
        return;
      }
      case PanelType.CheckRefine:
        if (item.RefineAdded === 0) { this.system(`Your ${this.name(item)} hasn't been refined so it doesn't need checking.`); return; }
        send(ClientPackets.CheckRefine({ UniqueID: item.UniqueID }));
        break;
      case PanelType.ReplaceWedRing:
        if (info?.Type !== ItemType.Ring) { this.system(`${this.name(item)} isn't a ring.`); return; }
        send(ClientPackets.ReplaceWedRing({ UniqueID: item.UniqueID }));
        break;
    }
    this.clearTarget();
    this.render();
  }

  /** NPCDropDialog_BeforeDraw: the label and the price for the target. */
  private dropText(item: UserItem | null): string {
    const info = item ? this.info(item) : undefined;
    const grade = info?.Grade ?? 0;
    const label: Record<number, string> = {
      [PanelType.Disassemble]: "Item will be destroyed. ", [PanelType.Downgrade]: "Downgrade: ", [PanelType.Reset]: "Reset: ",
      [PanelType.Refine]: "Refine: ", [PanelType.CheckRefine]: "Check Refine", [PanelType.ReplaceWedRing]: "Replace: ",
    };
    let text = label[this.dropType] ?? "";
    if (!item || !info) return text;
    switch (this.dropType) {
      // UserItem.DisassemblePrice / DowngradePrice / ResetPrice (uint casts of float products)
      case PanelType.Disassemble: text += Math.trunc(1500 * grade * Math.fround((addedCount(item) + awakeLevel(item)) * 0.1 + 1)); break;
      case PanelType.Downgrade: text += 3000 * (1 + (awakeLevel(item) + 1) * 2) * grade; break;
      case PanelType.Reset: text += Math.trunc(3000 * grade * Math.fround(addedCount(item) * 0.2 + 1)); break;
      case PanelType.Refine: case PanelType.ReplaceWedRing: text += floatText(info.RequiredAmount * 10 * this.rate); break;
      default: return text; // CheckRefine shows no price
    }
    return text + " Gold";
  }

  private renderDrop(): void {
    const index = this.findInv(this.target);
    if (index < 0 && this.target !== null) this.target = null;
    const item = index >= 0 ? this.inv.inventory[index]! : null;
    const titles: Record<number, string> = {
      [PanelType.Refine]: "Refine", [PanelType.CheckRefine]: "Check Refine", [PanelType.ReplaceWedRing]: "Replace Wedding Ring",
      [PanelType.Disassemble]: "Disassemble", [PanelType.Downgrade]: "Downgrade Awakening", [PanelType.Reset]: "Reset Added Stats",
    };
    const cell = this.o.invWindow.viewCell(item);
    cell.classList.add("npc-drop-cell");
    cell.onmousedown = (e) => { e.stopPropagation(); if (e.button === 0) this.dropCellClick(); };
    const hint = this.dropType === PanelType.Refine ? "Pick an item in your bag, then click the cell." : "Click an item in your bag.";
    this.drop.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, titles[this.dropType] ?? "NPC"), el("button", { class: "window-close", onclick: () => this.hideDrop() }, "×")),
      el("div", { class: "npc-drop-body" }, cell, el("div", {},
        el("div", {}, item ? `${this.name(item)}${item.Count > 1 ? ` x${item.Count}` : ""}` : hint),
        el("div", { class: "npc-goods-price" }, this.dropText(item)))),
      el("div", { class: "row" }, el("button", { class: "primary", disabled: !item, onclick: () => this.confirm() }, "Confirm")),
    );
  }

  // — RefineDialog —

  /** RefineDialog.Hide: C.RefineCancel (the server returns the items with S.RetrieveRefineItem). */
  private hideRefine(): void {
    if (this.refine.hidden) return;
    this.refine.hidden = true;
    this.o.send(ClientPackets.RefineCancel());
  }

  private renderRefine(): void {
    const cells: HTMLElement[] = [];
    for (let i = 0; i < REFINE_SLOTS; i++) cells.push(this.o.invWindow.refineCell(i));
    this.refine.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, "Refine"), el("button", { class: "window-close", title: "Cancel the refine (items go back to the bag)", onclick: () => this.hideRefine() }, "×")),
      el("div", { class: "inv-grid npc-refine-grid" }, ...cells),
    );
  }

  // — NPCAwakeDialog —

  /** NPCAwakeDialog.Show (+ InventoryDialog.Show). */
  private showAwake(): void {
    this.awake.hidden = false;
    this.o.invWindow.toggle(true);
    this.itemCellClick();
    this.render();
  }

  /** NPCAwakeDialog.Hide: unlock every cell's item through the server (S.AwakeningLockedItem echo), clear, reset. */
  private hideAwake(): void {
    if (this.awake.hidden) return;
    for (let i = 0; i < AWAKE_CELLS; i++) {
      const item = this.awakeItems[i];
      if (item) this.o.send(ClientPackets.AwakeningLockedItem({ UniqueID: item.UniqueID, Locked: false }));
      this.awakeItems[i] = null;
    }
    this.itemCellClick();
    this.awake.hidden = true;
  }

  /** NPCAwakeDialog.ItemCellClear. */
  private itemCellClear(): void {
    this.materials = [null, null];
    this.awakeGold = "";
  }

  /** NPCAwakeDialog.ItemCell_Click: rebuild the glyph list for the base item. */
  private itemCellClick(): void {
    this.itemCellClear();
    this.awakeTypes = [];
    const base = this.awakeItems[0];
    if (!base) {
      this.awakeTypes.push({ text: "Select Upgrade Item.", type: AwakeType.None });
      this.awakeSelected = 0;
      this.currentAwakeType = AwakeType.None;
      return;
    }
    if (awakeLevel(base) === 0) {
      this.awakeTypes.push({ text: "Select Upgrade Type.", type: AwakeType.None });
      const t = this.info(base)?.Type;
      const types = t === ItemType.Weapon ? [AwakeType.DC, AwakeType.MC, AwakeType.SC]
        : t === ItemType.Helmet ? [AwakeType.AC, AwakeType.MAC] : [AwakeType.HPMP];
      for (const type of types) this.awakeTypes.push({ text: AWAKE_TYPE_TEXT[type]!, type });
    } else {
      this.awakeTypes.push({ text: AWAKE_TYPE_TEXT[base.AwakeType] ?? "Select Upgrade Item.", type: base.AwakeType });
      if (this.currentAwakeType !== base.AwakeType) {
        this.currentAwakeType = base.AwakeType;
        this.onAwakeTypeSelect(0);
      }
    }
    if (this.awakeSelected >= this.awakeTypes.length) this.awakeSelected = 0;
  }

  /** NPCAwakeDialog.OnAwakeTypeSelect: ask the server for the materials of this glyph. */
  private onAwakeTypeSelect(index: number): void {
    this.awakeSelected = index;
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
    }
    const base = this.awakeItems[0];
    if (base) {
      // UserItem.AwakeningPrice
      const grade = this.info(base)?.Grade ?? 0;
      this.awakeGold = String(1500 * (1 + awakeLevel(base) * 2) * grade);
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
      this.inv.onChange();
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
    this.inv.onChange();
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
    this.inv.onChange();
    const text: Record<number, string> = {
      [-4]: "You have not supplied enough materials.", [-3]: "Not enough gold.",
      [-2]: "Awakening already at maximum level.", [-1]: "Cannot awaken this item.",
    };
    if (text[result]) messageBox(this.o.stage, text[result]);
  }

  private awakeCell(slot: number): HTMLElement {
    let c: HTMLElement;
    if (slot === 1 || slot === 2) {
      const need = this.materials[slot - 1];
      c = el("div", { class: "inv-cell npc-awake-need", title: need ? need.info.Name : "" });
      if (need) {
        const img = el("img", { alt: need.info.Name, draggable: false });
        void itemIcon(need.info.Image).then((url) => { if (url) img.src = url; });
        c.append(img);
        if (need.count > 1) c.append(el("span", { class: "inv-count" }, String(need.count)));
      }
    } else {
      c = this.o.invWindow.viewCell(this.awakeItems[slot] ?? null);
      c.onmousedown = (e) => { e.stopPropagation(); if (e.button === 0) this.awakeCellClick(slot); };
    }
    return c;
  }

  private renderAwake(): void {
    const select = el("select", { class: "npc-awake-type" });
    this.awakeTypes.forEach((t, i) => select.append(el("option", { value: String(i), selected: i === this.awakeSelected }, t.text)));
    select.onchange = () => this.onAwakeTypeSelect(Number(select.value));
    const needText = this.materials.map((m) => m ? `${m.info.Name.replace(/[\d-]/g, "")}  x${m.count}` : "").filter(Boolean).join("   ");
    this.awake.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, "Awakening"), el("button", { class: "window-close", onclick: () => this.hideAwake() }, "×")),
      el("div", { class: "npc-awake-top" }, el("div", {}, el("div", { class: "inv-label" }, "Item"), this.awakeCell(0)), el("div", {}, el("div", { class: "inv-label" }, "Glyph"), select)),
      el("div", { class: "inv-label" }, "Materials"), el("div", { class: "npc-awake-row" }, this.awakeCell(3), this.awakeCell(4)),
      el("div", { class: "inv-label" }, "Extra"), el("div", { class: "npc-awake-row" }, this.awakeCell(5), this.awakeCell(6)),
      el("div", { class: "inv-label" }, "Needed"), el("div", { class: "npc-awake-row" }, this.awakeCell(1), this.awakeCell(2), el("span", { class: "npc-awake-need-text" }, needText)),
      el("div", { class: "npc-goods-price" }, this.awakeGold ? `Cost: ${this.awakeGold} gold` : ""),
      el("div", { class: "row" }, el("button", { class: "primary", disabled: !this.awakeItems[0], onclick: () => this.awaken() }, "Awaken")),
    );
  }

  // — NPCCraftGoodsDialog + CraftDialog —

  /** NPCGoodsDialog.Hide (PanelType.Craft) also hides the CraftDialog. */
  private hideCraftGoods(): void {
    this.craftGoods.hidden = true;
    this.hideCraft();
  }

  private showCraft(): void {
    this.craft.hidden = false;
    this.render();
  }

  /** CraftDialog.Hide -> ResetCells. */
  private hideCraft(): void {
    if (this.craft.hidden) return;
    this.craft.hidden = true;
    this.resetCells();
  }

  /** CraftDialog.ResetCells: the selected inventory cells unlock. */
  private resetCells(clearRecipe = true): void {
    if (clearRecipe) this.recipeItem = null;
    for (const { id } of this.selected.values()) this.setLock(id, false);
    const had = this.selected.size > 0;
    this.selected.clear();
    if (had) this.inv.onChange();
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

  /** NPCCraftGoodsDialog cell click: select the recipe (ResetCells + RefreshCraftCells, CraftDialog.Show). */
  private selectRecipe(item: UserItem): void {
    this.resetCells();
    this.recipeItem = item;
    this.showCraft();
    this.renderCraftGoods();
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
    this.inv.onChange();
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
    this.inv.onChange();
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

  /** CraftDialog.CraftItem. */
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
    const stack = this.info(recipeItem)?.StackSize ?? 1;
    const perCraft = Math.trunc(stack / Math.max(1, recipeItem.Count));
    if (max > perCraft) max = perCraft;
    const slots = [...this.selected.values()].map((s) => s.slot);
    if (max === 1 && r.Gold > this.inv.gold) { this.system("You do not have enough gold."); return; }
    if (max > 1) {
      const answer = prompt(`Craft Amount: (1-${max})`, String(max));
      if (answer === null) return;
      const amount = Math.trunc(Number(answer));
      if (!(amount > 0)) return;
      const n = Math.min(amount, max);
      if (!this.hasCraftItems(n)) { this.system("You do not have the required tools or ingredients."); return; }
      if (r.Gold * n > this.inv.gold) { this.system("You do not have enough gold."); return; }
      this.o.send(ClientPackets.CraftItem({ UniqueID: recipeItem.UniqueID, Count: n, Slots: slots }));
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
    if (changed) this.inv.onChange(); else this.render();
  }

  private renderCraftGoods(): void {
    const rows = this.craftList.map((item) => {
      const info = this.info(item), recipe = this.recipes.get(item.ItemIndex);
      const img = el("img", { class: "npc-goods-icon", alt: "" });
      if (info) void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
      const price = recipe ? Math.trunc(Math.fround(recipe.Gold * this.rate)) : 0; // MirGoodsCell (Recipe): recipe.Gold * NPCRate
      const chosen = this.recipeItem === item;
      return el("div", { class: `npc-goods-row${chosen ? " selected" : ""}`, title: info?.Name ?? "", onclick: () => this.selectRecipe(item) },
        img, el("span", { class: "npc-goods-name" }, info?.Name ?? `Item #${item.ItemIndex}`, item.Count > 1 ? ` (${item.Count})` : ""),
        el("span", { class: "npc-goods-price" }, `${price.toLocaleString()} gold`));
    });
    this.craftGoods.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, "Craft"), el("button", { class: "window-close", onclick: () => this.hideCraftGoods() }, "×")),
      el("div", { class: "npc-goods-list" }, ...rows));
  }

  private renderCraft(): void {
    const r = this.recipe;
    const cell = (i: number) => {
      const item = this.craftCellItem(i), shadow = this.shadow(i);
      const c = item ? this.o.invWindow.viewCell(item) : this.o.invWindow.viewCell(shadow);
      if (!item && shadow) c.classList.add("npc-craft-shadow");
      if (!shadow) c.classList.add("npc-craft-unused");
      c.onmousedown = (e) => { e.stopPropagation(); if (e.button === 0) this.craftCellClick(i); };
      return c;
    };
    // CraftDialog.RefreshCraftCells: Craft is enabled when every needed cell holds enough
    let ready = !!r;
    for (let i = 0; i < TOOL_COUNT + INGREDIENT_COUNT && r; i++) {
      const shadow = this.shadow(i);
      if (!shadow) continue;
      const item = this.craftCellItem(i);
      if (!item || item.Count < shadow.Count) ready = false;
    }
    const rate = this.o.stat(Stat.CraftRatePercent);
    const chance = r ? (rate > 0 ? `${Math.min(100, r.Chance + rate)}% (+${rate}%)` : `${r.Chance}%`) + " Chance of Success" : "";
    const tools: HTMLElement[] = [], ingredients: HTMLElement[] = [];
    for (let i = 0; i < TOOL_COUNT; i++) tools.push(cell(i));
    for (let i = TOOL_COUNT; i < TOOL_COUNT + INGREDIENT_COUNT; i++) ingredients.push(cell(i));
    this.craft.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, r ? this.name(r.Item) : "Select a recipe"), el("button", { class: "window-close", onclick: () => this.hideCraft() }, "×")),
      el("div", { class: "inv-label" }, "Tools"), el("div", { class: "npc-craft-row" }, ...tools),
      el("div", { class: "inv-label" }, "Ingredients"), el("div", { class: "npc-craft-row" }, ...ingredients),
      el("div", { class: "npc-craft-chance" }, chance),
      el("div", { class: "npc-goods-price" }, r ? `Gold: ${r.Gold.toLocaleString("en-US")}` : ""),
      el("div", { class: "row" },
        el("button", { disabled: !r, onclick: () => this.autoFill() }, "Auto Fill"),
        el("button", { class: "primary", disabled: !ready, onclick: () => this.craftItem() }, "Craft")),
    );
  }

  // — NPCGoodsDialog (UsePearls) —

  private renderPearlGoods(): void {
    const rows = this.pearlList.map((item) => {
      const info = this.info(item);
      const img = el("img", { class: "npc-goods-icon", alt: "" });
      if (info) void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
      const price = Math.trunc(Math.fround(itemPrice(item, info) * this.rate)); // MirGoodsCell: PricePearl, Price() * NPCRate
      return el("div", { class: "npc-goods-row", title: info?.Name ?? "", onclick: () => this.buyPearl(item) },
        img, el("span", { class: "npc-goods-name" }, info?.Name ?? `Item #${item.ItemIndex}`, item.Count > 1 ? ` (${item.Count})` : ""),
        el("span", { class: "npc-goods-price" }, `${price.toLocaleString()} pearl${itemPrice(item, info) > 1 ? "s" : ""}`));
    });
    this.pearlGoods.replaceChildren(
      el("div", { class: "window-title" }, el("span", {}, "Pearl Goods"), el("button", { class: "window-close", onclick: () => (this.pearlGoods.hidden = true) }, "×")),
      el("div", { class: "npc-goods-list" }, ...rows));
  }

  /** NPCGoodsDialog.BuyItem with UsePearls (the desktop's stack check divides the gold; this port uses the pearl count
   *  when it is known and otherwise leaves the check to the server). */
  private buyPearl(item: UserItem): void {
    const info = this.info(item);
    if (!info) return;
    const pearls = this.o.pearls();
    let count = 1;
    if (info.StackSize > 1) {
      const unit = Math.max(1, itemPrice({ ...item, Count: 1 }, info));
      let max = info.StackSize;
      if (pearls !== null && unit * max > pearls) {
        max = Math.min(0xffff, Math.trunc(pearls / unit));
        if (max === 0) { this.system("You do not have enough Pearls."); return; }
      }
      const answer = prompt(`Purchase Amount: (1-${max})`, String(Math.min(item.Count, max)));
      if (answer === null) return;
      count = Math.trunc(Number(answer));
      if (!(count >= 1 && count <= max)) return;
    } else {
      if (pearls !== null && info.Price > pearls) { this.system("You do not have enough Pearls."); return; }
      if (!this.inv.inventory.some((i) => !i)) { this.system("You do not have enough space."); return; }
    }
    this.o.send(ClientPackets.BuyItem({ ItemIndex: item.UniqueID, Count: count, Type: PanelType.Buy }));
  }

  // — Redraw (chained on the inventory window's render) —

  render(): void {
    if (!this.craft.hidden && !this.o.invWindow.visible) this.hideCraft(); // CraftDialog.BeforeDraw
    if (!this.drop.hidden) this.renderDrop();
    if (!this.refine.hidden) this.renderRefine();
    if (!this.awake.hidden) this.renderAwake();
    if (!this.craft.hidden) this.renderCraft();
  }
}
