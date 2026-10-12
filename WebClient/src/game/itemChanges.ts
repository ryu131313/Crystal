// Item changes kept in step with the server, as GameScene's handlers do: sockets and slot items (SocketDialog grid =
// GameScene.SelectedItem.Slots, and the mount / fishing-rod slots MirGridType.Mount / Fishing: RemoveSlotItem,
// EquipSlotItem, ItemSlotSizeChanged), ItemSealChanged, CombineItem (Ctrl + place onto an inventory item),
// ItemUpgraded, and the user's side of an item rental (ItemRentingDialog.RentalItem / RentalPeriod,
// User.RentalGoldAmount / RentalGoldLocked / RentalItemLocked: DepositRentalItem, RetrieveRentalItem, ItemRentalLock,
// CancelItemRental, ConfirmItemRental). The rental dialogs' guest side lives in src/ui/rentalWindow.ts.
// Locks share Inventory.locked (keyed by UniqueID); an empty socket cell waiting for an answer is in pendingSlots.
// Not ported: the Storage grid (EquipSlotItem / RemoveSlotItem from or to NPC storage) and hero inventories.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { EquipmentSlot, ItemType, MirGridType } from "../protocol/enums.generated.ts";
import { BELT_IDX, type Inventory } from "./inventory.ts";

/** SocketDialog.Grid (6 x 2). */
export const SOCKET_GRID_SLOTS = 12;
const FISHING_ROD_SHAPES = [49, 50]; // Globals.FishingRodShapes
/** Shared/Enums.cs MountSlot / FishingSlot (not in enums.generated.ts). */
const MOUNT_SLOT: Partial<Record<number, number>> = { [ItemType.Reins]: 0, [ItemType.Bells]: 1, [ItemType.Saddle]: 2, [ItemType.Ribbon]: 3, [ItemType.Mask]: 4 };
const FISHING_SLOT: Partial<Record<number, number>> = { [ItemType.Hook]: 0, [ItemType.Float]: 1, [ItemType.Bait]: 2, [ItemType.Finder]: 3, [ItemType.Reel]: 4 };

/** DateTime.FromBinary: low 62 bits = ticks (100 ns since 0001-01-01); same rule as screens.ts. */
export function dateFromBinary(v: bigint): Date {
  const ticks = BigInt.asUintN(62, v);
  return new Date(Number((ticks - 621355968000000000n) / 10000n));
}

/** Functions.PrintTimeSpanFromSeconds (accurate). */
export function printTimeSpan(secs: number): string {
  const t = Math.max(0, Math.floor(secs));
  const d = Math.floor(t / 86400), h = Math.floor(t / 3600) % 24, m = Math.floor(t / 60) % 60, s = t % 60;
  const p2 = (n: number) => String(n).padStart(2, "0");
  if (t < 60) return `${s}s`;
  if (t < 3600) return `${m}m ${p2(s)}s`;
  if (t < 86400) return `${h}h ${p2(m)}m ${p2(s)}s`;
  return `${d}d ${p2(h)}h ${p2(m)}m ${p2(s)}s`;
}

/** GameScene.CreateItemLabel SEALED + rental lines (SealedFor, ItemRentedFrom, RentalExpiresIn, RentalLockExpiresIn). */
export function itemChangeLines(item: UserItem, now = Date.now()): string[] {
  const lines: string[] = [];
  if (item.Sealed) {
    const left = (dateFromBinary(item.Sealed.ExpiryDate).getTime() - now) / 1000;
    if (left > 0) lines.push(`Sealed for ${printTimeSpan(left)}`);
  }
  const r = item.Rental;
  if (r && !r.RentalLocked) {
    const left = (dateFromBinary(r.ExpiryDate).getTime() - now) / 1000;
    lines.push(`Item rented from: ${r.OwnerName}`, left > 0 ? `Rental expires in: ${printTimeSpan(left)}` : "Rental expired");
  } else if (r && r.RentalLocked) {
    const left = (dateFromBinary(r.ExpiryDate).getTime() - now) / 1000;
    if (left > 0) lines.push(`Rental lock expires in: ${printTimeSpan(left)}`);
  }
  return lines;
}

