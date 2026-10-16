// Dropping the picked item or gold on the world: MapControl.OnMouseClick with GameScene.SelectedCell / PickedUpGold
// (Client/MirScenes/GameScene.cs): the YouCannotDrop box for a DontDrop item, the DropTip Yes/No for one item,
// MirAmountBox (DropAmount) for a stack, MirAmountBox (Items 116) for gold, then C.DropItem / C.DropGold. DOM-free:
// each UI passes its own prompts (DropPrompts) and its pick-clearing redraw, so the classic and modern UIs share the rules.

import { BindMode } from "../protocol/enums.generated.ts";
import { itemFriendlyName } from "./gameInfo.ts";
import type { Inventory } from "./inventory.ts";
import type { ItemInfos } from "./items.ts";
import type { Hero } from "./hero.ts";
import type { HeroItems } from "./heroItems.ts";
import type { ItemPick } from "./itemPick.ts";

/** The UI's prompts (classic: messageBox / amountBox). */
export interface DropPrompts {
  /** A Yes / No question; `yes` runs on Yes. */
  confirm(text: string, yes: () => void): void;
  /** MirAmountBox: `ok(n)` with 0..max. */
  amount(title: string, image: number, max: number, ok: (n: number) => void): void;
  /** A refusal with an OK button. */
  refuse(text: string): void;
}

const T = { // ClientTextKeys (English.json)
  YouCannotDrop: (name: string) => `You cannot drop ${name}`,
  DropTip: (name: string) => `Are you sure you want to drop ${name}?`,
  DropAmount: "Drop Amount:",
};

/** Items[116]: the gold icon. */
const GOLD_IMAGE = 116;

/** ItemInfo.FriendlyName + UserItem.FriendlyName's " (count)". */
const friendlyName = (info: Parameters<typeof itemFriendlyName>[0], count: number) => itemFriendlyName(info) + (count > 1 ? ` (${count})` : "");

/** GameScene.PickedUpGold + map click: MirAmountBox (DropAmount, Items 116, max = the user's gold) -> C.DropGold. */
export function askDropGold(inventory: Inventory, ui: DropPrompts): void {
  ui.amount(T.DropAmount, GOLD_IMAGE, inventory.gold, (n) => { if (n > 0) inventory.dropGold(n); });
}

/** GameScene map click while a hero belt / bag cell is picked: the YouCannotDrop box for DontDrop, the DropTip Yes/No
 *  for a single item or MirAmountBox (DropAmount) for a stack, then C.DropItem with HeroInventory = true. An equipment /
 *  auto-pot pick is only cleared. Returns true when it used the click (a hero cell was picked). */
export function dropHeroItem(d: { pick: ItemPick; hero: Hero; heroItems: HeroItems; clearPick: () => void }, ui: DropPrompts): boolean {
  const pick = d.pick.heroCell;
  if (!pick) return false;
  d.clearPick();
  if (pick.grid !== "heroInv") return true; // equipment / auto-pot picks: GameScene clears SelectedCell
  const item = d.hero.info?.inventory[pick.index];
  const info = item && d.heroItems.itemInfo(item);
  if (!item) return true;
  const name = info?.Name ?? "this item";
  if (info && (info.Bind & BindMode.DontDrop)) { ui.refuse(T.YouCannotDrop(name)); return true; }
  if (item.Count === 1) {
    ui.confirm(T.DropTip(name), () => d.heroItems.drop(pick.index, 1));
    return true;
  }
  ui.amount(T.DropAmount, info?.Image ?? -1, item.Count, (n) => { if (n > 0) d.heroItems.drop(pick.index, n); });
  return true;
}

/** MapControl.OnMouseClick with GameScene.SelectedCell on a bag / belt slot: DontDrop refusal, YesNo for one item,
 *  MirAmountBox for a stack (C.DropItem via Inventory.drop); the pick is cleared after the prompt opens. Returns true
 *  when a bag slot was picked. */
export function dropBagItem(d: { pick: ItemPick; inventory: Inventory; itemInfos: ItemInfos; clearPick: () => void }, ui: DropPrompts): boolean {
  const slot = d.pick.inventorySlot;
  if (slot < 0) return false;
  const item = d.inventory.inventory[slot];
  const info = item ? d.itemInfos.get(item.ItemIndex) : undefined;
  if (item) {
    const name = info ? friendlyName(info, item.Count) : "this item";
    if (info && (info.Bind & BindMode.DontDrop)) ui.refuse(T.YouCannotDrop(name));
    else if (item.Count === 1) ui.confirm(T.DropTip(name), () => d.inventory.drop(slot));
    else ui.amount(T.DropAmount, info?.Image ?? 0, item.Count, (n) => { if (n > 0) d.inventory.drop(slot, n); });
  }
  d.clearPick();
  return true;
}

/** MainDialog.GoldLabel's PickedUpGold + map click: clears it and asks how much gold to drop. */
export function dropHudGold(d: { pick: ItemPick; inventory: Inventory }, ui: DropPrompts): boolean {
  if (!d.pick.hudGold) return false;
  d.pick.setHudGold(false);
  askDropGold(d.inventory, ui);
  return true;
}

export interface ItemDropDeps {
  readonly pick: ItemPick;
  readonly inventory: Inventory;
  readonly itemInfos: ItemInfos;
  readonly hero: Hero;
  readonly heroItems: HeroItems;
  /** Clears the cell pick and redraws the cells (classic: InventoryWindow.clearPick; default: pick.clear()). */
  readonly clearPick?: () => void;
}

/** The map-click drop chain one UI uses: a picked hero item, then a picked bag item, then the HUD gold pick. */
export class ItemDrop {
  private readonly d: Required<ItemDropDeps>;

  constructor(deps: ItemDropDeps, private readonly ui: DropPrompts) {
    this.d = { ...deps, clearPick: deps.clearPick ?? (() => deps.pick.clear()) };
  }

  /** A left map click with something picked; true = the click was used (the caller does nothing else with it). */
  mapClick(): boolean {
    return dropHeroItem(this.d, this.ui) || dropBagItem(this.d, this.ui) || dropHudGold(this.d, this.ui);
  }

  /** The bag window's gold pick (ItemPick.gold) + map click. */
  dropGold(): boolean {
    if (!this.d.pick.gold) return false;
    this.d.pick.setGold(false);
    askDropGold(this.d.inventory, this.ui);
    return true;
  }
}
