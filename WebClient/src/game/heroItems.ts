// The hero's items: UserHeroObject.Inventory (belt = slots 0..HeroBeltIdx-1, bag after) and Equipment, kept in step with
// the server exactly as the hero branches of GameScene's handlers do (MoveItem / EquipItem / RemoveItem / MergeItem /
// CombineItem / UseItem / DropItem (HeroItem) / TakeBackHeroItem / TransferHeroItem / DeleteItem / RefreshItem /
// ItemRepaired / ItemSlotSizeChanged / ItemSealChanged / ItemUpgraded), plus the MirItemCell decisions that SEND the
// hero requests (UseItem / RemoveHeroItem / MoveItem's "To HeroInventory", "To HeroEquipment", "To HeroHPItem /
// HeroMPItem" and "To Inventory / From HeroInventory" cases) and the HeroInventoryDialog auto-pot buttons.
// The arrays live in hero.info (hero.ts); cell locks share Inventory.locked (UniqueIDs are unique across both owners).
// Feed packets AFTER Inventory.handle / ItemChanges.handle: those leave every hero grid alone (a MergeItem between a
// user grid and a hero grid is applied here, both sides).

import type { ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { EquipmentSlot, ItemType, MirGridType, Stat } from "../protocol/enums.generated.ts";
import { BELT_IDX, type Inventory } from "./inventory.ts";
import { HERO_BELT_IDX, type Hero, type HeroInfo } from "./hero.ts";

const T = { // ClientTextKeys (English.json)
  AreYouWantUsePotion: "Are you use you want to use this Potion?",
};

/** A UserItem standing for an ItemInfo (`new UserItem(GetItemInfo(index))`: the auto-pot cells). */
function displayItem(index: number): UserItem {
  return {
    UniqueID: 0n, ItemIndex: index, CurrentDura: 0, MaxDura: 0, Count: 1, SoulBoundId: -1, Identified: false, Cursed: false,
    Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [], RefinedValue: 0, RefineAdded: 0, RefineSuccessChance: 0,
    WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false, Sealed: null, GMMade: false,
  };
}

/** MirItemCell.CorrectSlot for an equipment cell. */
export function correctSlot(info: ItemInfo, slot: number): boolean {
  const t = info.Type;
  switch (slot) {
    case EquipmentSlot.Weapon: return t === ItemType.Weapon;
    case EquipmentSlot.Armour: return t === ItemType.Armour;
    case EquipmentSlot.Helmet: return t === ItemType.Helmet;
    case EquipmentSlot.Torch: return t === ItemType.Torch;
    case EquipmentSlot.Necklace: return t === ItemType.Necklace;
    case EquipmentSlot.BraceletL: return t === ItemType.Bracelet;
    case EquipmentSlot.BraceletR: return t === ItemType.Bracelet || t === ItemType.Amulet;
    case EquipmentSlot.RingL: case EquipmentSlot.RingR: return t === ItemType.Ring;
    case EquipmentSlot.Amulet: return t === ItemType.Amulet;
    case EquipmentSlot.Boots: return t === ItemType.Boots;
    case EquipmentSlot.Belt: return t === ItemType.Belt;
    case EquipmentSlot.Stone: return t === ItemType.Stone;
    case EquipmentSlot.Mount: return t === ItemType.Mount;
    default: return false;
  }
}

export class HeroItems {
  /** MirItemCell.CanWearItem(GameScene.Hero, item) before C.EquipItem: a refusal line ("" = silent) or null to allow.
   *  The default lets the server decide (the web client does not compute the hero's stats here). */
  canWear: (item: UserItem, slot: number) => string | null = () => null;
  /** Prints a client-side refusal (system chat line). */
  refuse: (text: string) => void = () => {};
  /** MirMessageBox Yes/No stand-in (the shape-4 potion question). */
  /** MirMessageBox YesNo: `yes` runs only on Yes (non-blocking; the UI sets this to messageBox). */
  ask: (text: string, yes: () => void) => void = (_text, yes) => yes();
  /** GameScene.UseItemTime for hero items. */
  private useItemTime = 0;
  private readonly display = new Map<number, UserItem>();

  constructor(private readonly hero: Hero, private readonly inv: Inventory, private readonly send: (frame: Uint8Array) => void) {}

  get info(): HeroInfo | null {
    return this.hero.info;
  }

  itemInfo(item: UserItem): ItemInfo | undefined {
    return this.inv.info(item);
  }

  // — State (GameScene handlers, hero branches) —

  private unlock(...ids: bigint[]): void {
    for (const id of ids) this.inv.locked.delete(String(id));
  }

  isLocked(item: UserItem | null | undefined): boolean {
    return !!item && this.inv.isLocked(item);
  }

  private lock(...items: (UserItem | null | undefined)[]): void {
    for (const i of items) if (i) this.inv.locked.add(String(i.UniqueID));
    this.inv.onChange();
    this.hero.changed();
  }

  /** MirItemCell.ItemArray for the grids a hero MergeItem can name. */
  private list(grid: number): (UserItem | null)[] | null {
    const h = this.hero.info;
    switch (grid) {
      case MirGridType.HeroInventory: return h?.inventory ?? null;
      case MirGridType.HeroEquipment: return h?.equipment ?? null;
      case MirGridType.Inventory: return this.inv.inventory;
      case MirGridType.Equipment: return this.inv.equipment;
      case MirGridType.Storage: return this.inv.storage;
      case MirGridType.Trade: return this.inv.trade;
      default: return null;
    }
  }

  private static isHeroGrid(grid: number): boolean {
    return grid === MirGridType.HeroInventory || grid === MirGridType.HeroEquipment;
  }

  /** The hero's item with this UniqueID (inventory, then equipment; top level only, as the desktop searches). */
  private find(id: bigint): UserItem | null {
    const h = this.hero.info;
    if (!h) return null;
    for (const list of [h.inventory, h.equipment]) for (const item of list) if (item?.UniqueID === id) return item;
    return null;
  }

  /** Feed every server packet after Inventory.handle; returns true when the hero's items changed (refresh hero stats). */
  handle(p: ServerPacket): boolean {
    const h = this.hero.info;
    switch (p.type) {
      case "MoveItem": {
        // GameScene.MoveItem, Grid = HeroInventory (HeroBeltDialog / HeroInventoryDialog cells)
        if (p.Grid !== MirGridType.HeroInventory || !h) return false;
        const list = h.inventory;
        if (p.From < 0 || p.To < 0 || p.From >= list.length || p.To >= list.length) return false;
        const a = list[p.From] ?? null, b = list[p.To] ?? null;
        if (a) this.unlock(a.UniqueID);
        if (b) this.unlock(b.UniqueID);
        if (!p.Success) break;
        list[p.From] = b;
        list[p.To] = a;
        break;
      }
      case "EquipItem": {
        // GameScene.EquipItem, Grid = HeroInventory: HeroInventoryDialog / HeroBeltDialog cell -> HeroDialog.Grid[To]
        if (p.Grid !== MirGridType.HeroInventory || !h) return false;
        this.unlock(p.UniqueID);
        const from = h.inventory.findIndex((i) => i?.UniqueID === p.UniqueID);
        if (from < 0 || p.To < 0 || p.To >= h.equipment.length) break;
        const old = h.equipment[p.To] ?? null;
        if (old) this.unlock(old.UniqueID);
        if (!p.Success) break;
        h.equipment[p.To] = h.inventory[from] ?? null;
        h.inventory[from] = old;
        break;
      }
      case "RemoveItem": {
        // GameScene.RemoveItem, Grid = HeroInventory: HeroDialog cell (found in Hero.Equipment) -> hero belt / bag [To]
        if (p.Grid !== MirGridType.HeroInventory || !h) return false;
        this.unlock(p.UniqueID);
        const from = h.equipment.findIndex((i) => i?.UniqueID === p.UniqueID);
        if (from < 0 || p.To < 0 || p.To >= h.inventory.length) break;
        if (!p.Success) break;
        h.inventory[p.To] = h.equipment[from] ?? null;
        h.equipment[from] = null;
        break;
      }
      case "MergeItem": {
        // GameScene.MergeItem with a HeroInventory / HeroEquipment side (Inventory.handle skips any hero grid)
        if (!HeroItems.isHeroGrid(p.GridFrom) && !HeroItems.isHeroGrid(p.GridTo)) return false;
        this.unlock(p.IDFrom, p.IDTo);
        if (!p.Success) break;
        const fromList = this.list(p.GridFrom), toList = this.list(p.GridTo);
        if (!fromList || !toList) break;
        const fi = fromList.findIndex((i) => i?.UniqueID === p.IDFrom), ti = toList.findIndex((i) => i?.UniqueID === p.IDTo);
        if (fi < 0 || ti < 0) break;
        const from = fromList[fi]!, to = toList[ti]!;
        const stack = this.inv.info(to)?.StackSize ?? 1;
        if (from.Count <= stack - to.Count) { to.Count += from.Count; fromList[fi] = null; }
        else { from.Count -= stack - to.Count; to.Count = stack; }
        break;
      }
      case "CombineItem": {
        // GameScene.CombineItem, Grid = HeroInventory
        if (p.Grid !== MirGridType.HeroInventory || !h) return false;
        const list = h.inventory;
        const fi = list.findIndex((i) => i?.UniqueID === p.IDFrom), ti = list.findIndex((i) => i?.UniqueID === p.IDTo);
        if (fi < 0 || ti < 0) return false;
        this.unlock(p.IDFrom, p.IDTo);
        if (p.Destroy) list[ti] = null;
        if (p.Success) {
          const from = list[fi]!;
          if (from.Count > 1) from.Count--;
          else list[fi] = null;
        }
        break;
      }
      case "UseItem": {
        // GameScene.UseItem, Grid = HeroInventory (the user's clicks and the hero's server-side auto-pot)
        if (p.Grid !== MirGridType.HeroInventory || !h) return false;
        const i = h.inventory.findIndex((x) => x?.UniqueID === p.UniqueID);
        if (i < 0) return false;
        this.unlock(p.UniqueID);
        if (!p.Success) break;
        const item = h.inventory[i]!;
        if (item.Count > 1) item.Count--;
        else h.inventory[i] = null;
        break;
      }
      case "DropItem": {
        // GameScene.DropItem, HeroItem = true
        if (!p.HeroItem || !h) return false;
        const i = h.inventory.findIndex((x) => x?.UniqueID === p.UniqueID);
        if (i < 0) return false;
        this.unlock(p.UniqueID);
        if (!p.Success) break;
        const item = h.inventory[i]!;
        if (p.Count === item.Count) h.inventory[i] = null;
        else item.Count -= p.Count;
        break;
      }
      case "TakeBackHeroItem": case "TransferHeroItem": {
        // GameScene.TakeBackHeroItem (hero belt / bag [From] -> user belt / bag [To]) / TransferHeroItem (the reverse)
        const back = p.type === "TakeBackHeroItem";
        const heroList = h?.inventory ?? [];
        const [fromList, toList] = back ? [heroList, this.inv.inventory] : [this.inv.inventory, heroList];
        const a = fromList[p.From] ?? null, b = toList[p.To] ?? null;
        if (a) this.unlock(a.UniqueID);
        if (b) this.unlock(b.UniqueID);
        if (!h || !p.Success || p.From < 0 || p.From >= fromList.length || p.To < 0 || p.To >= toList.length) break;
        toList[p.To] = a;
        fromList[p.From] = null;
        break;
      }
      case "DeleteItem": {
        // GameScene.DeleteItem, the Hero part (hero inventory, then equipment; slot items first). The desktop clears
        // User.Inventory[i] for a whole hero-inventory stack (a typo); this port clears the hero's cell.
        if (!h || !this.deleteItem(h, p.UniqueID, p.Count)) return false;
        break;
      }
      case "RefreshItem": {
        // GameScene.RefreshItem, the Hero part
        if (!h) return false;
        const i = h.inventory.findIndex((x) => x?.UniqueID === p.Item.UniqueID);
        if (i >= 0) { h.inventory[i] = p.Item; break; }
        const e = h.equipment.findIndex((x) => x?.UniqueID === p.Item.UniqueID);
        if (e < 0) return false;
        h.equipment[e] = p.Item;
        break;
      }
      case "ItemRepaired": {
        // GameScene.ItemRepaired, the Hero part (HeroObject sends it for the hero's own repair items)
        const item = this.find(p.UniqueID);
        if (!item) return false;
        item.MaxDura = p.MaxDura;
        item.CurrentDura = p.CurrentDura;
        break;
      }
      case "ItemSlotSizeChanged": {
        // GameScene.ItemSlotSizeChanged, the Hero part -> UserItem.SetSlotSize
        const item = this.find(p.UniqueID);
        if (!item || p.SlotSize < 0 || p.SlotSize === item.Slots.length) return false;
        if (p.SlotSize < item.Slots.length) item.Slots.length = p.SlotSize;
        else while (item.Slots.length < p.SlotSize) item.Slots.push(null);
        break;
      }
      case "ItemSealChanged": {
        // GameScene.ItemSealChanged, the Hero part
        const item = this.find(p.UniqueID);
        if (!item) return false;
        item.Sealed = { ExpiryDate: p.ExpiryDate, NextSealDate: 0n };
        break;
      }
      case "ItemUpgraded": {
        // GameScene.ItemUpgraded, the Hero part (hero inventory only; the grid effect is not drawn)
        const item = h?.inventory.find((x) => x?.UniqueID === p.Item.UniqueID);
        if (!item) return false;
        item.AddedStats = { ...p.Item.AddedStats };
        item.MaxDura = p.Item.MaxDura;
        item.RefineAdded = p.Item.RefineAdded;
        break;
      }
      default: return false;
    }
    this.inv.onChange();
    this.hero.changed();
    return true;
  }

  private deleteItem(h: HeroInfo, id: bigint, count: number): boolean {
    for (const list of [h.inventory, h.equipment]) {
      for (let i = 0; i < list.length; i++) {
        const item = list[i];
        if (!item) continue;
        for (let j = 0; j < item.Slots.length; j++) {
          const slot = item.Slots[j];
          if (!slot || slot.UniqueID !== id) continue;
          if (slot.Count === count) item.Slots[j] = null; else slot.Count -= count;
          return true;
        }
        if (item.UniqueID !== id) continue;
        if (item.Count === count) list[i] = null; else item.Count -= count;
        return true;
      }
    }
    return false;
  }

  // — Display helpers —

  /** UserHeroObject.HPItem[0] / MPItem[0]: the auto-pot potion as a display item, or null. */
  autoPotItem(grid: number): UserItem | null {
    const h = this.hero.info;
    const index = !h ? 0 : grid === MirGridType.HeroHPItem ? h.hpItemIndex : h.mpItemIndex;
    if (index <= 0) return null;
    let item = this.display.get(index);
    if (!item) { item = displayItem(index); this.display.set(index, item); }
    return item;
  }

  /** HeroAutoPotPreview.CountItem: how many of this potion the hero carries. */
  countItem(itemIndex: number): number {
    let n = 0;
    for (const item of this.hero.info?.inventory ?? []) if (item && item.ItemIndex === itemIndex) n += item.Count;
    return n;
  }

  // — Requests (MirItemCell, hero grids) —

  private canStack(a: UserItem, b: UserItem): boolean {
    const info = this.inv.info(b);
    return a.ItemIndex === b.ItemIndex && !!info && b.Count < info.StackSize;
  }

  private isAmulet(item: UserItem): boolean {
    return this.inv.info(item)?.Type === ItemType.Amulet;
  }

  private merge(gridFrom: number, gridTo: number, a: UserItem, b: UserItem): void {
    this.send(ClientPackets.MergeItem({ GridFrom: gridFrom, GridTo: gridTo, IDFrom: a.UniqueID, IDTo: b.UniqueID }));
    this.lock(a, b);
  }

  /** The hero is summoned and alive (MirItemCell: `GameScene.Hero == null || GameScene.Hero.Dead` returns). */
  private get alive(): HeroInfo | null {
    return this.hero.info && !this.hero.dead ? this.hero.info : null;
  }

  /** MirItemCell.MoveItem "To HeroInventory / From HeroInventory": merge onto a matching stack, else C.MoveItem (swap). */
  move(from: number, to: number): void {
    const h = this.alive;
    if (!h) return;
    const a = h.inventory[from], b = h.inventory[to] ?? null;
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.HeroInventory, MirGridType.HeroInventory, a, b); return; }
    this.send(ClientPackets.MoveItem({ Grid: MirGridType.HeroInventory, From: from, To: to }));
    this.lock(a, b);
  }

  /** MirItemCell.MoveItem "To HeroInventory / From HeroInventory" with Ctrl, after the YouWantCombineItems Yes. */
  combine(from: number, to: number): void {
    const h = this.alive;
    if (!h) return;
    const a = h.inventory[from], b = h.inventory[to];
    if (!a || !b || from === to || this.isLocked(a) || this.isLocked(b)) return;
    this.send(ClientPackets.CombineItem({ Grid: MirGridType.HeroInventory, IDFrom: a.UniqueID, IDTo: b.UniqueID }));
    this.lock(a, b);
  }

  /** MirItemCell.MoveItem "To HeroInventory / From Inventory": merge, else C.TransferHeroItem into this slot, else the
   *  first free hero slot from HeroBeltIdx. (The TooHeavyToTransfer pre-check needs the hero's stats: the server checks.) */
  transfer(from: number, to: number): void {
    const h = this.alive;
    if (!h) return;
    const a = this.inv.inventory[from], b = h.inventory[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Inventory, MirGridType.HeroInventory, a, b); return; }
    if (b) to = h.inventory.findIndex((i, n) => n >= HERO_BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.TransferHeroItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To Inventory / From HeroInventory": merge, else C.TakeBackHeroItem into this slot, else the
   *  first free user slot from BeltIdx. */
  takeBack(from: number, to: number): void {
    const h = this.alive;
    if (!h) return;
    const a = h.inventory[from], b = this.inv.inventory[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.HeroInventory, MirGridType.Inventory, a, b); return; }
    if (b) to = this.inv.inventory.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.TakeBackHeroItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To HeroEquipment / From HeroInventory": an amulet merges onto the worn stack, else
   *  CorrectSlot + CanWearItem(Hero) -> C.EquipItem with Grid = HeroInventory. */
  equip(from: number, slot: number): void {
    const h = this.alive;
    if (!h) return;
    const a = h.inventory[from], cur = h.equipment[slot] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(cur)) return;
    if (cur && this.isAmulet(a) && this.canStack(a, cur)) { this.merge(MirGridType.HeroInventory, MirGridType.HeroEquipment, a, cur); return; }
    const info = this.inv.info(a);
    if (info && !correctSlot(info, slot)) return;
    const refusal = this.canWear(a, slot);
    if (refusal !== null) { if (refusal) this.refuse(refusal); return; }
    this.send(ClientPackets.EquipItem({ Grid: MirGridType.HeroInventory, UniqueID: a.UniqueID, To: slot }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To HeroInventory / From HeroEquipment": an amulet merges, else (CanRemoveItem: a free hero
   *  slot) C.RemoveItem with Grid = HeroInventory into this slot or the first free bag slot. */
  unequipTo(slot: number, to: number): void {
    const h = this.alive;
    if (!h) return;
    const a = h.equipment[slot], b = h.inventory[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.isAmulet(a) && this.canStack(a, b)) { this.merge(MirGridType.HeroEquipment, MirGridType.HeroInventory, a, b); return; }
    if (!h.inventory.some((i) => !i)) return;
    if (b) to = h.inventory.findIndex((i, n) => n >= HERO_BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.RemoveItem({ Grid: MirGridType.HeroInventory, UniqueID: a.UniqueID, To: to }));
    this.lock(a);
  }

  /** MirItemCell.RemoveHeroItem (right-click on a hero equipment cell): merge onto the last matching hero stack, else
   *  the first free cell (amulets: belt first; everything else: bag first, then the belt). The desktop's merge names
   *  GridTo = Inventory (the server then looks in the user's bag and refuses); this port sends HeroInventory. */
  removeHeroItem(slot: number): void {
    const h = this.alive;
    if (!h) return;
    const item = h.equipment[slot];
    if (!item || this.isLocked(item) || !h.inventory.some((i) => !i)) return;
    const info = this.inv.info(item);
    if (info && info.StackSize > 1) {
      const stack = [...h.inventory].reverse().find((i) => i && i.ItemIndex === item.ItemIndex);
      if (stack && stack.Count + item.Count <= info.StackSize) {
        this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.HeroEquipment, GridTo: MirGridType.HeroInventory, IDFrom: item.UniqueID, IDTo: stack.UniqueID }));
        this.lock(item);
        return;
      }
    }
    let to = -1;
    if (info?.Type === ItemType.Amulet) to = h.inventory.findIndex((i) => !i);
    else {
      to = h.inventory.findIndex((i, n) => n >= HERO_BELT_IDX && !i);
      if (to < 0) to = h.inventory.findIndex((i, n) => n < HERO_BELT_IDX && !i);
    }
    if (to < 0) return;
    this.send(ClientPackets.RemoveItem({ Grid: MirGridType.HeroInventory, UniqueID: item.UniqueID, To: to }));
    this.lock(item);
  }

  /** MirItemCell.UseItem on a hero belt / bag cell: equip by type into HeroDialog's slot, or C.UseItem with
   *  Grid = HeroInventory for consumables (refilling an emptied hero belt cell from the bag). */
  use(index: number, now: number): void {
    const h = this.hero.info;
    if (!h) return;
    const item = h.inventory[index];
    if (!item || this.isLocked(item)) return;
    const info = this.inv.info(item);
    if (!info) return;
    const eq = (s: number) => h.equipment[s] ?? null;
    const wears = (s: number) => this.canWear(item, s) === null;
    let slot = -1;
    switch (info.Type) {
      case ItemType.Weapon: slot = EquipmentSlot.Weapon; break;
      case ItemType.Armour: slot = EquipmentSlot.Armour; break;
      case ItemType.Helmet: slot = EquipmentSlot.Helmet; break;
      case ItemType.Necklace: slot = EquipmentSlot.Necklace; break;
      case ItemType.Bracelet: {
        const r = eq(EquipmentSlot.BraceletR);
        slot = (!r || this.isAmulet(r)) && wears(EquipmentSlot.BraceletR) ? EquipmentSlot.BraceletR : EquipmentSlot.BraceletL;
        break;
      }
      case ItemType.Ring: slot = !eq(EquipmentSlot.RingR) && wears(EquipmentSlot.RingR) ? EquipmentSlot.RingR : EquipmentSlot.RingL; break;
      case ItemType.Amulet: {
        const cur = eq(EquipmentSlot.Amulet);
        if (cur && cur.ItemIndex === item.ItemIndex && cur.Count < info.StackSize) {
          this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.HeroInventory, GridTo: MirGridType.HeroEquipment, IDFrom: item.UniqueID, IDTo: cur.UniqueID }));
          this.lock(item);
          return;
        }
        slot = EquipmentSlot.Amulet;
        break;
      }
      case ItemType.Belt: slot = EquipmentSlot.Belt; break;
      case ItemType.Boots: slot = EquipmentSlot.Boots; break;
      case ItemType.Stone: slot = EquipmentSlot.Stone; break;
      case ItemType.Torch: slot = EquipmentSlot.Torch; break;
      case ItemType.Mount: slot = EquipmentSlot.Mount; break;
      case ItemType.Potion: case ItemType.Scroll: case ItemType.Book: case ItemType.Food: case ItemType.Script:
      case ItemType.Pets: case ItemType.Transform: case ItemType.Deco: case ItemType.MonsterSpawn: case ItemType.SealedHero: {
        if (now < this.useItemTime) return;
        if (info.Type === ItemType.Potion && info.Shape === 4) {
          this.ask(T.AreYouWantUsePotion, () => this.useNow(item, index, now));
          return;
        }
        this.useNow(item, index, now);
        return;
      }
      default: return; // mount / fishing / socket parts: UseSlotItem works on the user's own dialogs (not ported for the hero)
    }
    this.equip(index, slot);
  }

  /** The use half of UseItem (after the AreYouWantUsePotion box for Shape 4 potions). */
  private useNow(item: UserItem, index: number, now: number): void {
    const h = this.hero.info;
    if (!h) return;
    this.send(ClientPackets.UseItem({ UniqueID: item.UniqueID, Grid: MirGridType.HeroInventory }));
    const refill: UserItem[] = [];
    if (item.Count === 1 && index < HERO_BELT_IDX) {
      for (let i = HERO_BELT_IDX; i < h.inventory.length; i++) {
        const other = h.inventory[i];
        if (other && other.ItemIndex === item.ItemIndex) {
          this.send(ClientPackets.MoveItem({ Grid: MirGridType.HeroInventory, From: i, To: index }));
          refill.push(other);
          break;
        }
      }
    }
    this.lock(item, ...refill);
    this.useItemTime = now + 300;
  }

  /** GameScene map click with a hero cell picked: C.DropItem with HeroInventory = true (`count` defaults to the stack). */
  drop(index: number, count?: number): void {
    const item = this.hero.info?.inventory[index];
    if (!item || this.isLocked(item)) return;
    const n = Math.min(item.Count, Math.max(1, Math.trunc(count ?? item.Count)));
    this.send(ClientPackets.DropItem({ UniqueID: item.UniqueID, Count: n, HeroInventory: true }));
    this.lock(item);
  }

  /** MirItemCell.MoveItem "To HeroHPItem / HeroMPItem / From HeroInventory": potions of Shape 0-1 only. */
  setAutoPotItem(grid: number, from: number): void {
    const h = this.alive;
    if (!h) return;
    const item = h.inventory[from];
    const info = item && this.inv.info(item);
    if (!info || info.Type !== ItemType.Potion || info.Shape > 1) return;
    this.send(ClientPackets.SetAutoPotItem({ Grid: grid, ItemIndex: info.Index }));
  }

  /** MirItemCell left-click on a filled HeroHPItem / HeroMPItem cell with nothing picked: clear it (ItemIndex 0). */
  clearAutoPotItem(grid: number): void {
    if (!this.autoPotItem(grid)) return;
    this.send(ClientPackets.SetAutoPotItem({ Grid: grid, ItemIndex: 0 }));
  }

  /** HeroInventoryDialog.HPButton / MPButton: MirAmountBox (max 99) -> C.SetAutoPotValue. */
  setAutoPotValue(stat: typeof Stat.HP | typeof Stat.MP, value: number): void {
    if (!this.hero.info?.autoPot || !Number.isFinite(value)) return;
    this.send(ClientPackets.SetAutoPotValue({ Stat: stat, Value: Math.min(99, Math.max(0, Math.trunc(value))) }));
  }
}