export class ItemChanges {
  /** GameScene.SelectedItem (the item the socket dialog shows), by UniqueID so a RefreshItem replacement still resolves. */
  socketId: bigint | null = null;
  /** ItemRentingDialog.RentalItem (the lender's Renting cell). */
  rentalItem: UserItem | null = null;
  /** ItemRentingDialog.RentalPeriod (days). */
  rentalPeriod = 0;
  /** User.RentalGoldAmount (the fee the borrower has put up; the server already took it with S.LoseGold). */
  rentalGold = 0;
  /** User.RentalGoldLocked / RentalItemLocked (S.ItemRentalLock). */
  rentalGoldLocked = false;
  rentalItemLocked = false;
  /** Empty slot cells locked while an EquipSlotItem waits for its answer (key `${GridTo}:${To}`). */
  readonly pendingSlots = new Set<string>();
  /** A system chat line for client-side refusals. */
  refuse: (text: string) => void = () => {};
  /** MapObject.User.RidingMount / Fishing for the MirItemCell.UseItem / RemoveItem gates (set by MountWindow). */
  userState: () => { ridingMount: boolean; fishing: boolean } = () => ({ ridingMount: false, fishing: false });
  private readonly inv: Inventory;
  private readonly send: (frame: Uint8Array) => void;

  constructor(inv: Inventory, send: (frame: Uint8Array) => void) {
    this.inv = inv;
    this.send = send;
  }

  private changed(): void {
    this.inv.onChange();
  }

  private lock(...items: UserItem[]): void {
    for (const i of items) this.inv.locked.add(String(i.UniqueID));
    this.changed();
  }

  private unlock(...ids: bigint[]): void {
    for (const id of ids) this.inv.locked.delete(String(id));
  }

  private findIn(list: (UserItem | null)[], id: bigint): number {
    return list.findIndex((i) => i?.UniqueID === id);
  }

  /** The user's item with this UniqueID in inventory or equipment (GameScene's ItemSlotSizeChanged / ItemSealChanged search). */
  private findUserItem(id: bigint): UserItem | null {
    for (const list of [this.inv.inventory, this.inv.equipment]) for (const item of list) if (item?.UniqueID === id) return item;
    return null;
  }

  /** The socket dialog's item, or null once it has left inventory/equipment. */
  socketItem(): UserItem | null {
    return this.socketId === null ? null : this.findUserItem(this.socketId);
  }

  /** The item array a slot grid shows (MirItemCell.ItemArray for Socket / Mount / Fishing). */
  private slotsOf(grid: number): (UserItem | null)[] | null {
    if (grid === MirGridType.Socket) return this.socketItem()?.Slots ?? null;
    if (grid === MirGridType.Mount) return this.inv.equipment[EquipmentSlot.Mount]?.Slots ?? null;
    if (grid === MirGridType.Fishing) return this.inv.equipment[EquipmentSlot.Weapon]?.Slots ?? null;
    return null;
  }

  isSlotPending(grid: number, to: number): boolean {
    return this.pendingSlots.has(`${grid}:${to}`);
  }

  /** A socket dialog cell waiting for an EquipSlotItem answer. */
  isSocketPending(index: number): boolean {
    return this.isSlotPending(MirGridType.Socket, index);
  }

