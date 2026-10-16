// Ported from play/hudInventory.ts (InventoryMock): the new UI's bag window, wired to the real Inventory model
// (src/game/inventory.ts) the way the classic src/ui/inventoryWindow.ts (InventoryDialog) is. Changes from the mock:
//  - the bag is Inventory.inventory after the belt (BELT_IDX 8 on; the belt cells are the HUD's): Bag I = slots 8-47,
//    Bag II = slots 48-87, following Inventory.inventory.length (48 base, up to 88). Bag II's missing slots show locks;
//    a lock (or the Bag II tab before the first purchase) asks the @ADDINVENTORY price (PlayerObject's ladder:
//    1,000,000 + ((Length - 48) / 4) x 1,000,000 gold, 8 slots the first time, then 4) and sends the chat command;
//  - Ctrl + click a bag tab with a bag item picked: move it to the first free cell of that page (TryMoveSelectedInventoryItem);
//  - a Quest tab with the quest items (Quests.questInventory, display only: the classic quest button's grid);
//  - cells are the shared item cells (core/itemCells.ts, grid "inv"), so pick, drag, split, right-click use, Shift +
//    right-click chat link and Ctrl + right-click sockets are core/drag.ts's; nothing moves until the server answers;
//  - gold and credit read from Inventory; clicking the gold picks it up (ItemPick.toggleGold, the InventoryDialog
//    GoldLabel) and a map click drops it (core/drag.ts mapClick -> itemDrop.ts);
//  - weight = UserStats.currentBagWeight / stats[Stat.BagWeight] (ctx.player.stats), free cells, a client-side search
//    highlight and category chips;
//  - Sort / Stack send C.SortItems through Inventory.sortItems (off while a bag cell is locked and for the 2 s cooldown;
//    the answer is S.UserSlotsRefresh), replacing the mock's local reorder;
//  - a delete bin (a custom drop target "x:invBin"): drop or place a bag / belt item on it, confirm (amount first for
//    a stack), then Inventory.deleteItem locks the cell until S.DeleteItem. Off while dead: the server echoes
//    S.DeleteItem without deleting when dead and the model would drop the item locally (Known debt, not fixed here);
//  - the mock's local sort, constant credit, WEIGHT_MAX, drag host registration and per-change rebuild are gone: the
//    window renders when shown and is only marked dirty while closed.

import type { UserItem, ItemInfo } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { ItemType, Stat } from "../../../protocol/enums.generated.ts";
import { BELT_IDX, SORT_COOLDOWN_MS } from "../../../game/inventory.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { KeybindOptions, keyLabel } from "../../../game/keybinds.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, setStyle, setText } from "../core/dom.ts";
import { fmt } from "../core/format.ts";
import { svg } from "../core/icons.ts";
import { GOLD_IMG } from "../core/popups.ts";
import { button, chips, searchBox, tabs, type Btn, type Choice } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import type { ItemCell } from "../core/itemCells.ts";
import type { CellRef } from "../core/dragRoutes.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

/** Cells per bag page (InventoryDialog: 8 x 5). */
const BAG_PAGE = 40;
/** UserObject.Inventory.Length before any @ADDINVENTORY (the belt + Bag I = CharacterInfo.Inventory since phase 2.9). */
const INVENTORY_BASE = BELT_IDX + BAG_PAGE;
/** The largest bag (CharacterInfo.ResizeInventory cap). */
const INVENTORY_MAX = INVENTORY_BASE + BAG_PAGE;
const QUEST_PAGE = 2;
const BIN = "x:invBin" as const;

