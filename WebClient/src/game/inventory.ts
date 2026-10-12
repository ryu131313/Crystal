// The user's items: User.Inventory (belt = slots 0..BeltIdx-1, bag after), User.Equipment and gold, kept in step
// with the server exactly as GameScene's handlers do (MoveItem / EquipItem / RemoveItem / MergeItem / UseItem /
// DropItem / GainedItem (AddItem) / DeleteItem / RefreshItem / DuraChanged / ItemRepaired / SplitItem(1)), plus the
// MirItemCell decisions that SEND those requests (UseItem's equip-slot choice, RemoveItem's merge-or-free-slot).
// Grids: Inventory, Equipment and Trade (User.Trade, TradeGoldAmount, TradeLocked: GameScene.DepositTradeItem /
// RetrieveTradeItem / TradeConfirm / TradeCancel, TradeDialog.ChangeLockState / TradeReset) and GuildStorage
// (GuildDialog.StorageGrid: GameScene.GuildStorageItemChange / GuildStorageList). NPC storage (GameScene.Storage / StorageDialog.Grid:
// UserStorage / StoreItem / TakeBackItem / ResizeStorage, and Grid = Storage in MoveItem / EquipItem / RemoveItem /
// MergeItem / SplitItem(1) / DeleteItem). Hero grids are kept by heroItems.ts: every packet naming a hero grid (MoveItem /
// EquipItem / RemoveItem / UseItem with Grid = HeroInventory, DropItem.HeroItem, a MergeItem with a hero side) is left alone here.
// Locks mirror MirItemCell.Locked: a cell stays locked until the server answers.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ItemType, MirGridType, EquipmentSlot } from "../protocol/enums.generated.ts";
import type { ItemInfos } from "./items.ts";

export const BELT_IDX = 6; // UserObject.BeltIdx
export const TRADE_SLOTS = 10; // UserObject.Trade
export const GUILD_STORAGE_SLOTS = 112; // GuildDialog.StorageGrid (8 x 14) = GuildInfo.StoredItems
export const STORAGE_GRID_SIZE = 80; // Globals.StorageGridSize: one StorageDialog page (the expanded page is slots 80..159)
export const REFINE_SLOTS = 16; // RefineDialog.Grid (4 x 4) = GameScene.Refine

export class Inventory {
  inventory: (UserItem | null)[] = [];
  equipment: (UserItem | null)[] = [];
  gold = 0;
  credit = 0;
  /** UserObject.Trade (TradeDialog grid, 5 x 2). */
  trade: (UserItem | null)[] = new Array<UserItem | null>(TRADE_SLOTS).fill(null);
  /** UserObject.TradeGoldAmount: gold this side has put up (the server already took it with S.LoseGold). */
  tradeGold = 0;
  /** UserObject.TradeLocked (TradeDialog.ChangeLockState): any change to either offer unlocks. */
  tradeLocked = false;
  /** GuildDialog.StorageGrid items (the server sends the list once per login, then every change to the whole guild). */
  guildStorage: (UserItem | null)[] = new Array<UserItem | null>(GUILD_STORAGE_SLOTS).fill(null);
  /** UserObject.Id (UserInformation.RealId): GuildStorageItemChange.User is compared with it. */
  realId = -1;
  /** GameScene.Storage (NPC storage; S.UserStorage replaces it, S.ResizeStorage resizes it). */
  storage: (UserItem | null)[] = new Array<UserItem | null>(STORAGE_GRID_SIZE).fill(null);
  /** GameScene.Refine (RefineDialog.Grid): items put up for refining; the server hands them back on C.RefineCancel. */
  refine: (UserItem | null)[] = new Array<UserItem | null>(REFINE_SLOTS).fill(null);
  /** UniqueIDs (as strings) whose cells are locked waiting for a server answer. */
  readonly locked = new Set<string>();
  onChange: () => void = () => {};
  /** MirItemCell.CanWearItem before C.EquipItem: a refusal line ("" = silent), or null to allow. Set by the game once stats exist. */
  canWear: (item: UserItem, slot: number) => string | null = () => null;
  /** Prints a client-side refusal (system chat line). */
  refuse: (text: string) => void = () => {};
  /** MirItemCell.UseSlotItem (sockets, mount and fishing parts): set to ItemChanges.useSlotItem by the game. */
  useSlotItem: (index: number) => void = () => {};
  private readonly infos: ItemInfos;
  private readonly send: (frame: Uint8Array) => void;
  private useItemTime = 0;

  constructor(infos: ItemInfos, send: (frame: Uint8Array) => void) {
    this.infos = infos;
    this.send = send;
  }

  info(item: UserItem) {
    return this.infos.get(item.ItemIndex);
  }

  private findInventory(id: bigint): number {
    return this.inventory.findIndex((i) => i?.UniqueID === id);
  }