  /** Feed every server packet (after Inventory.handle); returns true when the user's items changed (refresh looks + stats). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "EquipSlotItem": {
        // GameScene.EquipSlotItem (from the Inventory grid; Storage is not ported)
        const slots = this.slotsOf(p.GridTo);
        if (!slots || p.Grid !== MirGridType.Inventory) return false;
        this.pendingSlots.delete(`${p.GridTo}:${p.To}`);
        this.unlock(p.UniqueID);
        const from = this.findIn(this.inv.inventory, p.UniqueID);
        if (p.Success && from >= 0 && p.To >= 0 && p.To < slots.length) {
          slots[p.To] = this.inv.inventory[from];
          this.inv.inventory[from] = null;
        }
        break;
      }
      case "RemoveSlotItem": {
        // GameScene.RemoveSlotItem (to the Inventory grid; Storage is not ported)
        const slots = this.slotsOf(p.Grid);
        if (!slots || p.GridTo !== MirGridType.Inventory) return false;
        const from = this.findIn(slots, p.UniqueID);
        if (from < 0) return false;
        this.unlock(p.UniqueID);
        if (p.Success && p.To >= 0 && p.To < this.inv.inventory.length) {
          this.inv.inventory[p.To] = slots[from];
          slots[from] = null;
        }
        break;
      }
      case "MergeItem": {
        if (p.GridFrom === MirGridType.Fishing && p.GridTo === MirGridType.Inventory) {
          // GameScene.MergeItem, GridFrom = Fishing (MirItemCell.RemoveItem stacks the rod's bait onto a bag stack)
          if (!p.Success) return false;
          const slots = this.slotsOf(MirGridType.Fishing);
          const fi = slots ? this.findIn(slots, p.IDFrom) : -1, ti = this.findIn(this.inv.inventory, p.IDTo);
          if (!slots || fi < 0 || ti < 0) return false;
          const from = slots[fi]!, to = this.inv.inventory[ti]!;
          const stack = this.inv.info(to)?.StackSize ?? 1;
          if (from.Count <= stack - to.Count) { to.Count += from.Count; slots[fi] = null; }
          else { from.Count -= stack - to.Count; to.Count = stack; }
          break;
        }
        // GameScene.MergeItem, GridTo = Fishing (bait onto the rod's bait slot); Inventory.handle has unlocked the cells
        if (p.GridTo !== MirGridType.Fishing || p.GridFrom !== MirGridType.Inventory || !p.Success) return false;
        const slots = this.slotsOf(MirGridType.Fishing);
        const fi = this.findIn(this.inv.inventory, p.IDFrom), ti = slots ? this.findIn(slots, p.IDTo) : -1;
        if (!slots || fi < 0 || ti < 0) return false;
        const from = this.inv.inventory[fi]!, to = slots[ti]!;
        const stack = this.inv.info(to)?.StackSize ?? 1;
        if (from.Count <= stack - to.Count) { to.Count += from.Count; this.inv.inventory[fi] = null; }
        else { from.Count -= stack - to.Count; to.Count = stack; }
        break;
      }
      case "ItemSlotSizeChanged": {
        // GameScene.ItemSlotSizeChanged -> UserItem.SetSlotSize(size): Array.Resize keeps the head, pads with null
        const item = this.findUserItem(p.UniqueID);
        if (!item || p.SlotSize < 0 || p.SlotSize === item.Slots.length) return false;
        if (p.SlotSize < item.Slots.length) item.Slots.length = p.SlotSize;
        else while (item.Slots.length < p.SlotSize) item.Slots.push(null);
        break;
      }
      case "ItemSealChanged": {
        // GameScene.ItemSealChanged: new SealedInfo { ExpiryDate } (NextSealDate stays default)
        const item = this.findUserItem(p.UniqueID);
        if (!item) return false;
        item.Sealed = { ExpiryDate: p.ExpiryDate, NextSealDate: 0n };
        break;
      }
      case "CombineItem": {
        // GameScene.CombineItem (Inventory grid; HeroInventory: heroItems.ts)
        if (p.Grid !== MirGridType.Inventory) return false;
        const list = this.inv.inventory;
        const fi = this.findIn(list, p.IDFrom), ti = this.findIn(list, p.IDTo);
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
      case "ItemUpgraded": {
        // GameScene.ItemUpgraded (Inventory only): AddedStats, MaxDura and RefineAdded are replaced
        const i = this.findIn(this.inv.inventory, p.Item.UniqueID);
        if (i < 0) return false;
        const item = this.inv.inventory[i]!;
        item.AddedStats = { ...p.Item.AddedStats };
        item.MaxDura = p.Item.MaxDura;
        item.RefineAdded = p.Item.RefineAdded;
        break;
      }
      case "DepositRentalItem": {
        // GameScene.DepositRentalItem (the InputRentalPeroid prompt is opened by RentalWindow)
        const item = this.inv.inventory[p.From];
        if (!item) return false;
        this.unlock(item.UniqueID);
        if (p.Success) { this.rentalItem = item; this.inv.inventory[p.From] = null; }
        break;
      }
      case "RetrieveRentalItem": {
        // GameScene.RetrieveRentalItem
        const item = this.rentalItem;
        if (!item) return false;
        this.unlock(item.UniqueID);
        if (p.Success && p.To >= 0 && p.To < this.inv.inventory.length) { this.inv.inventory[p.To] = item; this.rentalItem = null; }
        break;
      }
      case "DeleteItem":
        // PlayerObject.CancelItemRental mails a deposited item back when the bag is full, then deletes it client-side
        if (!this.rentalItem || this.rentalItem.UniqueID !== p.UniqueID) return false;
        this.rentalItem = null;
        break;
      case "ItemRentalLock":
        // GameScene.ItemRentalLock
        if (!p.Success) return false;
        this.rentalGoldLocked = p.GoldLocked;
        this.rentalItemLocked = p.ItemLocked;
        break;
      case "CancelItemRental": case "ConfirmItemRental":
        // GameScene.CancelItemRental / ConfirmItemRental -> ItemRentingDialog.Reset + ItemRentDialog.Reset (user side)
        this.rentalGoldLocked = false;
        this.rentalItemLocked = false;
        if (this.rentalItem) this.unlock(this.rentalItem.UniqueID);
        this.rentalItem = null;
        this.rentalPeriod = 0;
        this.rentalGold = 0;
        break;
      default: return false;
    }
    this.changed();
    return true;
  }

  // — Requests (MirItemCell) —

  /** MirItemCell.OpenItem (Ctrl + right-click) -> SocketDialog.Show: only items with slots. Returns false when nothing opens. */
  openSockets(item: UserItem | null): boolean {
    if (!item || item.Slots.length === 0) { this.socketId = null; this.changed(); return false; }
    this.socketId = item.UniqueID;
    this.changed();
    return true;
  }