/** Client-side categories for the chips (by ItemType; anything else is "Other"). */
type Cat = "gear" | "use" | "craft" | "other";
const CAT_OF = new Map<number, Cat>([
  ...[ItemType.Weapon, ItemType.Armour, ItemType.Helmet, ItemType.Necklace, ItemType.Bracelet, ItemType.Ring, ItemType.Belt,
    ItemType.Boots, ItemType.Stone, ItemType.Torch, ItemType.Mask].map((t) => [t, "gear"] as [number, Cat]),
  ...[ItemType.Potion, ItemType.Scroll, ItemType.Amulet, ItemType.Food, ItemType.Script, ItemType.Book, ItemType.Transform,
    ItemType.Pets, ItemType.MonsterSpawn].map((t) => [t, "use"] as [number, Cat]),
  ...[ItemType.Ore, ItemType.Meat, ItemType.CraftingMaterial, ItemType.Gem, ItemType.Awakening, ItemType.Socket]
    .map((t) => [t, "craft"] as [number, Cat]),
]);
const CATS: readonly Choice<Cat>[] = [
  { value: "gear", label: "Gear", title: "Weapons, armour and jewellery" },
  { value: "use", label: "Use", title: "Potions, scrolls, amulets and other consumables" },
  { value: "craft", label: "Craft", title: "Ores, gems and crafting materials" },
  { value: "other", label: "Other", title: "Everything else" },
];

const CSS = `
.hudm .iwin { width: 408px; }
.hudm .iwin .itabs { margin: 6px 14px 0; }
.hudm .iwin .itool { display: flex; align-items: center; gap: 6px; padding: 8px 14px 0; flex-wrap: wrap; }
.hudm .iwin .itool .ui-search { flex: 1 1 130px; min-width: 110px; }
.hudm .iwin .grid { display: grid; grid-template-columns: repeat(8, 44px); gap: 4px; padding: 10px 14px 10px; }
.hudm .iwin .grid .icell.nomatch { opacity: .22; }
.hudm .iwin .grid .icell.match { border-color: var(--ac); }
.hudm .iwin .lk { width: 44px; height: 44px; display: flex; align-items: center; justify-content: center; cursor: pointer; pointer-events: auto;
  border-radius: var(--rad); border: 1px dashed var(--fr); background: color-mix(in srgb, var(--pb) 60%, #000); color: var(--dim); }
.hudm .iwin .lk svg { width: 14px; height: 14px; opacity: .55; }
.hudm .iwin .lk.next { border-color: color-mix(in srgb, var(--gold) 55%, var(--fr)); color: var(--gold); }
.hudm .iwin .lk.next svg { opacity: .9; }
.hudm .iwin .lk:hover { border-color: var(--ac); color: #fff; }
.hudm .iwin .qnote { padding: 0 14px 6px; font-size: 11px; color: var(--dim); }
.hudm .iwin .foot { display: flex; flex-direction: column; gap: 9px; padding: 2px 14px 12px; }
.hudm .iwin .wt { display: flex; align-items: center; gap: 8px; font-size: 11px; }
.hudm .iwin .wt span { color: var(--dim); font-weight: 600; letter-spacing: .04em; }
.hudm .iwin .wt b { color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .iwin .wt .bar { flex: 1; height: 8px; border-radius: 4px; overflow: hidden; background: #04060a; border: 1px solid var(--fr); box-shadow: inset 0 1px 2px #000; }
.hudm .iwin .wt .bar i { display: block; width: 0; height: 100%; border-radius: 3px;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .iwin .wt.warn .bar i { background: linear-gradient(90deg, color-mix(in srgb, var(--warn) 45%, #000), var(--warn)); }
.hudm .iwin .wt.full .bar i { background: linear-gradient(90deg, color-mix(in srgb, var(--bad) 45%, #000), var(--bad)); }
.hudm .iwin .wt .free { margin-left: 6px; }
.hudm .iwin .money { display: flex; align-items: center; gap: 12px; font-size: 12px; }
.hudm .iwin .money span { color: var(--dim); font-weight: 600; margin-right: 5px; }
.hudm .iwin .money b { color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .iwin .money .gpick { display: inline-flex; align-items: center; padding: 1px 4px; margin: -1px -4px; border: 1px solid transparent; border-radius: calc(var(--rad) * .8);
  background: none; font: inherit; color: inherit; cursor: pointer; pointer-events: auto; }
.hudm .iwin .money .gpick b { color: var(--gold); }
.hudm .iwin .money .gpick:hover, .hudm .iwin .money .gpick.on { border-color: var(--gold); }
.hudm .iwin .money .acts { margin-left: auto; display: flex; align-items: center; gap: 5px; }
.hudm .iwin .money .icell.bin { --cs: 26px; }
.hudm .iwin .money .icell.bin > .e { opacity: .6; }
.hudm .iwin .money .icell.bin svg { width: 14px; height: 14px; }
`;