  /** The item list of a grid this port keeps (Inventory, Equipment, Trade). */
  private list(grid: number): (UserItem | null)[] | null {
    if (grid === MirGridType.Inventory) return this.inventory;
    if (grid === MirGridType.Equipment) return this.equipment;
    if (grid === MirGridType.Trade) return this.trade;
    if (grid === MirGridType.Storage) return this.storage;
    if (grid === MirGridType.Refine) return this.refine;
    return null;
  }

  /** TradeDialog.TradeReset: the trade grid, gold and lock are cleared (the server already moved the items). */
  private tradeReset(): void {
    this.trade.fill(null);
    this.tradeGold = 0;
    this.tradeLocked = false;
  }

  private findEquipment(id: bigint): number {
    return this.equipment.findIndex((i) => i?.UniqueID === id);
  }

  /** The item with this UniqueID anywhere in inventory/equipment, slot items included (DuraChanged walks slots too). */
  private findAnywhere(id: bigint): UserItem | null {
    for (const list of [this.inventory, this.equipment]) {
      for (const item of list) {
        if (!item) continue;
        if (item.UniqueID === id) return item;
        for (const slot of item.Slots) if (slot?.UniqueID === id) return slot;
      }
    }
    return null;
  }

  private unlock(...ids: bigint[]): void {
    for (const id of ids) this.locked.delete(String(id));
  }

  isLocked(item: UserItem | null): boolean {
    return !!item && this.locked.has(String(item.UniqueID));
  }