  /** SocketDialog.Hide: GameScene.SelectedItem = null. */
  closeSockets(): void {
    this.socketId = null;
    this.changed();
  }

  /** MirItemCell.UseSlotItem for an inventory item (right-click): a Socket item goes into the first free visible socket
   *  of the open socket dialog (shape 1 weapon / 2 armour / 3 ring, bracelet, necklace); mount and fishing parts go into
   *  their fixed MountSlot / FishingSlot of the equipped mount / rod (bait merges onto the bait slot first).
   *  The generic CanWearItem checks (gender, class, level, weight) are left to the server for slot items. */
  useSlotItem(index: number): void {
    const item = this.inv.inventory[index];
    if (!item || this.inv.isLocked(item)) return;
    const info = this.inv.info(item);
    if (!info) return;
    if (info.Type === ItemType.Socket) {
      const target = this.socketItem();
      const tInfo = target ? this.inv.info(target) : undefined;
      if (!target || !tInfo || tInfo.Type === ItemType.Mount || (tInfo.Type === ItemType.Weapon && FISHING_ROD_SHAPES.includes(tInfo.Shape))) return;
      if (info.Shape === 1 && tInfo.Type !== ItemType.Weapon) return;
      if (info.Shape === 2 && tInfo.Type !== ItemType.Armour) return;
      if (info.Shape === 3 && tInfo.Type !== ItemType.Ring && tInfo.Type !== ItemType.Bracelet && tInfo.Type !== ItemType.Necklace) return;
      const visible = Math.min(target.Slots.length, SOCKET_GRID_SLOTS);
      let to = -1;
      for (let i = 0; i < visible; i++) if (!target.Slots[i] && !this.isSlotPending(MirGridType.Socket, i)) { to = i; break; }
      if (to < 0) return;
      this.sendEquipSlot(item, to, MirGridType.Socket, target);
      return;
    }
    const mountSlot = MOUNT_SLOT[info.Type], fishSlot = FISHING_SLOT[info.Type];
    if (mountSlot !== undefined) {
      // CanWearItem: MountType < 0 -> refused silently (no mount equipped)
      const mount = this.inv.equipment[EquipmentSlot.Mount];
      if (!mount) return;
      this.sendEquipSlot(item, mountSlot, MirGridType.Mount, mount);
    } else if (fishSlot !== undefined) {
      // CanWearItem: HasFishingRod
      const rod = this.inv.equipment[EquipmentSlot.Weapon];
      const rodInfo = rod ? this.inv.info(rod) : undefined;
      if (!rod || !rodInfo || !FISHING_ROD_SHAPES.includes(rodInfo.Shape)) return;
      const bait = rod.Slots[fishSlot];
      if (info.Type === ItemType.Bait && bait && bait.ItemIndex === item.ItemIndex && bait.Count < info.StackSize) {
        this.send(ClientPackets.MergeItem({ GridFrom: MirGridType.Inventory, GridTo: MirGridType.Fishing, IDFrom: item.UniqueID, IDTo: bait.UniqueID }));
        this.lock(item);
        return;
      }
      this.sendEquipSlot(item, fishSlot, MirGridType.Fishing, rod);
    }
  }

