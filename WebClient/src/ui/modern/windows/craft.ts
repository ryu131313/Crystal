// Ported from play/hudCraft.ts: the new UI's crafting window over the real models. Desktop counterpart:
// NPCCraftGoodsDialog + CraftDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCGoods (PanelType.Craft) /
// CraftItem; the classic port is src/ui/npcServices.ts (its auto-fill / HasCraftItems / CraftItem rules are kept).
//
// Server rules (NPCScript.Craft): C.CraftItem { UniqueID of the recipe's goods item, Count, Slots } where Slots are the
// inventory slots of the tools, then the ingredients, in recipe order. Each tool needs floor(CurrentDura / 1000) >= Count
// (1 durability point per craft); each ingredient is taken from ONE bag stack (the first slot of that item in Slots) that
// holds Count x the recipe amount and, for a recipe ingredient below full durability, at least its purity; Count is
// capped by the result's StackSize / amount and each ingredient's StackSize / amount; Gold x Count. A batch makes ONE
// roll: Random(100) < Chance + Stat.CraftRatePercent makes the items, otherwise everything is lost (S.CraftItem.Success
// is still true; only S.GainedItem tells the outcome).
// Changes from the mock:
//  - opens only when the server lists this NPC's recipes (S.NPCGoods PanelType.Craft while the NPC page is open) and
//    closes with the NPC session or the next NPC page; there is no menu entry or hotkey;
//  - recipes = the craft goods list + Recipes (S.NewRecipeInfo); materials, tool durability and gold are read from
//    Inventory; the chance bonus is the user's Stat.CraftRatePercent;
//  - the slots are chosen like CraftDialog.AutoFill (tools: matching, unlocked, CurrentDura >= 1000; ingredients:
//    matching, unlocked, enough purity, Count >= the recipe amount), picking the fullest tool / biggest stack so the
//    quantity cap reflects what one stack allows (the server never adds stacks together);
//  - Craft sends C.CraftItem and locks those bag cells until S.CraftItem (no local roll, no simulated outcome); the
//    result line and the session log come from S.GainedItem / S.CraftItem;
//  - HIDDEN (Crystal has no data or system for them): locked recipes and their criteria (the server lists only recipes
//    that pass RecipeInfo.CanCraft), "find on the market" (the market only works at a trust merchant's page), the mock's
//    fake crafting bar and sparks.
// Narrow layout (a layer below 1040 px): narrower columns, two recipe cards a row.

import type { ClientRecipeInfo, ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { ItemType, PanelType, Stat } from "../../../protocol/enums.generated.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, fmt1, h, injectCss, later, npcName, plural, progressRing, restartAnim, searchBox, setStyle,
  setText, toggleSwitch, typingField, type Btn,
} from "../core/index.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const CRAFT_WINDOW = "craft";

const LIST = "x:craftList" as const; // the recipe cards: index = position in the craft goods list
const DETAIL = "x:craft" as const;   // the detail pane: 0 = result, TOOL_BASE + i = tools, ING_BASE + i = ingredients
const TOOL_BASE = 1, ING_BASE = 40;
const MAX_CRAFT = 99;                // CraftDialog.CraftItem: the MirAmountBox's maximum
const ANSWER_MS = 8000;              // locks released if the server never answers (it returns silently when out of range)
const LOG_MAX = 20;

/** ClientTextKeys (English defaults) as the classic npcServices.ts. */
const T = {
  YouDoNotHaveEnoughGold: "You do not have enough gold.",
  YouDoNotHaveRequiredToolsOrIngredients: "You do not have the required tools or ingredients.",
};

type Cat = "All" | "Potions" | "Gems" | "Jewellery" | "Gear" | "Materials" | "Other";
const CATS: readonly Cat[] = ["All", "Potions", "Gems", "Jewellery", "Gear", "Materials", "Other"];
/** ItemType of the result -> category (everything else is Other). */
const CAT_OF: Readonly<Partial<Record<number, Cat>>> = {
  [ItemType.Potion]: "Potions", [ItemType.Gem]: "Gems", [ItemType.Necklace]: "Jewellery", [ItemType.Ring]: "Jewellery",
  [ItemType.Bracelet]: "Jewellery", [ItemType.Weapon]: "Gear", [ItemType.Armour]: "Gear", [ItemType.Helmet]: "Gear",
  [ItemType.Belt]: "Gear", [ItemType.Boots]: "Gear", [ItemType.Torch]: "Gear", [ItemType.Stone]: "Gear",
  [ItemType.CraftingMaterial]: "Materials", [ItemType.Ore]: "Materials", [ItemType.Meat]: "Materials",
};
const catOf = (info: ItemInfo | undefined): Cat => (info && CAT_OF[info.Type]) || "Other";