  /** Feed every server packet; returns true when the inventory changed (looks may need a refresh). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "UserInformation":
        this.inventory = p.Inventory ?? [];
        this.equipment = p.Equipment ?? [];
        this.gold = p.Gold;
        this.credit = p.Credit;
        this.locked.clear();
        this.tradeReset();
        this.realId = p.RealId;
        this.refine.fill(null);
        break;
      case "GuildStorageList":
        // GameScene.GuildStorageList
        for (let i = 0; i < p.Items.length && i < GUILD_STORAGE_SLOTS; i++) this.guildStorage[i] = p.Items[i]?.Item ?? null;
        break;
      case "GuildStorageItemChange": this.guildStorageChange(p); break;
      // — NPC storage (GameScene.UserStorage / StoreItem / TakeBackItem / ResizeInventory / ResizeStorage) —
      case "UserStorage": this.storage = p.Storage ?? []; break;
      case "StoreItem": case "TakeBackItem": {
        const [from, to] = p.type === "StoreItem" ? [this.inventory, this.storage] : [this.storage, this.inventory];
        const a = from[p.From], b = to[p.To];
        if (a) this.unlock(a.UniqueID);
        if (b) this.unlock(b.UniqueID);
        if (!p.Success) break;
        to[p.To] = a ?? null;
        from[p.From] = null;
        break;
      }
      case "ResizeInventory": this.inventory.length = p.Size; this.inventory = Array.from(this.inventory, (i) => i ?? null); break;
      case "ResizeStorage": this.storage.length = p.Size; this.storage = Array.from(this.storage, (i) => i ?? null); break;
      case "MoveItem": {
        // GameScene.MoveItem (Inventory or Trade grid; a Trade move unlocks the deal)
        if (p.Grid !== MirGridType.Inventory && p.Grid !== MirGridType.Trade && p.Grid !== MirGridType.Refine && p.Grid !== MirGridType.Storage) return false;
        const list = this.list(p.Grid)!;
        const a = list[p.From], b = list[p.To];
        if (a) this.unlock(a.UniqueID);
        if (b) this.unlock(b.UniqueID);
        if (p.Grid === MirGridType.Trade) this.tradeLocked = false;
        if (!p.Success) break;
        list[p.From] = b ?? null;
        list[p.To] = a ?? null;
        break;
      }
      case "DepositRefineItem": case "RetrieveRefineItem": {
        // GameScene.DepositRefineItem / RetrieveRefineItem (the server's C.RefineCancel answer is a RetrieveRefineItem per item)
        const [from, to] = p.type === "DepositRefineItem" ? [this.inventory, this.refine] : [this.refine, this.inventory];
        const item = from[p.From];
        if (item) this.unlock(item.UniqueID);
        if (!p.Success || !item) break;
        to[p.To] = item;
        from[p.From] = null;
        break;
      }
      case "RefineItem": {
        // GameScene.RefineItem: RefineDialog.RefineReset (the server kept the grid) and the refined item leaves the bag
        this.refine.fill(null);
        const i = this.findInventory(p.UniqueID);
        if (i >= 0) this.inventory[i] = null;
        break;
      }
      case "RefineCancel": this.refine.fill(null); break; // GameScene.RefineCancel (RefineReset)
      case "DepositTradeItem": case "RetrieveTradeItem": {
        // GameScene.DepositTradeItem / RetrieveTradeItem
        const [from, to] = p.type === "DepositTradeItem" ? [this.inventory, this.trade] : [this.trade, this.inventory];
        const item = from[p.From];
        if (item) this.unlock(item.UniqueID);
        this.tradeLocked = false;
        if (!p.Success || !item) break;
        to[p.To] = item;
        from[p.From] = null;
        break;
      }
      case "TradeGold": case "TradeItem": case "TradeAccept": this.tradeLocked = false; break;
      case "TradeConfirm": this.tradeReset(); break;
      case "TradeCancel": if (p.Unlock) this.tradeLocked = false; else this.tradeReset(); break;
      case "EquipItem": {
        // GameScene.EquipItem (Grid = Storage: StorageDialog.GetCell ?? BeltDialog.GetCell)
        if (p.Grid !== MirGridType.Inventory && p.Grid !== MirGridType.Storage) return false;
        this.unlock(p.UniqueID);
        let src = p.Grid === MirGridType.Storage ? this.storage : this.inventory;
        let from = src.findIndex((i) => i?.UniqueID === p.UniqueID);
        if (from < 0 && src === this.storage) { src = this.inventory; from = this.findInventory(p.UniqueID); }
        if (from < 0 || !p.Success) break;
        const old = this.equipment[p.To] ?? null;
        if (old) this.unlock(old.UniqueID);
        this.equipment[p.To] = src[from] ?? null;
        src[from] = old;
        break;
      }
      case "RemoveItem": {
        // GameScene.RemoveItem (Grid = Storage: into StorageDialog.Grid[To])
        if (p.Grid !== MirGridType.Inventory && p.Grid !== MirGridType.Storage) return false;
        this.unlock(p.UniqueID);
        const from = this.findEquipment(p.UniqueID);
        if (from < 0 || !p.Success) break;
        (p.Grid === MirGridType.Storage ? this.storage : this.inventory)[p.To] = this.equipment[from] ?? null;
        this.equipment[from] = null;
        break;
      }
      case "MergeItem": {
        this.unlock(p.IDFrom, p.IDTo);
        if (p.GridFrom === MirGridType.Trade || p.GridTo === MirGridType.Trade) this.tradeLocked = false;
        if (!p.Success) break;
        const fromList = this.list(p.GridFrom), toList = this.list(p.GridTo);
        if (!fromList || !toList) break;
        const fi = fromList.findIndex((i) => i?.UniqueID === p.IDFrom), ti = toList.findIndex((i) => i?.UniqueID === p.IDTo);
        if (fi < 0 || ti < 0) break;
        const from = fromList[fi]!, to = toList[ti]!;
        const stack = this.info(to)?.StackSize ?? 1;
        if (from.Count <= stack - to.Count) { to.Count += from.Count; fromList[fi] = null; }
        else { from.Count -= stack - to.Count; to.Count = stack; }
        break;
      }
      case "UseItem": {
        this.unlock(p.UniqueID);
        if (p.Grid !== MirGridType.Inventory || !p.Success) break;
        const i = this.findInventory(p.UniqueID);
        if (i < 0) break;
        if (this.inventory[i]!.Count > 1) this.inventory[i]!.Count--;
        else this.inventory[i] = null;
        break;
      }
      case "DropItem": {
        this.unlock(p.UniqueID);
        if (p.HeroItem || !p.Success) break;
        const i = this.findInventory(p.UniqueID);
        if (i < 0) break;
        if (p.Count === this.inventory[i]!.Count) this.inventory[i] = null;
        else this.inventory[i]!.Count -= p.Count;
        break;
      }
      case "GainedItem": this.addItem(p.Item); break;
      case "SplitItem":
        if (p.Item && p.Grid === MirGridType.Inventory) this.placeSplit(p.Item);
        else if (p.Item && p.Grid === MirGridType.Storage) this.placeStorageSplit(p.Item);
        break;
      case "SplitItem1": {
        this.unlock(p.UniqueID);
        if ((p.Grid !== MirGridType.Inventory && p.Grid !== MirGridType.Storage) || !p.Success) break;
        const item = p.Grid === MirGridType.Storage ? this.storage.find((i) => i?.UniqueID === p.UniqueID) : this.findAnywhere(p.UniqueID);
        if (item) item.Count -= p.Count;
        break;
      }
      case "DeleteItem": this.deleteItem(p.UniqueID, p.Count); break;
      case "RefreshItem": {
        const i = this.findInventory(p.Item.UniqueID);
        if (i >= 0) { this.inventory[i] = p.Item; break; }
        const e = this.findEquipment(p.Item.UniqueID);
        if (e >= 0) this.equipment[e] = p.Item;
        else return false;
        break;
      }
      case "DuraChanged": { const item = this.findAnywhere(p.UniqueID); if (!item) return false; item.CurrentDura = p.CurrentDura; break; }
      case "ItemRepaired": { const item = this.findAnywhere(p.UniqueID); if (!item) return false; item.MaxDura = p.MaxDura; item.CurrentDura = p.CurrentDura; break; }
      case "SellItem": {
        this.unlock(p.UniqueID);
        if (!p.Success) break;
        const i = this.findInventory(p.UniqueID);
        if (i < 0) break;
        if (p.Count === this.inventory[i]!.Count) this.inventory[i] = null;
        else this.inventory[i]!.Count -= p.Count;
        break;
      }
      case "RepairItem": this.unlock(p.UniqueID); break;
      case "ConsignItem": {
        // GameScene.ConsignItem (inventory or belt cell: unlock; on success the item leaves the bag)
        const i = this.findInventory(p.UniqueID);
        if (i < 0) return false;
        this.unlock(p.UniqueID);
        if (p.Success) this.inventory[i] = null;
        break;
      }
      case "GainedGold": this.gold += p.Gold; break;
      case "LoseGold": this.gold -= p.Gold; break;
      case "GainedCredit": this.credit += p.Credit; break; // GameScene.GainedCredit / LoseCredit
      case "LoseCredit": this.credit -= p.Credit; break;
      default: return false;
    }
    this.onChange();
    return true;
  }

  /** GameScene.GuildStorageItemChange (the cells unlock on every answer, success or refusal). */
  private guildStorageChange(p: Extract<ServerPacket, { type: "GuildStorageItemChange" }>): void {
    const g = this.guildStorage, inv = this.inventory;
    const unlockAt = (...items: (UserItem | null | undefined)[]) => { for (const i of items) if (i) this.unlock(i.UniqueID); };
    const mine = p.User === this.realId;
    switch (p.Type) {
      case 0: // store
        if (p.To < 0 || p.To >= g.length || !p.Item) return;
        g[p.To] = p.Item.Item;
        unlockAt(p.Item.Item);
        if (mine) inv[p.From] = null;
        break;
      case 1: { // retrieve
        const item = g[p.From];
        unlockAt(item);
        if (mine && item) inv[p.To] = item;
        g[p.From] = null;
        break;
      }
      case 2: // move (swap)
        if (!p.Item) return;
        unlockAt(g[p.From], g[p.To]);
        g[p.From] = g[p.To] ?? null;
        g[p.To] = p.Item.Item;
        break;
      case 3: unlockAt(inv[p.From], g[p.To]); break; // store refused
      case 4: unlockAt(inv[p.To], g[p.From]); break; // retrieve refused
      case 5: unlockAt(g[p.From], g[p.To]); break; // move refused
    }
  }