  private sendEquipSlot(item: UserItem, to: number, gridTo: number, target: UserItem): void {
    if (to >= target.Slots.length || target.Slots[to] || this.isSlotPending(gridTo, to)) return;
    this.send(ClientPackets.EquipSlotItem({ Grid: MirGridType.Inventory, UniqueID: item.UniqueID, To: to, GridTo: gridTo, ToUniqueID: target.UniqueID }));
    this.pendingSlots.add(`${gridTo}:${to}`);
    this.lock(item);
  }

  /** MirItemCell.RemoveItem for a socket cell (right-click): C.RemoveSlotItem into the first free bag slot, then the belt
   *  (amulets: the first free slot from the belt). The desktop's stack-merge branch is not ported (socket items do not stack). */
  removeSocket(slot: number): void {
    const target = this.socketItem();
    const item = target?.Slots[slot] ?? null;
    if (!target || !item || this.inv.isLocked(item)) return;
    const inv = this.inv.inventory;
    if (!inv.some((i) => !i)) return;
    const amulet = this.inv.info(item)?.Type === ItemType.Amulet;
    let to = amulet ? inv.findIndex((i) => !i) : inv.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) to = inv.findIndex((i) => !i);
    if (to < 0) return;
    this.send(ClientPackets.RemoveSlotItem({ Grid: MirGridType.Socket, GridTo: MirGridType.Inventory, UniqueID: item.UniqueID, To: to, FromUniqueID: target.UniqueID }));
    this.lock(item);
  }

  /** The equipped mount's / rod's slot items (MirItemCell.ItemArray for MirGridType.Mount / Fishing). */
  slotItems(grid: number): (UserItem | null)[] | null {
    return grid === MirGridType.Mount || grid === MirGridType.Fishing ? this.slotsOf(grid) : null;
  }

  /** MirItemCell.UseItem -> RemoveItem for a MountDialog / FishingDialog cell (right-click): nothing while fishing or
   *  riding (torches aside); a stackable item (bait) merges onto the last bag stack of the same item when it fits
   *  (C.MergeItem Fishing -> Inventory), else C.RemoveSlotItem into the first free bag slot, then the belt (amulets:
   *  belt first). FromUniqueID is the equipped mount / weapon. */
  removeSlotItem(grid: number, slot: number): void {
    const slots = this.slotItems(grid);
    const item = slots?.[slot] ?? null;
    const holder = this.inv.equipment[grid === MirGridType.Mount ? EquipmentSlot.Mount : EquipmentSlot.Weapon];
    if (!item || !holder || this.inv.isLocked(item)) return;
    const st = this.userState();
    const info = this.inv.info(item);
    if (st.fishing || !info || (st.ridingMount && info.Type !== ItemType.Torch)) return;
    const inv = this.inv.inventory;
    if (!inv.some((i) => !i)) return;
    if (info.StackSize > 1) {
      let stack: UserItem | null = null;
      for (const i of inv) if (i && i.ItemIndex === item.ItemIndex) stack = i;
      if (stack && stack.Count + item.Count <= info.StackSize) {
        this.send(ClientPackets.MergeItem({ GridFrom: grid, GridTo: MirGridType.Inventory, IDFrom: item.UniqueID, IDTo: stack.UniqueID }));
        this.lock(item);
        return;
      }
    }
    let to = info.Type === ItemType.Amulet ? inv.findIndex((i) => !i) : inv.findIndex((i, n) => n >= BELT_IDX && !i);
    if (to < 0) to = inv.findIndex((i) => !i);
    if (to < 0) return;
    this.send(ClientPackets.RemoveSlotItem({ Grid: grid, GridTo: MirGridType.Inventory, UniqueID: item.UniqueID, To: to, FromUniqueID: holder.UniqueID }));
    this.lock(item);
  }

  /** MirItemCell.MoveItem "To Inventory / From Inventory" with Ctrl, after the YouWantCombineItems Yes. */
  combine(from: number, to: number): void {
    const a = this.inv.inventory[from], b = this.inv.inventory[to];
    if (!a || !b || from === to || this.inv.isLocked(a) || this.inv.isLocked(b)) return;
    this.send(ClientPackets.CombineItem({ Grid: MirGridType.Inventory, IDFrom: a.UniqueID, IDTo: b.UniqueID }));
    this.lock(a, b);
  }

  /** MirItemCell "To Renting / From Inventory" (empty Renting cell only). */
  depositRental(from: number): void {
    const a = this.inv.inventory[from];
    if (!a || this.inv.isLocked(a) || this.rentalItem) return;
    this.send(ClientPackets.DepositRentalItem({ From: from, To: 0 }));
    this.lock(a);
  }

  /** MirItemCell "To Inventory / From Renting" (empty inventory cell only; refused while the item is locked in). */
  retrieveRental(to: number): void {
    const a = this.rentalItem;
    if (!a || this.inv.isLocked(a) || this.inv.inventory[to]) return;
    if (this.rentalItemLocked) { this.refuse("Unable to remove locked item, cancel item rental and try again."); return; }
    this.send(ClientPackets.RetrieveRentalItem({ From: 0, To: to }));
    this.lock(a);
  }

  /** ItemRentingDialog.InputRentalPeroid OK (1..30 days). */
  setRentalPeriod(days: number): boolean {
    if (!Number.isInteger(days) || days < 1 || days > 30) return false;
    this.rentalPeriod = days;
    this.send(ClientPackets.ItemRentalPeriod({ Days: days }));
    this.changed();
    return true;
  }

  /** ItemRentDialog rental price click (MirAmountBox up to Gold). Deliberate fix: the desktop sends the running total
   *  (RentalGoldAmount) but PlayerObject.SetItemRentalFee ADDS the amount and takes that much gold again, so this sends
   *  only the added amount; the shown total matches the server's ItemRentalFeeAmount. */
  addRentalFee(amount: number): void {
    const n = Math.trunc(amount);
    if (!(n > 0) || n > this.inv.gold || this.rentalGoldLocked) return;
    this.rentalGold += n;
    this.send(ClientPackets.ItemRentalFee({ Amount: n }));
    this.changed();
  }

  /** ItemRentDialog lock button (needs a fee of at least 1). */
  lockFee(): void {
    if (this.rentalGold < 1 || this.rentalGoldLocked) return;
    this.send(ClientPackets.ItemRentalLockFee());
  }

  /** ItemRentingDialog lock button (needs a period of 1..30 days). */
  lockItem(): void {
    if (this.rentalPeriod < 1 || this.rentalPeriod > 30 || this.rentalItemLocked) return;
    this.send(ClientPackets.ItemRentalLockItem());
  }
}