/** One display-only quest item cell (quest items have no item grid in the models: tooltip only). */
interface QuestCell { el: HTMLElement; icon: HTMLElement; count: HTMLElement; img: number; item: UserItem | null }

export class InventoryWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  private page = 0;
  private query = "";
  private cats: ReadonlySet<Cat> = new Set();
  private readonly tabBar: ReturnType<typeof tabs>;
  private readonly grid = h("div", "grid");
  private readonly qgrid = h("div", "grid");
  private readonly qnote = h("div", "qnote", "Quest items are kept apart from your bag and go when their quest is done.");
  private readonly cells: ItemCell[] = [];
  private readonly locks: HTMLElement[] = [];
  private readonly qcells: QuestCell[] = [];
  private readonly wt = h("div", "wt");
  private readonly fill = h("i");
  private readonly wtText = h("b");
  private readonly freeText = h("b");
  private readonly goldBtn = h("button", "gpick");
  private readonly goldText = h("b", "", "0");
  private readonly creditText = h("b", "", "0");
  private readonly sortBtn: Btn;
  private readonly stackBtn: Btn;
  private readonly bin: ItemCell;
  /** performance.now() until which Sort / Stack stay off (Inventory.sortItems' cooldown, mirrored for the buttons). */
  private sortUntil = 0;
  /** The last Sort / Stack button state written ("" = enabled, else the reason). */
  private sortWhy = "-";
  private dead = false;
  private wtKey = "";

  constructor(ui: ModernUiHost) {
    super(ui.rt, "iwin", null, 70); // opens 40 px from the right edge of the screen
    this.ui = ui;
    injectCss("win-inventory", CSS);
    const m = ui.ctx.models, inv = m.inventory, sig = this.signal;

    this.tabBar = tabs(["Bag I", "Bag II", "Quest"], 0, (i) => this.selectPage(i));
    this.tabBar.el.classList.add("itabs");
    // Ctrl + click a bag tab with a bag item picked: move it to that page (InventoryDialog.TryMoveSelectedInventoryItem).
    this.listen(this.tabBar.el, "click", (e) => {
      if (!e.ctrlKey) return;
      const b = (e.target as Element).closest("button");
      const i = b ? Array.prototype.indexOf.call(this.tabBar.el.children, b) : -1;
      const pick = m.itemPick.cell;
      if (i < 0 || i >= QUEST_PAGE || !pick || pick.grid !== "inv") return;
      e.stopPropagation();
      e.preventDefault();
      this.moveToBag(i === 1, pick.index);
    }, { capture: true });

    const search = searchBox("Search the bag", (q) => { this.query = q.toLowerCase(); this.invalidate(); }, { signal: sig });
    const chipRow = chips(CATS, [], (sel) => { this.cats = sel; this.invalidate(); }, { multi: true, allowNone: true });
    chipRow.el.title = "Highlight items by kind (none selected = all)";

    // The 40 cells of one page, re-pointed on a page change (setRef), and a lock tile behind each for Bag II.
    for (let p = 0; p < BAG_PAGE; p++) {
      const c = ui.cells.cell("inv", BELT_IDX + p, { size: 44, signal: sig });
      const lk = h("div", "lk", svg("lock", "ic"));
      lk.hidden = true;
      lk.setAttribute("role", "button");
      lk.addEventListener("click", () => this.askUnlock(), { signal: sig });
      this.cells.push(c);
      this.locks.push(lk);
      this.grid.append(c.el, lk);
    }
    for (let p = 0; p < BAG_PAGE; p++) {
      const icon = h("i"), count = h("b");
      count.hidden = true;
      const el = h("div", "icell ro", icon, count);
      el.style.setProperty("--cs", "44px");
      this.qcells.push({ el, icon, count, img: -2, item: null });
      this.qgrid.append(el);
    }
    this.qgrid.hidden = true;
    this.qnote.hidden = true;
    // Quest item tooltips (one delegated pair; these cells are not core item cells, so ItemCells' hover skips them).
    this.listen(this.qgrid, "pointerover", (e) => {
      const q = this.questCellOf(e.target);
      if (q?.item) ui.tooltip.show(q.el, q.item, ui.labelContext());
    });
    this.listen(this.qgrid, "pointerout", (e) => {
      const to = this.questCellOf(e.relatedTarget);
      if (!to?.item) ui.tooltip.hide();
    });

    // Weight + free cells
    this.wt.append(h("span", "", "Weight"), h("div", "bar", this.fill), this.wtText, h("span", "free", "Free"), this.freeText);
    this.wt.title = "Bag weight / the most you can carry";

    // Gold (click: pick it up to drop on the map), credit, Sort / Stack, the delete bin
    this.goldBtn.type = "button";
    this.goldBtn.title = "Click to pick up gold, then click the map to drop some";
    this.goldBtn.append(h("span", "", "Gold"), this.goldText);
    this.goldBtn.addEventListener("click", () => m.itemPick.toggleGold(inv.gold), { signal: sig });
    this.sortBtn = button("Sort", () => this.sort("sort"), { title: "Sort the bag by kind and name" });
    this.stackBtn = button("Stack", () => this.sort("stack"), { title: "Merge part stacks, then sort the bag" });
    this.bin = ui.cells.cell(BIN, 0, { size: 26, cls: "bin", empty: svg("trash", "ic"), signal: sig, disabled: () => this.dead });
    this.bin.el.title = "Delete: drop a bag item here";
    sig.addEventListener("abort", ui.drag.addTarget(BIN, {
      item: () => null,
      accepts: (from) => from.grid !== "inv" ? null : this.dead ? "Not while dead" : "",
      verb: () => "Delete",
      drop: (from, item) => this.askDelete(from, item),
    }), { once: true });
    const money = h("div", "money", this.goldBtn, h("div", "", h("span", "", "Credit"), this.creditText),
      h("div", "acts", this.sortBtn.el, this.stackBtn.el, this.bin.el));

    this.el.append(h("div", "panel",
      this.titleBar("INVENTORY", `Close (${keyLabel(KeybindOptions.Inventory) || "I"})`),
      this.tabBar.el, h("div", "itool", search.el, chipRow.el), this.grid, this.qgrid, this.qnote,
      h("div", "foot", this.wt, money)));

    // Model events only mark the window dirty (rendered on the next frame while open, else on the next open).
    const off = [
      inv.subscribe(() => this.invalidate()),
      m.quests.onChange((c) => { if (c === "items") this.invalidate(); }),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
      m.itemPick.changed.on(() => this.goldBtn.classList.toggle("on", m.itemPick.gold)),
    ];
    sig.addEventListener("abort", () => { for (const f of off) f(); }, { once: true });

    // Weight, the dead state (the bin) and the Sort cooldown are not model events: a light poll while shown.
    this.everyShown(250, () => this.tick());
  }

  protected shown(): void { this.tick(); }

  protected closed(): void {
    // InventoryDialog.Hide: the bag's pick and the picked gold go with it (belt picks stay: the belt is on the HUD).
    const pick = this.ui.ctx.models.itemPick;
    if (pick.cell?.grid === "inv" && pick.cell.index >= BELT_IDX) pick.clear();
    pick.setGold(false);
    this.ui.tooltip.hide();
  }

  protected render(): void {
    const m = this.ui.ctx.models, inv = m.inventory, list = inv.inventory, len = list.length;
    if (this.page === 1 && len <= INVENTORY_BASE) this.page = 0;
    this.tabBar.select(this.page);
    const bag2 = this.tabBar.el.children[1] as HTMLElement | undefined;
    if (bag2) bag2.title = len <= INVENTORY_BASE ? "Locked: click to buy 8 more cells" : "";
    const quest = this.page === QUEST_PAGE;
    this.grid.hidden = quest;
    this.qgrid.hidden = !quest;
    this.qnote.hidden = !quest;

    // Bag page: cells for the slots that exist, lock tiles for the rest of Bag II.
    const filtering = this.query !== "" || this.cats.size > 0;
    const base = BELT_IDX + (this.page === 1 ? BAG_PAGE : 0);
    const step = len <= INVENTORY_BASE ? 8 : 4;
    const per = [0, 0];
    if (!quest) {
      for (let p = 0; p < BAG_PAGE; p++) {
        const slot = base + p, c = this.cells[p]!, lk = this.locks[p]!;
        const exists = slot < len;
        c.setRef({ grid: "inv", index: exists ? slot : BELT_IDX + p });
        c.el.hidden = !exists;
        lk.hidden = exists;
        if (!exists) {
          const next = slot - len < step && len < INVENTORY_MAX;
          lk.classList.toggle("next", next);
          lk.title = next ? `Unlock ${step} more cells for ${fmt(this.unlockPrice(len))} gold` : "Locked: unlock the cells before it first";
        }
        const item = exists ? list[slot] ?? null : null;
        const hit = filtering && !!item && this.matches(m.itemInfos.get(item.ItemIndex));
        c.el.classList.toggle("match", hit);
        c.el.classList.toggle("nomatch", filtering && !!item && !hit);
      }
    }
    if (filtering) {
      for (let s = BELT_IDX; s < len; s++) {
        const item = list[s];
        if (item && this.matches(m.itemInfos.get(item.ItemIndex))) per[s < INVENTORY_BASE ? 0 : 1]!++;
      }
    }
    this.tabBar.badge(0, filtering && this.page !== 0 ? per[0]! : 0);
    this.tabBar.badge(1, filtering && this.page !== 1 ? per[1]! : 0);

    // Quest page (display only).
    let questHits = 0;
    const qi = m.quests.questInventory;
    for (let p = 0; p < BAG_PAGE; p++) {
      const q = this.qcells[p]!, item = qi[p] ?? null, info = item ? m.itemInfos.get(item.ItemIndex) : undefined;
      const hit = filtering && !!item && this.matches(info);
      if (hit) questHits++;
      if (!quest) continue;
      q.item = item;
      q.el.classList.toggle("has", !!item);
      q.el.classList.toggle("match", hit);
      q.el.classList.toggle("nomatch", filtering && !!item && !hit);
      const stack = !!item && !!info && info.StackSize > 1;
      q.count.hidden = !stack;
      if (stack) setText(q.count, String(item!.Count));
      if (item && info) q.el.setAttribute("aria-label", info.Name); else q.el.removeAttribute("aria-label");
      const image = item && info ? info.Image : -1;
      if (image !== q.img) {
        q.img = image;
        if (image < 0) q.icon.style.backgroundImage = "";
        else void libPicture(Lib.Items, image).then((pic) => { if (q.img === image && !this.signal.aborted) q.icon.style.backgroundImage = pic ? `url(${pic.url})` : ""; });
      }
    }
    this.tabBar.badge(QUEST_PAGE, filtering && !quest ? questHits : 0);

    // Free cells (both pages), gold, credit, the Sort / Stack state.
    let free = 0;
    for (let s = BELT_IDX; s < len; s++) if (!list[s]) free++;
    setText(this.freeText, `${free} / ${Math.max(0, len - BELT_IDX)}`);
    setText(this.goldText, fmt(inv.gold));
    setText(this.creditText, fmt(inv.credit));
    this.goldBtn.classList.toggle("on", m.itemPick.gold);
    this.updateSort(this.ui.ctx.now());
  }

  // — Pages —

  private selectPage(i: number): void {
    const len = this.ui.ctx.models.inventory.inventory.length;
    if (i === 1 && len <= INVENTORY_BASE) {
      this.tabBar.select(this.page); // stays where it was: Bag II has to be bought first
      this.askUnlock();
      return;
    }
    if (this.page === i) return;
    this.page = i;
    this.ui.tooltip.hide();
    this.invalidate();
  }

  /** InventoryDialog.TryMoveSelectedInventoryItem: the picked bag item to the first free cell of a page. */
  private moveToBag(second: boolean, from: number): void {
    const m = this.ui.ctx.models, inv = m.inventory, list = inv.inventory, len = list.length;
    const item = list[from];
    if (!item || inv.isLocked(item)) return;
    if (second && (len <= INVENTORY_BASE || from >= INVENTORY_BASE)) return;
    if (!second && from >= BELT_IDX && from < INVENTORY_BASE) return;
    const start = second ? INVENTORY_BASE : BELT_IDX, end = second ? len : Math.min(len, INVENTORY_BASE);
    let to = -1;
    for (let i = start; i < end; i++) if (!list[i]) { to = i; break; }
    if (to < 0) { this.ui.drag.toast("No free cell there"); return; }
    m.itemPick.clear();
    const id = item.UniqueID;
    this.ui.drag.request(() => {
      const now = inv.inventory[from];
      if (now && now.UniqueID === id && !inv.inventory[to]) inv.move(from, to);
    }, [{ grid: "inv", index: from }, { grid: "inv", index: to }]);
  }

  // — @ADDINVENTORY —

  /** PlayerObject "ADDINVENTORY": 1,000,000 + ((Length - 48) / 4) x 1,000,000 gold. */
  private unlockPrice(len: number): number {
    return 1000000 + Math.trunc((len - INVENTORY_BASE) / 4) * 1000000;
  }

  private askUnlock(): void {
    const len = this.ui.ctx.models.inventory.inventory.length;
    if (len >= INVENTORY_MAX) return;
    const first = len <= INVENTORY_BASE, price = this.unlockPrice(len);
    const text = first
      ? `Buy 8 more bag cells for ${fmt(price)} gold? After this, each purchase unlocks 4 more cells, up to 40.`
      : `Unlock 4 more bag cells for ${fmt(price)} gold?`;
    void this.ui.rt.popups.ask(text, { title: first ? "Bag II" : "More bag cells", kind: "question", gold: price }).then((yes) => {
      if (yes && !this.signal.aborted) this.ui.ctx.send(ClientPackets.Chat({ Message: "@ADDINVENTORY" }));
    });
  }

  // — Sort / Stack —

  private sort(mode: "sort" | "stack"): void {
    const now = this.ui.ctx.now();
    const inv = this.ui.ctx.models.inventory;
    if (inv.sortItems("inventory", mode, now)) this.sortUntil = now + SORT_COOLDOWN_MS;
    else if (!inv.inventory.some((item) => inv.isLocked(item))) {
      // Refused without a locked cell: the model's shared cooldown (a storage sort). Its end is not exposed, so take the
      // full cooldown as an upper bound and say why.
      this.sortUntil = now + SORT_COOLDOWN_MS;
      this.ui.drag.toast("Wait a moment before sorting again");
    }
    this.updateSort(now);
  }

  /** Off while any bag / belt cell waits for the server (the model refuses then) or during the cooldown. */
  private updateSort(now: number): void {
    const inv = this.ui.ctx.models.inventory;
    let why = "";
    if (now < this.sortUntil) why = "Wait a moment before sorting again";
    else for (const item of inv.inventory) if (inv.isLocked(item)) { why = "Waiting for the server"; break; }
    if (why === this.sortWhy) return;
    this.sortWhy = why;
    this.sortBtn.setDisabled(why !== "", why);
    this.stackBtn.setDisabled(why !== "", why);
  }

  // — Delete bin —

  private askDelete(from: CellRef, item: UserItem): void {
    const m = this.ui.ctx.models, inv = m.inventory, slot = from.index, id = item.UniqueID;
    const info = m.itemInfos.get(item.ItemIndex);
    const name = info ? itemFriendlyName(info) : "this item";
    const pop = { name, img: info?.Image ?? GOLD_IMG };
    const still = (): UserItem | null => {
      const now = inv.inventory[slot];
      return now && now.UniqueID === id && !inv.isLocked(now) && !this.isDead() ? now : null;
    };
    const confirm = (count: number): void => {
      const label = count > 1 ? `${name} (${fmt(count)})` : name;
      void this.ui.rt.popups.ask(`Destroy ${label}? It is gone for good.`, { title: "Delete item", kind: "danger", item: { ...pop, count }, hold: 600 })
        .then((yes) => {
          if (!yes || this.signal.aborted) return;
          if (!still()) { this.ui.drag.toast("It's no longer there"); return; }
          this.ui.drag.request(() => { if (still()) inv.deleteItem(slot, count); }, [{ grid: "inv", index: slot }]);
        });
    };
    if (item.Count <= 1) { confirm(1); return; }
    void this.ui.rt.popups.amount({ title: "Delete how many?", max: item.Count, value: item.Count, item: { ...pop, count: item.Count }, leftLabel: "left" })
      .then((n) => {
        if (n === null || n <= 0 || this.signal.aborted) return;
        const now = still();
        if (now) confirm(Math.min(n, now.Count));
      });
  }

  // — Polled state (while shown) —

  private isDead(): boolean { return this.ui.ctx.player.user?.dead ?? false; }

  private tick(): void {
    const now = this.ui.ctx.now();
    const dead = this.isDead();
    if (dead !== this.dead) {
      this.dead = dead;
      this.bin.el.title = dead ? "You can't delete items while dead" : "Delete: drop a bag item here";
      this.ui.cells.refresh(); // the bin greys out / back
    }
    if (this.sortUntil && now >= this.sortUntil) { this.sortUntil = 0; this.updateSort(now); }
    const st = this.ui.ctx.player.stats;
    const max = st?.stats[Stat.BagWeight] ?? 0, w = st?.currentBagWeight ?? 0;
    const key = `${w}/${max}`;
    if (key === this.wtKey) return;
    this.wtKey = key;
    const f = max > 0 ? w / max : 0;
    setStyle(this.fill, "width", (Math.min(1, f) * 100).toFixed(1) + "%");
    setText(this.wtText, `${fmt(w)} / ${fmt(max)}`);
    this.wt.classList.toggle("warn", f > 0.75 && f < 1);
    this.wt.classList.toggle("full", f >= 1);
  }

  // — Helpers —

  /** The search / category filter (client-side, by ItemInfo name and ItemType). */
  private matches(info: ItemInfo | undefined): boolean {
    if (!info) return false;
    if (this.cats.size && !this.cats.has(CAT_OF.get(info.Type) ?? "other")) return false;
    return !this.query || info.Name.toLowerCase().includes(this.query) || itemFriendlyName(info).toLowerCase().includes(this.query);
  }

  private questCellOf(t: EventTarget | null): QuestCell | undefined {
    const el = t instanceof Element ? t.closest(".icell") : null;
    return el ? this.qcells.find((q) => q.el === el) : undefined;
  }
}

registerWindow("inventory", (host) => new InventoryWindow(host));