  /** MirItemCell "To GuildStorage / From Inventory" (empty target only; rights are checked by the caller). */
  guildStore(from: number, to: number): void {
    const a = this.inventory[from];
    if (!a || this.isLocked(a) || this.guildStorage[to]) return;
    this.send(ClientPackets.GuildStorageItemChange({ Type: 0, From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To Inventory / From GuildStorage" (empty target only). */
  guildRetrieve(from: number, to: number): void {
    const a = this.guildStorage[from];
    if (!a || this.isLocked(a) || this.inventory[to]) return;
    this.send(ClientPackets.GuildStorageItemChange({ Type: 1, From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To GuildStorage / From GuildStorage" (the server swaps). */
  guildMove(from: number, to: number): void {
    const a = this.guildStorage[from], b = this.guildStorage[to];
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    this.send(ClientPackets.GuildStorageItemChange({ Type: 2, From: from, To: to }));
    this.lock(a, ...(b ? [b] : []));
  }

  /** GameScene.SplitItem: the split-off stack goes to a free slot WITHOUT stacking (potions / scrolls belt 0..3, amulets
   *  belt 4..5, else the bag, else the belt). */
  private placeSplit(item: UserItem): void {
    const info = this.info(item), t = info?.Type;
    const put = (from: number, to: number) => {
      for (let i = from; i < to; i++) if (!this.inventory[i]) { this.inventory[i] = item; return true; }
      return false;
    };
    if (t === ItemType.Potion || t === ItemType.Scroll || (t === ItemType.Script && info?.Effect === 1)) { if (put(0, 4)) return; }
    else if (t === ItemType.Amulet) { if (put(4, BELT_IDX)) return; }
    if (put(BELT_IDX, this.inventory.length)) return;
    put(0, BELT_IDX);
  }

  /** GameScene.AddItem: stack first, then potions/scrolls to the belt (0..BeltIdx-3), amulets to belt 4..5, else the bag, else anywhere. */
  private addItem(item: UserItem): void {
    const info = this.info(item);
    if (info && info.StackSize > 1) {
      for (const temp of this.inventory) {
        if (!temp || temp.ItemIndex !== item.ItemIndex || temp.Count >= info.StackSize) continue;
        if (item.Count + temp.Count <= info.StackSize) { temp.Count += item.Count; return; }
        item.Count -= info.StackSize - temp.Count;
        temp.Count = info.StackSize;
      }
    }
    const put = (from: number, to: number) => {
      for (let i = from; i < to; i++) if (!this.inventory[i]) { this.inventory[i] = item; return true; }
      return false;
    };
    const t = info?.Type;
    if (t === ItemType.Potion || t === ItemType.Scroll || (t === ItemType.Script && info?.Effect === 1)) { if (put(0, BELT_IDX - 2)) return; }
    else if (t === ItemType.Amulet) { if (put(4, BELT_IDX)) return; }
    else if (put(BELT_IDX, this.inventory.length)) return;
    put(0, this.inventory.length);
  }

  private deleteItem(id: bigint, count: number): void {
    for (const list of [this.inventory, this.equipment]) {
      for (let i = 0; i < list.length; i++) {
        const item = list[i];
        if (!item) continue;
        for (let j = 0; j < item.Slots.length; j++) {
          const slot = item.Slots[j];
          if (!slot || slot.UniqueID !== id) continue;
          if (slot.Count === count) item.Slots[j] = null; else slot.Count -= count;
          return;
        }
        if (item.UniqueID !== id) continue;
        if (item.Count === count) list[i] = null; else item.Count -= count;
        return;
      }
    }
    // GameScene.DeleteItem: not on the user (or hero) -> GameScene.Storage
    for (let i = 0; i < this.storage.length; i++) {
      const item = this.storage[i];
      if (!item || item.UniqueID !== id) continue;
      if (item.Count === count) this.storage[i] = null; else item.Count -= count;
      return;
    }
  }

  // — NPC storage (StorageDialog.Grid, MirGridType.Storage) —

  /** GameScene.SplitItem with Grid = Storage: the first free slot from BeltIdx, then 0..BeltIdx-1 (the desktop applies
   *  the inventory's belt offset to the storage array too; the type-based belt rule is Inventory-only). */
  private placeStorageSplit(item: UserItem): void {
    const s = this.storage;
    for (let i = BELT_IDX; i < s.length; i++) if (!s[i]) { s[i] = item; return; }
    for (let i = 0; i < BELT_IDX && i < s.length; i++) if (!s[i]) { s[i] = item; return; }
  }

  /** MirItemCell.MoveItem "To Storage / From Inventory": merge onto a matching stack, else C.StoreItem into this slot,
   *  else into the first free storage slot. */
  storeItem(from: number, to: number): void {
    const a = this.inventory[from], b = this.storage[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Inventory, MirGridType.Storage, a, b); return; }
    if (b) to = this.storage.findIndex((i) => !i);
    if (to < 0) return;
    this.send(ClientPackets.StoreItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To Inventory / From Storage": merge, else C.TakeBackItem into this slot, else the first free
   *  slot from 6 (the bag). */
  takeBackItem(from: number, to: number): void {
    const a = this.storage[from], b = this.inventory[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Storage, MirGridType.Inventory, a, b); return; }
    if (b) to = this.inventory.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.TakeBackItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To Storage / From Storage": merge, else C.MoveItem with Grid = Storage (the server swaps). */
  storageMove(from: number, to: number): void {
    const a = this.storage[from], b = this.storage[to] ?? null;
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Storage, MirGridType.Storage, a, b); return; }
    this.send(ClientPackets.MoveItem({ Grid: MirGridType.Storage, From: from, To: to }));
    this.lock(a, ...(b ? [b] : []));
  }

  /** MirItemCell.MoveItem "To Equipment / From Storage": an amulet merges onto the worn stack, else C.EquipItem with
   *  Grid = Storage after CanWearItem. */
  storageEquip(from: number, slot: number): void {
    const a = this.storage[from], cur = this.equipment[slot] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(cur)) return;
    if (cur && this.info(a)?.Type === ItemType.Amulet && this.canStack(a, cur)) { this.merge(MirGridType.Storage, MirGridType.Equipment, a, cur); return; }
    const refusal = this.canWear(a, slot);
    if (refusal !== null) { if (refusal) this.refuse(refusal); return; }
    this.send(ClientPackets.EquipItem({ Grid: MirGridType.Storage, UniqueID: a.UniqueID, To: slot }));
    this.lock(a);
  }

  /** MirItemCell.MoveItem "To Storage / From Equipment": merge, else (CanRemoveItem: a free storage slot) C.RemoveItem
   *  with Grid = Storage into this slot or the first free one. */
  unequipToStorage(slot: number, to: number): void {
    const a = this.equipment[slot], b = this.storage[to] ?? null;
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Equipment, MirGridType.Storage, a, b); return; }
    if (!this.storage.some((i) => !i)) return;
    if (b) to = this.storage.findIndex((i) => !i);
    this.send(ClientPackets.RemoveItem({ Grid: MirGridType.Storage, UniqueID: a.UniqueID, To: to }));
    this.lock(a);
  }

  /** MirItemCell.UseItem on a storage cell: equippable types go straight on (C.EquipItem with Grid = Storage, the same
   *  slot choice as the inventory); consumables are Inventory-only on the desktop, so nothing happens. */
  storageUse(index: number): void {
    const item = this.storage[index];
    if (!item || this.isLocked(item)) return;
    const info = this.info(item);
    if (!info) return;
    const eq = (s: number) => this.equipment[s] ?? null;
    const slots: Partial<Record<number, number>> = {
      [ItemType.Weapon]: EquipmentSlot.Weapon, [ItemType.Armour]: EquipmentSlot.Armour, [ItemType.Helmet]: EquipmentSlot.Helmet,
      [ItemType.Necklace]: EquipmentSlot.Necklace, [ItemType.Belt]: EquipmentSlot.Belt, [ItemType.Boots]: EquipmentSlot.Boots,
      [ItemType.Stone]: EquipmentSlot.Stone, [ItemType.Torch]: EquipmentSlot.Torch, [ItemType.Mount]: EquipmentSlot.Mount,
      [ItemType.Amulet]: EquipmentSlot.Amulet,
    };
    let slot = slots[info.Type] ?? -1;
    if (info.Type === ItemType.Bracelet) {
      const r = eq(EquipmentSlot.BraceletR);
      slot = !r || this.info(r)?.Type === ItemType.Amulet ? EquipmentSlot.BraceletR : EquipmentSlot.BraceletL;
    } else if (info.Type === ItemType.Ring) slot = !eq(EquipmentSlot.RingR) ? EquipmentSlot.RingR : EquipmentSlot.RingL;
    if (slot >= 0) this.storageEquip(index, slot);
  }

  /** MirItemCell Shift+left-click on a storage cell: split `count` off (C.SplitItem with Grid = Storage). */
  storageSplit(index: number, count: number): string | null {
    const item = this.storage[index];
    if (!item || this.isLocked(item) || item.Count <= 1) return null;
    if (!this.storage.some((i) => !i)) return "No room to split stack.";
    if (!(count > 0) || count >= item.Count) return null;
    this.send(ClientPackets.SplitItem({ Grid: MirGridType.Storage, UniqueID: item.UniqueID, Count: Math.trunc(count) }));
    this.lock(item);
    return null;
  }

  // — Requests (MirItemCell) —

  private lock(...items: UserItem[]): void {
    for (const i of items) this.locked.add(String(i.UniqueID));
    this.onChange();
  }

  /** Inventory slot -> inventory slot (MirItemCell.MoveItem within one grid; merges onto a matching stack). */
  move(from: number, to: number): void {
    const a = this.inventory[from], b = this.inventory[to];
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    const info = this.info(a);
    if (b && b.ItemIndex === a.ItemIndex && info && info.StackSize > 1 && b.Count < info.StackSize) {
      this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.Inventory, GridTo: MirGridType.Inventory, IDFrom: a.UniqueID, IDTo: b.UniqueID }));
      this.lock(a, b);
      return;
    }
    this.send(ClientPackets.MoveItem({ Grid: MirGridType.Inventory, From: from, To: to }));
    this.lock(a, ...(b ? [b] : []));
  }

  /** MirItemCell "To Trade / From Inventory": merge onto a matching stack, else deposit into this slot or the first free one. */
  deposit(from: number, to: number): void {
    const a = this.inventory[from], b = this.trade[to];
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Inventory, MirGridType.Trade, a, b); return; }
    if (b) to = this.trade.findIndex((i) => !i);
    if (to < 0) return;
    this.send(ClientPackets.DepositTradeItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To Inventory / From Trade": merge, else retrieve into this slot or the first free bag slot. */
  retrieve(from: number, to: number): void {
    const a = this.trade[from], b = this.inventory[to];
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Trade, MirGridType.Inventory, a, b); return; }
    if (b) to = this.inventory.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.RetrieveTradeItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To Trade / From Trade": merge or C.MoveItem with Grid = Trade. */
  tradeMove(from: number, to: number): void {
    const a = this.trade[from], b = this.trade[to];
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Trade, MirGridType.Trade, a, b); return; }
    this.send(ClientPackets.MoveItem({ Grid: MirGridType.Trade, From: from, To: to }));
    this.lock(a, ...(b ? [b] : []));
  }

  /** MirItemCell "To Refine / From Inventory": merge onto a matching stack, else C.DepositRefineItem into this slot. */
  refineDeposit(from: number, to: number): void {
    const a = this.inventory[from], b = this.refine[to];
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Inventory, MirGridType.Refine, a, b); return; }
    this.send(ClientPackets.DepositRefineItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To Inventory / From Refine": merge, else retrieve into this slot or the first free bag slot. */
  refineRetrieve(from: number, to: number): void {
    const a = this.refine[from], b = this.inventory[to];
    if (!a || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Refine, MirGridType.Inventory, a, b); return; }
    if (b) to = this.inventory.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) return;
    this.send(ClientPackets.RetrieveRefineItem({ From: from, To: to }));
    this.lock(a);
  }

  /** MirItemCell "To Refine / From Refine": merge or C.MoveItem with Grid = Refine. */
  refineMove(from: number, to: number): void {
    const a = this.refine[from], b = this.refine[to];
    if (!a || from === to || this.isLocked(a) || this.isLocked(b)) return;
    if (b && this.canStack(a, b)) { this.merge(MirGridType.Refine, MirGridType.Refine, a, b); return; }
    this.send(ClientPackets.MoveItem({ Grid: MirGridType.Refine, From: from, To: to }));
    this.lock(a, ...(b ? [b] : []));
  }

  /** TradeDialog.GoldLabel click: put more gold up (the amount box is capped at the user's gold). */
  offerGold(amount: number): void {
    if (!(amount > 0) || amount > this.gold) return;
    this.tradeGold += amount;
    this.send(ClientPackets.TradeGold({ Amount: amount }));
    this.onChange();
  }

  /** TradeDialog.ConfirmButton: toggle the lock and tell the server. */
  toggleTradeLock(): void {
    this.tradeLocked = !this.tradeLocked;
    this.send(ClientPackets.TradeConfirm({ Locked: this.tradeLocked }));
    this.onChange();
  }

  private canStack(a: UserItem, b: UserItem): boolean {
    const info = this.info(b);
    return a.ItemIndex === b.ItemIndex && !!info && b.Count < info.StackSize;
  }

  private merge(gridFrom: number, gridTo: number, a: UserItem, b: UserItem): void {
    this.send(ClientPackets.MergeItem({ GridFrom: gridFrom, GridTo: gridTo, IDFrom: a.UniqueID, IDTo: b.UniqueID }));
    this.lock(a, b);
  }

  /** Inventory item onto an equipment slot (MirItemCell drop on the character grid). */
  equip(from: number, slot: number): void {
    const a = this.inventory[from];
    if (!a || this.isLocked(a) || this.isLocked(this.equipment[slot] ?? null)) return;
    const refusal = this.canWear(a, slot);
    if (refusal !== null) { if (refusal) this.refuse(refusal); return; }
    this.send(ClientPackets.EquipItem({ Grid: MirGridType.Inventory, UniqueID: a.UniqueID, To: slot }));
    this.lock(a);
  }

  /** Equipment item into an inventory slot (`to`), or MirItemCell.RemoveItem's choice when `to` is omitted. */
  unequip(slot: number, to?: number): void {
    const item = this.equipment[slot];
    if (!item || this.isLocked(item)) return;
    const info = this.info(item);
    if (to === undefined) {
      if (!this.inventory.some((i) => !i)) return;
      if (info && info.StackSize > 1) {
        const stack = [...this.inventory].reverse().find((i) => i && i.ItemIndex === item.ItemIndex);
        if (stack && stack.Count + item.Count <= info.StackSize) {
          this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.Equipment, GridTo: MirGridType.Inventory, IDFrom: item.UniqueID, IDTo: stack.UniqueID }));
          this.lock(item);
          return;
        }
      }
      to = this.inventory.findIndex((i, n) => n >= BELT_IDX && !i);
      if (to < 0) to = this.inventory.findIndex((i) => !i);
    } else if (this.inventory[to]) return;
    this.send(ClientPackets.RemoveItem({ Grid: MirGridType.Inventory, UniqueID: item.UniqueID, To: to }));
    this.lock(item);
  }

  /** MirItemCell.UseItem for an inventory item: equip by type into the desktop's slot, or C.UseItem for consumables. */
  use(index: number, now: number): void {
    const item = this.inventory[index];
    if (!item || this.isLocked(item)) return;
    const info = this.info(item);
    if (!info) return;
    const eq = (s: number) => this.equipment[s] ?? null;
    const isAmulet = (i: UserItem | null) => !!i && this.info(i)?.Type === ItemType.Amulet;
    let slot = -1;
    switch (info.Type) {
      case ItemType.Weapon: slot = EquipmentSlot.Weapon; break;
      case ItemType.Armour: slot = EquipmentSlot.Armour; break;
      case ItemType.Helmet: slot = EquipmentSlot.Helmet; break;
      case ItemType.Necklace: slot = EquipmentSlot.Necklace; break;
      case ItemType.Bracelet: slot = !eq(EquipmentSlot.BraceletR) || isAmulet(eq(EquipmentSlot.BraceletR)) ? EquipmentSlot.BraceletR : EquipmentSlot.BraceletL; break;
      case ItemType.Ring: slot = !eq(EquipmentSlot.RingR) ? EquipmentSlot.RingR : EquipmentSlot.RingL; break;
      case ItemType.Amulet: {
        const cur = eq(EquipmentSlot.Amulet);
        if (cur && cur.ItemIndex === item.ItemIndex && cur.Count < info.StackSize) {
          this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.Inventory, GridTo: MirGridType.Equipment, IDFrom: item.UniqueID, IDTo: cur.UniqueID }));
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
      // MirItemCell.UseItem -> UseSlotItem (src/game/itemChanges.ts)
      case ItemType.Reins: case ItemType.Bells: case ItemType.Ribbon: case ItemType.Saddle: case ItemType.Mask:
      case ItemType.Hook: case ItemType.Float: case ItemType.Bait: case ItemType.Finder: case ItemType.Reel: case ItemType.Socket:
        this.useSlotItem(index);
        return;
      case ItemType.Potion: case ItemType.Scroll: case ItemType.Book: case ItemType.Food: case ItemType.Script:
      case ItemType.Pets: case ItemType.Transform: case ItemType.Deco: case ItemType.MonsterSpawn: case ItemType.SealedHero: {
        if (now < this.useItemTime) return;
        this.send(ClientPackets.UseItem({ UniqueID: item.UniqueID, Grid: MirGridType.Inventory }));
        this.lock(item);
        // Refill an emptied belt slot from the bag (MirItemCell.UseItem)
        if (item.Count === 1 && index < BELT_IDX) {
          for (let i = BELT_IDX; i < this.inventory.length; i++) {
            const other = this.inventory[i];
            if (other && other.ItemIndex === item.ItemIndex) {
              this.send(ClientPackets.MoveItem({ Grid: MirGridType.Inventory, From: i, To: index }));
              break;
            }
          }
        }
        this.useItemTime = now + 300;
        return;
      }
      default: return;
    }
    this.equip(index, slot);
  }

  /** NPCDropDialog.Confirm for Sell (whole stack) / Repair / SpecialRepair. */
  npcAction(index: number, kind: "sell" | "repair" | "srepair"): void {
    const item = this.inventory[index];
    if (!item || this.isLocked(item)) return;
    if (kind === "sell") this.send(ClientPackets.SellItem({ UniqueID: item.UniqueID, Count: item.Count }));
    else if (kind === "repair") this.send(ClientPackets.RepairItem({ UniqueID: item.UniqueID }));
    else this.send(ClientPackets.SRepairItem({ UniqueID: item.UniqueID }));
    this.lock(item);
  }

  /** C.ConsignItem (TrustMerchantDialog.SellItemButton / NPCDropDialog Consign); the cell stays locked until S.ConsignItem. */
  consign(item: UserItem, price: number, type: number): void {
    this.send(ClientPackets.ConsignItem({ UniqueID: item.UniqueID, Price: price, Type: type }));
    this.lock(item);
  }

  /** MirItemCell.Locked set / cleared from outside (TrustMerchantDialog.tempCell). */
  setLocked(item: UserItem, on: boolean): void {
    if (on) this.lock(item);
    else { this.unlock(item.UniqueID); this.onChange(); }
  }

  /** C.DropItem (GameScene drop: MirAmountBox for stacks; `count` defaults to the whole stack). */
  drop(index: number, count?: number): void {
    const item = this.inventory[index];
    if (!item || this.isLocked(item)) return;
    const n = Math.min(item.Count, Math.max(1, Math.trunc(count ?? item.Count)));
    this.send(ClientPackets.DropItem({ UniqueID: item.UniqueID, Count: n, HeroInventory: false }));
    this.lock(item);
  }

  /** MirItemCell Shift+left-click: split `count` (1..Count-1) off a stack into a free slot (S.SplitItem1 + S.SplitItem answer). */
  split(index: number, count: number): string | null {
    const item = this.inventory[index];
    if (!item || this.isLocked(item) || item.Count <= 1) return null;
    if (!this.inventory.some((i) => !i)) return "No room to split stack.";
    if (!(count > 0) || count >= item.Count) return null;
    this.send(ClientPackets.SplitItem({ Grid: MirGridType.Inventory, UniqueID: item.UniqueID, Count: Math.trunc(count) }));
    this.lock(item);
    return null;
  }

  /** GameScene gold drop (picked-up gold + map click -> MirAmountBox -> C.DropGold). */
  dropGold(amount: number): void {
    const n = Math.min(this.gold, Math.trunc(amount));
    if (n > 0) this.send(ClientPackets.DropGold({ Amount: n }));
  }
}