/** One tool or ingredient of a recipe and the bag slot chosen for it (-1 = none). */
interface Need {
  readonly shadow: UserItem;
  readonly slot: number;
  /** Tools: crafts the chosen tool has left (floor(CurrentDura / 1000)); ingredients: the chosen stack's count. */
  readonly have: number;
  /** Ingredients: the same item across the whole bag (the server uses one stack only). */
  readonly total: number;
  /** Why nothing in the bag fits ("" = a slot was chosen). */
  readonly why: string;
}

/** What the bag allows for one recipe. */
interface Plan {
  readonly tools: readonly Need[];
  readonly ings: readonly Need[];
  /** C.CraftItem.Slots: tools, then ingredients (empty while something is missing). */
  readonly slots: readonly number[];
  /** The most crafts the server would accept now (0 = none). */
  readonly max: number;
  /** Why max is 0 ("" otherwise). */
  readonly why: string;
}

interface Pending { readonly ids: readonly bigint[]; readonly itemIndex: number; readonly count: number; readonly name: string; made: boolean; cancel: () => void }

const CSS = `
.hudm .cfwin2 { width: 960px; }
.hudm .cfwin2 .panel { height: 560px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .cfwin2 .body { flex: 1; min-height: 0; display: flex; }
.hudm .cfwin2 .cats { width: 160px; flex: none; padding: 8px; display: flex; flex-direction: column; gap: 2px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .cfwin2 .cats .ui-search { margin-bottom: 6px; }
.hudm .cfwin2 .ct { display: flex; justify-content: space-between; padding: 5px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.hudm .cfwin2 .ct:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .cfwin2 .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .cfwin2 .ct small { color: var(--dim); font-size: 10px; } .hudm .cfwin2 .ct small b { color: var(--ok); font-weight: 700; }
.hudm .cfwin2 .canrow { display: flex; align-items: center; gap: 6px; margin-top: 8px; font-size: 11px; }
.hudm .cfwin2 .grid { flex: 1; min-width: 0; overflow-y: auto; padding: 10px; display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); grid-auto-rows: max-content; gap: 6px; align-content: start;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cfwin2 .rc { position: relative; display: flex; gap: 8px; align-items: center; padding: 7px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .cfwin2 .rc:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .cfwin2 .rc.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .cfwin2 .rc .t { flex: 1; min-width: 0; } .hudm .cfwin2 .rc .t b { display: block; font-size: 12px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .cfwin2 .rc .t small { font-size: 10px; color: var(--dim); } .hudm .cfwin2 .rc .t small.ok { color: var(--ok); } .hudm .cfwin2 .rc .t small.no { color: var(--bad-soft); }
.hudm .cfwin2 .rc .ch { position: absolute; right: 6px; top: 5px; font-size: 9px; font-weight: 800; padding: 0 5px; border-radius: 6px; color: #000; background: var(--bad-soft); }
.hudm .cfwin2 .rc .ch.mid { background: var(--gold); } .hudm .cfwin2 .rc .ch.hi { background: var(--ok); }
.hudm .cfwin2 .empty { grid-column: 1 / -1; padding: 20px; text-align: center; font-size: 12px; color: var(--dim); }
.hudm .cfwin2 .det { width: 330px; flex: none; overflow-y: auto; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cfwin2 .det > * { flex-shrink: 0; }
.hudm .cfwin2 .dmain { display: flex; flex-direction: column; gap: 10px; }
.hudm .cfwin2 .hero { display: flex; gap: 12px; align-items: center; }
.hudm .cfwin2 .res { position: relative; flex: none; }
.hudm .cfwin2 .res::after { content: ""; position: absolute; inset: -6px; border-radius: 50%; pointer-events: none; opacity: 0; background: radial-gradient(circle, var(--gold), transparent 65%); }
.hudm .cfwin2 .res.ok::after { animation: hudm-cf2ok .9s ease-out; } @keyframes hudm-cf2ok { 30% { opacity: .8; } }
.hudm .cfwin2 .res.bad { animation: hudm-cf2bad .5s ease-in-out; } @keyframes hudm-cf2bad { 25%, 75% { transform: translateX(-4px); } 50% { transform: translateX(4px); } }
.hudm .cfwin2 .hero h3 { margin: 0; font-size: 16px; color: #fff; } .hudm .cfwin2 .hero small { display: block; font-size: 11px; color: var(--dim); }
.hudm .cfwin2 .hero .ui-ring { margin-left: auto; }
.hudm .cfwin2 .slbl { display: flex; justify-content: space-between; }
.hudm .cfwin2 .slbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .cfwin2 .ing { display: flex; align-items: center; gap: 8px; padding: 4px 6px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); font-size: 12px; }
.hudm .cfwin2 .ing .n { flex: 1; min-width: 0; } .hudm .cfwin2 .ing .n small { display: block; font-size: 10px; color: var(--dim); }
.hudm .cfwin2 .ing .c { font-weight: 700; font-variant-numeric: tabular-nums; text-align: right; } .hudm .cfwin2 .ing .c.ok { color: var(--ok); } .hudm .cfwin2 .ing .c.no { color: var(--bad-soft); }
.hudm .cfwin2 .ing.miss { border-color: color-mix(in srgb, var(--bad) 55%, transparent); background: color-mix(in srgb, var(--bad) 6%, transparent); }
.hudm .cfwin2 .ing .du { height: 4px; margin-top: 3px; border-radius: 2px; background: #05070c; overflow: hidden; } .hudm .cfwin2 .ing .du i { display: block; height: 100%; background: var(--ac); }
.hudm .cfwin2 .qty { display: flex; align-items: center; gap: 6px; }
.hudm .cfwin2 .qty .btn { min-width: 26px; height: 26px; padding: 0 6px; }
.hudm .cfwin2 .qty input { width: 54px; height: 26px; text-align: center; font-size: 14px; font-weight: 700; }
.hudm .cfwin2 .qty .tot { margin-left: auto; font-size: 11px; color: var(--dim); text-align: right; } .hudm .cfwin2 .qty .tot b { color: var(--gold); }
.hudm .cfwin2 .go { height: 38px; font-size: 14px; font-weight: 800; letter-spacing: .1em; }
.hudm .cfwin2 .why { font-size: 11px; color: var(--bad-soft); text-align: center; min-height: 14px; } .hudm .cfwin2 .why.ok { color: var(--ok); } .hudm .cfwin2 .why.info { color: var(--dim); }
.hudm .cfwin2 .why.fail { color: var(--bad-soft); font-weight: 700; }
.hudm .cfwin2 .none { margin: auto; text-align: center; font-size: 12px; color: var(--dim); }
.hudm .cfwin2 .foot { display: flex; align-items: center; gap: 14px; padding: 6px 14px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 11px; color: var(--dim); }
.hudm .cfwin2 .foot b { color: #fff; } .hudm .cfwin2 .foot .g { color: var(--gold); } .hudm .cfwin2 .foot .sp { flex: 1; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
/* Narrow layer */
.hudm.narrow .cfwin2 { width: 760px; }
.hudm.narrow .cfwin2 .panel { height: 520px; }
.hudm.narrow .cfwin2 .cats { width: 130px; }
.hudm.narrow .cfwin2 .grid { grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); padding: 8px; }
.hudm.narrow .cfwin2 .det { width: 290px; padding: 10px; }
.hudm.narrow .cfwin2 .foot .hide-narrow { display: none; }
`;

class CraftWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private cat: Cat = "All";
  private query = "";
  private canOnly = false;
  /** The selected recipe: the goods item's UniqueID (null = the first listed). */
  private sel: bigint | null = null;
  private qty = 1;
  private pending: Pending | null = null;
  private result: "" | "ok" | "fail" | "refused" = "";
  private resultText = "";
  /** This visit's crafts (cleared when a craft list opens the window): the last LOG_MAX lines + the full tally. */
  private readonly log: { ok: boolean; text: string }[] = [];
  private visitDone = 0;
  private visitOk = 0;

  // — DOM, built once —
  private readonly title: HTMLElement;
  private readonly catBox = h("div");
  private readonly grid = h("div", "grid");
  private readonly det = h("div", "det");
  private readonly none = h("div", "none", "Pick a recipe.");
  private readonly main = h("div", "dmain");
  private readonly resName = h("h3");
  private readonly resSub = h("small");
  private readonly resGold = h("small");
  private readonly resBox = h("div", "res");
  private readonly ring = progressRing(62, 6);
  private readonly toolsLbl = h("div", "lbl slbl", "Tools", h("b", "", "1 durability per craft"));
  private readonly toolsBox = h("div");
  private readonly ingsLbl = h("div", "lbl slbl", "Ingredients");
  private readonly ingsFor = h("b");
  private readonly ingsBox = h("div");
  private readonly qtyInput = typingField(h("input", "fld"));
  private readonly maxBtn: Btn;
  private readonly minusBtn: Btn;
  private readonly plusBtn: Btn;
  private readonly makes = h("b");
  private readonly costs = h("b");
  private readonly goBtn: Btn;
  private readonly whyEl = h("div", "why");
  private readonly resultEl = h("div", "why");
  private readonly footGold = h("b", "g");
  private readonly footRate = h("b");
  private readonly footSession = h("b");
  private readonly footLast = h("span", "sp");
  private listCells: ItemCell[] = [];
  private detailCells: ItemCell[] = [];
  private catsKey = "";
  private gridKey = "";
  private detailKey = "";
  /** The plans of the last render, by goods index. */
  private plans: (Plan | null)[] = [];
  /** The shadow items the detail cells show (custom grid DETAIL). */
  private detailItems: { result: UserItem | null; tools: UserItem[]; ings: UserItem[] } = { result: null, tools: [], ings: [] };

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "cfwin2", 24, 24);
    injectCss("win-craft", CSS);
    this.m = ui.ctx.models;
    const m = this.m, sig = this.signal;

    // The custom read-only grids: the recipe cards and the detail pane's result / tools / ingredients
    const offList = ui.cells.addTarget(LIST, { item: (i) => this.goods()[i] ?? null, accepts: () => null, drop: () => {} });
    const offDet = ui.cells.addTarget(DETAIL, {
      item: (i) => i === 0 ? this.detailItems.result : i >= ING_BASE ? this.detailItems.ings[i - ING_BASE] ?? null : this.detailItems.tools[i - TOOL_BASE] ?? null,
      accepts: () => null, drop: () => {},
    });
    sig.addEventListener("abort", () => { offList(); offDet(); }, { once: true });

    // Categories column: search, categories with "can craft" counts, the "can craft now" switch
    const search = searchBox("Search recipes…", (q) => { this.query = q.toLowerCase(); this.invalidate(); }, { signal: sig });
    const can = toggleSwitch(false, (on) => { this.canOnly = on; this.invalidate(); }, { small: true, label: "Can craft now" });
    const cats = h("div", "cats", search.el, this.catBox, h("label", "canrow", can.el, "Can craft now"));

    // Detail pane
    const resCell = ui.cells.cell(DETAIL, 0, { readOnly: true, size: 56, signal: sig });
    this.resBox.append(resCell.el);
    this.ring.el.title = "The recipe's chance plus your craft rate bonus";
    this.ingsLbl.append(this.ingsFor);
    this.qtyInput.inputMode = "numeric";
    this.qtyInput.addEventListener("change", () => { this.qty = Math.trunc(Number(this.qtyInput.value)) || 1; this.invalidate(); }, { signal: sig });
    this.minusBtn = button("−", () => { this.qty--; this.invalidate(); }, { title: "One less" });
    this.plusBtn = button("+", () => { this.qty++; this.invalidate(); }, { title: "One more" });
    this.maxBtn = button("Max", () => { this.qty = MAX_CRAFT; this.invalidate(); }, { title: "As many as your bag and gold allow" });
    this.goBtn = button("CRAFT", () => void this.craft(), { cls: "main big go", icon: "craft" });
    this.main.append(
      h("div", "hero", this.resBox, h("div", "", this.resName, this.resSub, this.resGold), this.ring.el),
      this.toolsLbl, this.toolsBox, this.ingsLbl, this.ingsBox,
      h("div", "lbl", "Quantity"),
      h("div", "qty", this.minusBtn.el, this.qtyInput, this.plusBtn.el, this.maxBtn.el, h("div", "tot", "Makes ", this.makes, h("br"), "Costs ", this.costs)),
      this.goBtn.el, this.resultEl, this.whyEl,
    );
    this.det.append(this.none, this.main);

    // Recipe card clicks (one delegated listener)
    this.grid.addEventListener("click", (e) => {
      const card = (e.target as Element).closest<HTMLElement>(".rc[data-uid]");
      if (!card) return;
      const uid = BigInt(card.dataset.uid!);
      if (uid === this.sel) return;
      this.sel = uid;
      this.qty = 1;
      this.result = "";
      this.invalidate();
    }, { signal: sig });

    this.title = this.titleBar("CRAFTING", "Close");
    const foot = h("div", "foot", h("span", "", "Gold ", this.footGold), h("span", "", "Craft rate bonus ", this.footRate),
      h("span", "hide-narrow", "This visit ", this.footSession), this.footLast);
    this.el.append(h("div", "panel", this.title, h("div", "body", cats, this.grid, this.det), foot));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(m.inventory.subscribe(() => this.invalidate()));
    on(m.recipes.changed.on(() => { this.gridKey = this.catsKey = ""; this.invalidate(); }));
    on(m.npcSession.goods.on((g) => {
      if (g.type !== PanelType.Craft) return;
      this.sel = null; this.qty = 1; this.result = ""; this.gridKey = this.catsKey = "";
      this.log.length = 0; this.visitDone = this.visitOk = 0; // a new visit
      this.invalidate();
    }));
    // NPCDialog.Hide / a new NPC page (GameScene.NPCResponse) hide the craft dialogs
    on(m.npcSession.closed.on(() => this.toggle(false)));
    on(m.npcSession.page.on(() => this.toggle(false)));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.gridKey = this.catsKey = this.detailKey = ""; this.invalidate(); } }));
    on(ui.packets.on((p) => this.packet(p)));
  }

  protected override closed(): void {
    this.release(); // a request still out: its cells unlock (the late answer is ignored)
  }

  // — Data —

  private goods(): readonly UserItem[] { return this.m.npcSession.goodsList(PanelType.Craft); }
  private info(item: UserItem): ItemInfo | undefined { return this.m.itemInfos.get(item.ItemIndex); }
  private name(item: UserItem): string {
    const info = this.info(item);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }
  private rate(): number { return this.ui.ctx.player.stats?.stats[Stat.CraftRatePercent] ?? 0; }
  private chance(r: ClientRecipeInfo): number { return Math.max(0, Math.min(100, r.Chance + this.rate())); }

  /** CraftDialog.AutoFill + HasCraftItems + CraftItem's maximum, over the bag as it is now. */
  private plan(goods: UserItem, r: ClientRecipeInfo): Plan {
    const inv = this.m.inventory, bag = inv.inventory;
    const used = new Set<number>();
    const mine = this.pending?.ids ?? [];
    const locked = (s: UserItem) => inv.isLocked(s) && !mine.includes(s.UniqueID); // our own request's locks still count as ours
    const tools: Need[] = [], ings: Need[] = [];
    let max = MAX_CRAFT, why = "";
    for (const tool of r.Tools) {
      let best = -1, any = false;
      for (let i = 0; i < bag.length; i++) {
        const s = bag[i];
        if (!s || s.ItemIndex !== tool.ItemIndex || used.has(i) || locked(s)) continue;
        any = true;
        if (s.CurrentDura < 1000) continue;
        if (best < 0 || s.CurrentDura > bag[best]!.CurrentDura) best = i;
      }
      const have = best >= 0 ? Math.floor(bag[best]!.CurrentDura / 1000) : 0;
      const miss = best < 0 ? (any ? "Worn out" : "You don't have this tool") : "";
      if (best >= 0) used.add(best);
      tools.push({ shadow: tool, slot: best, have, total: have, why: miss });
      max = Math.min(max, have);
      if (miss && !why) why = `You need a ${this.name(tool)} that isn't worn out.`;
    }
    for (const ing of r.Ingredients) {
      let best = -1, total = 0, pure = false;
      const minPurity = ing.CurrentDura < ing.MaxDura ? ing.CurrentDura : -1;
      for (let i = 0; i < bag.length; i++) {
        const s = bag[i];
        if (!s || s.ItemIndex !== ing.ItemIndex || used.has(i) || locked(s)) continue;
        total += s.Count;
        if (minPurity >= 0 && s.CurrentDura < minPurity) continue; // NPCScript.Craft: ingredient purity
        pure = true;
        if (s.Count < ing.Count) continue;
        if (best < 0 || s.Count > bag[best]!.Count) best = i;
      }
      const have = best >= 0 ? bag[best]!.Count : 0;
      const miss = best >= 0 ? "" : total === 0 ? "None in your bag" : !pure ? "Purity too low" : "Not enough in one stack";
      if (best >= 0) used.add(best);
      ings.push({ shadow: ing, slot: best, have, total, why: miss });
      max = Math.min(max, Math.floor(have / Math.max(1, ing.Count)));
      const ii = this.info(ing);
      if (ii && ii.StackSize > 0) max = Math.min(max, Math.floor(ii.StackSize / Math.max(1, ing.Count))); // ingredient.Count x count <= StackSize
      if (miss && !why) why = miss === "Not enough in one stack" ? `Each ingredient must be in one stack: merge your ${this.name(ing)}.`
        : miss === "Purity too low" ? `Your ${this.name(ing)} isn't pure enough.` : T.YouDoNotHaveRequiredToolsOrIngredients;
    }
    const ri = this.info(goods);
    if (ri && ri.StackSize > 0) max = Math.min(max, ri.StackSize, Math.floor(ri.StackSize / Math.max(1, goods.Count))); // NPCScript.Craft
    if (r.Gold > 0) {
      const afford = Math.floor(inv.gold / r.Gold);
      if (afford === 0 && !why) why = T.YouDoNotHaveEnoughGold;
      max = Math.min(max, afford);
    }
    max = Math.max(0, max);
    if (max === 0 && !why) why = T.YouDoNotHaveRequiredToolsOrIngredients;
    const ok = tools.every((t) => t.slot >= 0) && ings.every((g) => g.slot >= 0);
    return { tools, ings, slots: ok ? [...tools.map((t) => t.slot), ...ings.map((g) => g.slot)] : [], max: ok ? max : 0, why: max > 0 && ok ? "" : why };
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const goods = this.goods(), recipes = this.m.recipes;
    const [npc] = npcName(this.m.npcSession.npcName);
    setText(this.title.firstChild!, npc ? `CRAFTING · ${npc.toUpperCase()}` : "CRAFTING");

    // Plans for every listed recipe (bag reads only; a few dozen recipes)
    this.plans = goods.map((g) => { const r = recipes.get(g.ItemIndex); return r ? this.plan(g, r) : null; });
    if (this.sel === null || !goods.some((g) => g.UniqueID === this.sel)) this.sel = goods[0]?.UniqueID ?? null;

    this.renderCats(goods);
    this.renderGrid(goods);
    this.renderDetail(goods);

    const rate = this.rate();
    setText(this.footGold, fmt(this.m.inventory.gold));
    setText(this.footRate, rate ? `+${rate}%` : "none");
    const done = this.visitDone, ok = this.visitOk;
    setText(this.footSession, done ? `${ok}/${done} succeeded` : "no crafts yet");
    setText(this.footLast, this.log[0]?.text ?? "");
  }

  private renderCats(goods: readonly UserItem[]): void {
    const counts = new Map<Cat, [number, number]>();
    for (const c of CATS) counts.set(c, [0, 0]);
    goods.forEach((g, i) => {
      const can = (this.plans[i]?.max ?? 0) > 0 ? 1 : 0;
      for (const c of ["All", catOf(this.info(g))] as Cat[]) { const n = counts.get(c)!; n[0]++; n[1] += can; }
    });
    const key = this.cat + "|" + CATS.map((c) => counts.get(c)!.join("/")).join(",");
    if (key === this.catsKey) return;
    this.catsKey = key;
    const rows: HTMLElement[] = [];
    for (const c of CATS) {
      const [n, can] = counts.get(c)!;
      if (c !== "All" && n === 0) continue;
      const e = h("div", "ct" + (this.cat === c ? " on" : ""), h("span", "", c), h("small", "", h("b", "", can ? `${can} ` : ""), `/ ${n}`));
      e.title = `${can} you can craft now`;
      e.addEventListener("click", () => { if (this.cat !== c) { this.cat = c; this.invalidate(); } });
      rows.push(e);
    }
    this.catBox.replaceChildren(...rows);
  }

  private renderGrid(goods: readonly UserItem[]): void {
    const q = this.query;
    const list: number[] = [];
    goods.forEach((g, i) => {
      if (this.cat !== "All" && catOf(this.info(g)) !== this.cat) return;
      const r = this.m.recipes.get(g.ItemIndex);
      if (q && !this.name(g).toLowerCase().includes(q) && !(r?.Ingredients.some((x) => this.name(x).toLowerCase().includes(q)) ?? false)) return;
      if (this.canOnly && !((this.plans[i]?.max ?? 0) > 0)) return;
      list.push(i);
    });
    // Craftable first, then by name (the server's order breaks ties)
    list.sort((a, b) => ((this.plans[b]?.max ?? 0) > 0 ? 1 : 0) - ((this.plans[a]?.max ?? 0) > 0 ? 1 : 0)
      || this.name(goods[a]!).localeCompare(this.name(goods[b]!)));
    const key = list.map((i) => `${goods[i]!.UniqueID}:${this.plans[i]?.max ?? -1}:${this.m.recipes.get(goods[i]!.ItemIndex)?.Chance ?? -1}`).join(",") + `|${this.sel}|${this.rate()}`;
    if (key === this.gridKey) return;
    this.gridKey = key;
    for (const c of this.listCells) c.dispose();
    this.listCells = [];
    const cards = list.map((i) => {
      const g = goods[i]!, r = this.m.recipes.get(g.ItemIndex), plan = this.plans[i];
      const cell = this.ui.cells.cell(LIST, i, { readOnly: true, size: 40, signal: this.signal });
      this.listCells.push(cell);
      const status = !r ? h("small", "", "Loading recipe…")
        : plan && plan.max > 0 ? h("small", "ok", `Can make ${plan.max}`) : h("small", "no", "Missing materials");
      const kids: (HTMLElement)[] = [cell.el, h("div", "t", h("b", "", this.name(g) + (g.Count > 1 ? ` ×${g.Count}` : "")), status)];
      if (r) {
        const ch = this.chance(r);
        kids.push(h("span", "ch" + (ch >= 100 ? " hi" : ch >= 50 ? " mid" : ""), `${ch}%`));
      }
      const card = h("div", "rc" + (g.UniqueID === this.sel ? " sel" : ""), ...kids);
      card.dataset.uid = String(g.UniqueID);
      return card;
    });
    if (!cards.length) cards.push(h("div", "empty", goods.length ? "No recipes match." : "This craftsman has no recipes for you."));
    this.grid.replaceChildren(...cards);
  }

  private renderDetail(goods: readonly UserItem[]): void {
    const i = goods.findIndex((g) => g.UniqueID === this.sel);
    const g = i >= 0 ? goods[i]! : null;
    const r = g ? this.m.recipes.get(g.ItemIndex) : undefined;
    const plan = i >= 0 ? this.plans[i] ?? null : null;
    this.none.hidden = !!(g && r && plan);
    this.main.hidden = !this.none.hidden;
    if (!g || !r || !plan) {
      setText(this.none, g ? "The recipe hasn't arrived yet." : "Pick a recipe.");
      return;
    }
    const busy = this.pending !== null;
    const max = plan.max;
    this.qty = Math.max(1, Math.min(this.qty, Math.max(1, max)));
    const q = this.qty;

    // Result
    const info = this.info(g), chance = this.chance(r), rate = this.rate();
    this.detailItems = { result: g, tools: r.Tools, ings: r.Ingredients };
    setText(this.resName, this.name(g));
    setText(this.resSub, `${info ? catOf(info) : ""}${g.Count > 1 ? ` · makes ${g.Count}` : ""}`);
    setText(this.resGold, r.Gold ? `${fmt(r.Gold)} gold per craft` : "No gold cost");
    this.ring.set(chance / 100, `${chance}%`, "SUCCESS");
    this.ring.el.title = rate ? `Recipe ${r.Chance}% + your craft rate ${rate}%` : `Recipe ${r.Chance}%`;

    // Tools and ingredients (rows rebuilt only when what they show changed)
    const key = `${g.UniqueID}|${q}|` + plan.tools.map((t) => `${t.slot}:${t.have}`).join(",") + "|" + plan.ings.map((x) => `${x.slot}:${x.have}:${x.total}`).join(",");
    if (key !== this.detailKey) {
      this.detailKey = key;
      for (const c of this.detailCells) c.dispose();
      this.detailCells = [];
      this.toolsLbl.hidden = this.toolsBox.hidden = plan.tools.length === 0;
      this.toolsBox.replaceChildren(...plan.tools.map((t, k) => this.toolRow(t, TOOL_BASE + k, q)));
      this.ingsBox.replaceChildren(...plan.ings.map((x, k) => this.ingRow(x, ING_BASE + k, q)));
      this.ui.cells.refresh();
    }
    setText(this.ingsFor, `for ${q} ${plural(q, "craft")}`);

    // Quantity, totals, button
    if (document.activeElement !== this.qtyInput) this.qtyInput.value = String(q);
    this.qtyInput.disabled = busy || max <= 1;
    this.minusBtn.setDisabled(busy || q <= 1);
    this.plusBtn.setDisabled(busy || q >= max, max <= 1 ? "" : `At most ${max}`);
    this.maxBtn.setLabel(`Max ${max}`);
    this.maxBtn.setDisabled(busy || max <= 1);
    setText(this.makes, fmt(g.Count * q));
    setText(this.costs, fmt(r.Gold * q));
    this.goBtn.setLabel(busy ? "CRAFTING…" : `CRAFT ×${q}`);
    this.goBtn.setDisabled(busy || max <= 0, busy ? "Waiting for the craftsman" : plan.why);

    this.resultEl.className = "why " + (this.result === "ok" ? "ok" : this.result ? "fail" : "");
    setText(this.resultEl, this.result ? this.resultText : "");
    this.whyEl.className = "why" + (max > 0 ? " info" : "");
    setText(this.whyEl, busy ? "" : max <= 0 ? plan.why
      : chance < 100 ? `${100 - chance}% chance to fail: one roll for the whole batch, and a failure loses its gold and ingredients.` : "");
  }

  private toolRow(t: Need, index: number, q: number): HTMLElement {
    const cell = this.ui.cells.cell(DETAIL, index, { readOnly: true, size: 34, signal: this.signal });
    this.detailCells.push(cell);
    const ok = t.slot >= 0 && t.have >= q;
    const n = h("div", "n", this.name(t.shadow));
    if (t.slot >= 0) {
      const s = this.m.inventory.inventory[t.slot]!;
      const fill = h("i");
      setStyle(fill, "width", `${Math.min(100, s.MaxDura > 0 ? s.CurrentDura / s.MaxDura * 100 : 0).toFixed(0)}%`);
      n.append(h("div", "du", fill));
    } else n.append(h("small", "", t.why));
    return h("div", "ing" + (ok ? "" : " miss"), cell.el, n, h("span", "c " + (ok ? "ok" : "no"), t.slot >= 0 ? `${t.have} ${plural(t.have, "craft")} left` : "0"));
  }

  private ingRow(x: Need, index: number, q: number): HTMLElement {
    const cell = this.ui.cells.cell(DETAIL, index, { readOnly: true, size: 34, signal: this.signal });
    this.detailCells.push(cell);
    const need = x.shadow.Count * q, ok = x.slot >= 0 && x.have >= need;
    const notes: HTMLElement[] = [];
    if (x.shadow.CurrentDura < x.shadow.MaxDura) notes.push(h("small", "", `Purity ${fmt1(x.shadow.CurrentDura / 1000)}+`));
    if (x.slot < 0) notes.push(h("small", "", x.why));
    else if (x.total > x.have) notes.push(h("small", "", `${fmt(x.total)} in your bag · one stack is used`));
    return h("div", "ing" + (ok ? "" : " miss"), cell.el, h("div", "n", this.name(x.shadow), ...notes),
      h("span", "c " + (ok ? "ok" : "no"), `${fmt(x.have)} / ${fmt(need)}`));
  }

  // — Craft (C.CraftItem; nothing changes until the server answers) —

  private async craft(): Promise<void> {
    if (this.pending) return;
    const goods = this.goods(), g = goods.find((x) => x.UniqueID === this.sel);
    const r = g ? this.m.recipes.get(g.ItemIndex) : undefined;
    if (!g || !r) return;
    let plan = this.plan(g, r);
    const q = Math.max(1, Math.min(this.qty, plan.max));
    if (plan.max <= 0) { this.ui.chat.system(plan.why || T.YouDoNotHaveRequiredToolsOrIngredients); return; }
    const chance = this.chance(r);
    if (chance < 100) {
      const ok = await this.ui.rt.popups.ask(
        `Craft ${q} × ${this.name(g)} for ${fmt(r.Gold * q)} gold?\nOne roll for the whole batch at ${chance}%: a failure loses the gold and every ingredient.`,
        { title: "Crafting", kind: "warning", okCancel: true, item: { name: this.name(g), img: this.info(g)?.Image ?? -1, count: g.Count * q } });
      if (!ok || !this.open || this.pending) return;
      plan = this.plan(g, r); // the bag may have changed while asking
      if (plan.max < q) { this.ui.chat.system(plan.why || T.YouDoNotHaveRequiredToolsOrIngredients); this.invalidate(); return; }
    }
    if (r.Gold * q > this.m.inventory.gold) { this.ui.chat.system(T.YouDoNotHaveEnoughGold); return; }
    const inv = this.m.inventory;
    const ids = plan.slots.map((s) => inv.inventory[s]!.UniqueID);
    this.ui.ctx.send(ClientPackets.CraftItem({ UniqueID: g.UniqueID, Count: q, Slots: [...plan.slots] }));
    for (const id of ids) inv.locked.add(String(id));
    const cancel = later(() => this.release(), ANSWER_MS, this.signal);
    this.pending = { ids, itemIndex: g.ItemIndex, count: g.Count * q, name: this.name(g), made: false, cancel };
    this.result = "";
    inv.changed();
  }

  /** Unlocks the request's cells (answer, timeout or close). */
  private release(): void {
    const p = this.pending;
    if (!p) return;
    this.pending = null;
    p.cancel();
    for (const id of p.ids) this.m.inventory.locked.delete(String(id));
    this.m.inventory.changed();
  }

  /** S.GainedItem (the crafted stack, sent before the answer) and S.CraftItem. */
  private packet(p: ServerPacket): void {
    const pend = this.pending;
    if (!pend) return;
    if (p.type === "GainedItem") { if (p.Item.ItemIndex === pend.itemIndex) pend.made = true; return; }
    if (p.type !== "CraftItem") return;
    this.release();
    if (!p.Success) {
      this.result = "refused";
      this.resultText = "The craftsman refused: check your materials, gold and bag space.";
    } else if (pend.made) {
      this.result = "ok";
      this.resultText = `Crafted ${fmt(pend.count)} × ${pend.name}!`;
      this.pushLog(true, this.resultText);
      restartAnim(this.resBox, "ok");
    } else {
      this.result = "fail";
      this.resultText = "The attempt failed. The gold and ingredients were lost.";
      this.pushLog(false, `Crafting ${pend.name} failed.`);
      restartAnim(this.resBox, "bad");
    }
    this.invalidate();
  }

  private pushLog(ok: boolean, text: string): void {
    this.visitDone++;
    if (ok) this.visitOk++;
    this.log.unshift({ ok, text });
    if (this.log.length > LOG_MAX) this.log.length = LOG_MAX;
  }
}

registerWindow(CRAFT_WINDOW, (host) => new CraftWindow(host), {
  binds: [],
  // GameScene.NPCGoods with PanelType.Craft (NpcSession emits it only while the NPC dialog is open)
  watch: (host, open) => host.ctx.models.npcSession.goods.on((g) => { if (g.type === PanelType.Craft) open(true); }),
});
