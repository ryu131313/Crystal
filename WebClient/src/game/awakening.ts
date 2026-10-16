// NPCAwakeDialog's state (Client/MirScenes/Dialogs/NPCDialogs.cs) and GameScene's awakening handlers (NPCAwakening,
// AwakeningNeedMaterials, AwakeningLockedItem, Awakening): the seven cells (0 base item, 1-2 the server's needed
// materials, 3-4 materials, 5-6 success-rate / protection items), the glyph list (SelectAwakeType), the gold line and
// the bag items this port locked for the cells. game.ts feeds handle(); the UI follows the events: `shown` / `hidden`
// (the dialog), `types` (the glyph drop-down's items / selection), `changed` (redraw the cells and labels) and
// `result` (GameScene.Awakening's MirMessageBox).
// Deliberate differences (as before): cells take items picked from the bag (the desktop also swaps between its cells);
// the bag locks are local (the desktop's no-op C.MoveItem unlock is not sent).

import type { ItemInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { AwakeType, ItemGrade, ItemType } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { awakeLevel, awakeningPrice } from "./items.ts";
import { Emitter } from "./emitter.ts";

export const AWAKE_CELLS = 7; // NPCAwakeDialog.Items (0 base, 1-2 needed materials, 3-4 materials, 5-6 extra)

/** ClientTextKeys used here (Client/Localization/English.json). */
const T = {
  SelectUpgradeItem: "Select Upgrade Item.", SelectUpgradeType: "Select Upgrade Type.",
  NeedItemQuantity: (name: string, n: number) => `${name}\nQuantity: ${n}`,
};

/** NPCAwakeDialog.getAwakeTypeText. */
const AWAKE_TYPE_TEXT: Record<number, string> = {
  [AwakeType.DC]: "Bravery Glyph", [AwakeType.MC]: "Magic Glyph", [AwakeType.SC]: "Soul Glyph",
  [AwakeType.AC]: "Protection Glyph", [AwakeType.MAC]: "EvilSlayer Glyph", [AwakeType.HPMP]: "Body Glyph",
};

/** GameScene.Awakening result texts (ClientTextKeys English). */
const RESULT_TEXT: Record<number, string> = {
  [-4]: "You have not supplied enough materials.", [-3]: "Not enough gold.",
  [-2]: "Awakening already at maximum level.", [-1]: "Cannot awaken this item.",
};

export interface AwakeMaterial { readonly info: ItemInfo; readonly count: number }

export class Awakening {
  private _open = false;
  private readonly _items: (UserItem | null)[] = new Array<UserItem | null>(AWAKE_CELLS).fill(null);
  private _materials: (AwakeMaterial | null)[] = [null, null]; // ItemCells[1] / [2]
  private _types: { text: string; type: number }[] = [];
  private _typeTexts: readonly string[] = []; // _types' texts, rebuilt with _types
  private _selected = 0;
  private currentAwakeType: number = AwakeType.None;
  private _gold: number | null = null;
  /** Inventory items this port locked locally for the cells (MirItemCell.Locked). */
  private readonly locks = new Set<string>();

  /** NPCAwakeDialog.Show (S.NPCAwakening while hidden). */
  readonly shown = new Emitter<void>();
  /** NPCAwakeDialog.Hide (the cells' items were handed back). */
  readonly hidden = new Emitter<void>();
  /** SelectAwakeType's items or selection changed (MirDropDownBox.Items / SelectedIndex). */
  readonly types = new Emitter<void>();
  /** Cells, needed materials or the gold line changed. */
  readonly changed = new Emitter<void>();
  /** GameScene.Awakening's MirMessageBox (a failure result). */
  readonly result = new Emitter<{ text: string }>();

  constructor(private readonly deps: { send: (f: Uint8Array) => void; inventory: Inventory }) {
    this.itemCellClick();
  }

  /** NPCAwakeDialog.Visible. */
  get open(): boolean { return this._open; }
  /** NPCAwakeDialog.Items (cells 1 and 2 stay null: they show `materials`). */
  get items(): readonly (UserItem | null)[] { return this._items; }
  /** ItemCells[1] / [2]: the server's needed materials (S.AwakeningNeedMaterials). */
  get materials(): readonly (AwakeMaterial | null)[] { return this._materials; }
  /** SelectAwakeType's items. */
  get typeTexts(): readonly string[] { return this._typeTexts; }
  /** SelectAwakeType.SelectedIndex. */
  get selected(): number { return this._selected; }
  /** The gold label's value (UserItem.AwakeningPrice of the base item once the materials arrive; null when cleared:
   *  the classic label shows String(gold) or ""). */
  get gold(): number | null { return this._gold; }
  /** NeedItemLabel1 / 2: "Name\nQuantity: n" for needed material `i` (0 / 1). */
  needText(i: number): string {
    const need = this._materials[i];
    return need ? T.NeedItemQuantity(need.info.Name.replace(/[\d-]/g, ""), need.count) : "";
  }

  private get inv(): Inventory { return this.deps.inventory; }
  private info(item: UserItem): ItemInfo | undefined { return this.inv.info(item); }
  private findInv(id: bigint): number {
    return this.inv.inventory.findIndex((i) => i?.UniqueID === id);
  }
  private setLock(id: bigint, on: boolean): void {
    if (on) this.inv.locked.add(String(id)); else this.inv.locked.delete(String(id));
  }

  /** NPCAwakeDialog.Show. */
  show(): void {
    this._open = true;
    this.itemCellClick();
    this.shown.emit();
  }

  /** NPCAwakeDialog.Hide: unlock every cell's item through the server (S.AwakeningLockedItem echo), clear, reset. */
  hide(): void {
    if (!this._open) return;
    for (let i = 0; i < AWAKE_CELLS; i++) {
      const item = this._items[i];
      if (item) this.deps.send(ClientPackets.AwakeningLockedItem({ UniqueID: item.UniqueID, Locked: false }));
      this._items[i] = null;
    }
    this.itemCellClick();
    this._open = false;
    this.hidden.emit();
  }

  /** NPCAwakeDialog.ItemCellClear. */
  private itemCellClear(): void {
    this._materials = [null, null];
    this._gold = null;
  }

  private setTypes(types: { text: string; type: number }[]): void {
    this._types = types;
    this._typeTexts = types.map((t) => t.text);
  }

  /** NPCAwakeDialog.ItemCell_Click: rebuild the glyph list for the base item. */
  private itemCellClick(): void {
    this.itemCellClear();
    const base = this._items[0];
    if (!base) {
      this.setTypes([{ text: T.SelectUpgradeItem, type: AwakeType.None }]);
      this._selected = 0;
      this.currentAwakeType = AwakeType.None;
    } else if (awakeLevel(base) === 0) {
      const t = this.info(base)?.Type;
      const types = t === ItemType.Weapon ? [AwakeType.DC, AwakeType.MC, AwakeType.SC]
        : t === ItemType.Helmet ? [AwakeType.AC, AwakeType.MAC] : [AwakeType.HPMP];
      this.setTypes([{ text: T.SelectUpgradeType, type: AwakeType.None },
        ...types.map((type) => ({ text: AWAKE_TYPE_TEXT[type]!, type }))]);
    } else {
      this.setTypes([{ text: AWAKE_TYPE_TEXT[base.AwakeType] ?? T.SelectUpgradeItem, type: base.AwakeType }]);
      if (this.currentAwakeType !== base.AwakeType) {
        this.currentAwakeType = base.AwakeType;
        this.selectType(0);
      }
    }
    if (this._selected >= this._types.length) this._selected = 0;
    this.types.emit();
  }

  /** NPCAwakeDialog.OnAwakeTypeSelect: ask the server for the materials of this glyph. */
  selectType(index: number): void {
    this._selected = index;
    this.types.emit();
    const type = this._types[index]?.type ?? AwakeType.None;
    this.currentAwakeType = type;
    const base = this._items[0];
    if (type !== AwakeType.None && base) this.deps.send(ClientPackets.AwakeningNeedMaterials({ UniqueID: base.UniqueID, Type: type }));
  }

  /** NPCAwakeDialog.setNeedItems (a null list clears the cells; the desktop would throw). */
  private setNeedItems(materials: (ItemInfo | null)[] | null, counts: number[] | null): void {
    for (let i = 0; i < 2; i++) {
      const info = materials?.[i] ?? null, count = counts?.[i] ?? 0;
      this._materials[i] = info && count !== 0 ? { info, count } : null;
    }
    const base = this._items[0];
    if (base) this._gold = awakeningPrice(base, this.info(base));
    this.changed.emit();
  }

  /** MirItemCell "From AwakenItem" with nothing picked: cell `slot`'s item goes back to the bag (cells 1 and 2 hold none).
   *  The desktop sends a no-op C.MoveItem from == to to unlock; the lock is local, so it is just dropped. */
  takeBack(slot: number): void {
    const item = this._items[slot];
    if (!item || slot === 1 || slot === 2) return;
    this.unlock(item);
    this._items[slot] = null;
    if (slot === 0) this.itemCellClick();
    this.inv.changed();
  }

  /** MirItemCell "To Awakening": bag slot `from` (the pick, already cleared) into cell `slot`. */
  put(slot: number, from: number): void {
    const item = this.inv.inventory[from];
    if (!item || this.inv.isLocked(item)) return;
    const info = this.info(item);
    if (!info) return;
    const put = () => {
      this._items[slot] = item;
      this.setLock(item.UniqueID, true);
      this.locks.add(String(item.UniqueID));
    };
    switch (slot) {
      case 0: // base item
        if ((info.Type !== ItemType.Weapon && info.Type !== ItemType.Helmet && info.Type !== ItemType.Armour) || info.Grade === ItemGrade.None) return;
        if (this._items[0]) this.deps.send(ClientPackets.AwakeningLockedItem({ UniqueID: this._items[0].UniqueID, Locked: false }));
        put();
        this.itemCellClick();
        this.selectType(0);
        break;
      case 3: case 4: // materials
        if (info.Type !== ItemType.Awakening || info.Shape >= 200 || this._items[slot]) return;
        put();
        break;
      case 5: case 6: // success-rate / protection items (Shape 200)
        if (info.Type !== ItemType.Awakening || info.Shape !== 200) return;
        if (this._items[slot]) this.unlock(this._items[slot]!);
        put();
        break;
      default: return; // 1, 2 show the needed materials
    }
    this.inv.changed();
  }

  private unlock(item: UserItem): void {
    this.setLock(item.UniqueID, false);
    this.locks.delete(String(item.UniqueID));
  }

  /** NPCAwakeDialog.CheckNeedMaterials. */
  private checkNeedMaterials(): boolean {
    const maxEqual = !this._materials[0] || !this._materials[1] ? 1 : 2;
    let equal = 0;
    for (const need of this._materials) {
      if (!need) continue;
      for (let j = 3; j < 5; j++) {
        const have = this._items[j];
        if (have && this.info(have)?.Name === need.info.Name && need.count <= have.Count) equal++;
      }
    }
    return equal >= maxEqual;
  }

  /** NPCAwakeDialog.Awakening (UpgradeButton): C.Awakening. */
  awaken(): void {
    if (!this.checkNeedMaterials()) return;
    const type = this._types[this._selected]?.type ?? AwakeType.None;
    const base = this._items[0];
    if (type !== AwakeType.None && base) this.deps.send(ClientPackets.Awakening({ UniqueID: base.UniqueID, Type: type, PositionIdx: 0 }));
  }

  /** GameScene.Awakening: hide the dialog, drop the destroyed item, unlock, report. */
  private awakening(result: number, removeID: bigint): void {
    this.hide();
    if (removeID >= 0n) {
      const i = this.findInv(BigInt.asUintN(64, removeID));
      if (i >= 0) { this.setLock(this.inv.inventory[i]!.UniqueID, false); this.inv.inventory[i] = null; }
    }
    // The desktop unlocks every locked inventory cell; this port only clears the locks the awakening cells set.
    for (const id of this.locks) this.inv.locked.delete(id);
    this.locks.clear();
    this.inv.changed();
    const text = RESULT_TEXT[result];
    if (text) this.result.emit({ text });
  }

  /** Server packets (after Inventory). Never consumes. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NPCAwakening": if (!this._open) this.show(); break; // GameScene.NPCAwakening
      case "AwakeningNeedMaterials": this.setNeedItems(p.Materials, p.MaterialsCount); break;
      case "AwakeningLockedItem": // GameScene.AwakeningLockedItem
        if (this.findInv(p.UniqueID) < 0) break;
        this.setLock(p.UniqueID, p.Locked);
        if (!p.Locked) this.locks.delete(String(p.UniqueID));
        this.inv.changed();
        break;
      case "Awakening": this.awakening(p.result, p.removeID); break;
    }
    return false;
  }
}
