// Ported from play/hudSocket.ts (SocketMock): the new UI's sockets & gems window over the real ItemChanges model
// (src/game/itemChanges.ts). Two benches:
//  - Gems & orbs: PlayerObject.CombineItem for gems (ItemType.Gem Shape 3), orbs (Shape 4) and socket stones (Shape 7)
//    on an item in your BAG (the server combines bag items only: C.CombineItem Grid Inventory, through
//    ItemChanges.combine, which locks both cells until S.CombineItem). The rules shown are the server's, read from the
//    gem's ItemInfo (ValidGemForItem by the gem's Unique flags; Stats: CriticalRate = base chance, Reflect = minus per
//    stat point / gem already added, CriticalDamage = most gems per item, HPDrainRatePercent = most of that stat;
//    DontUpgrade / special items / rented DontUpgrade refused). The result comes from the server: S.ItemUpgraded (the
//    stat took), S.ItemSlotSizeChanged (a socket was added), S.CombineItem (Destroy = the item broke; Success without
//    an upgrade = no effect). Nothing is rolled here.
//  - Sockets: SocketDialog (Client/MirScenes/Dialogs/SocketDialog.cs; classic src/ui/socketWindow.ts) for the item
//    opened with Ctrl + right-click (core/drag.ts -> ItemChanges.openSockets -> the drag hooks' onSockets, wired by
//    this window's watch hook): its Slots as real "socket" item cells (right-click or drag one to the bag removes it:
//    C.RemoveSlotItem), the socket items (ItemType.Socket) in your bag (click one, drag it onto a socket or right-click
//    it in the bag: ItemChanges.useSlotItem -> C.EquipSlotItem, the first free socket that fits its shape), and the stats
//    the socketed items add.
// HIDDEN: the Sockets bench while no item with sockets is open (the mock's sample sockets / jewels: Crystal items only
// have sockets when ItemInfo.Slots or a socket stone gives them); worn gear in the gem list (bag items only); the exact
// success chance unless S.ServerRules has arrived (Settings.GemStatIndependent decides what the penalty multiplies:
// that stat's added points, or the item's gem count); the mock's sample data, local rolls and timers.
// No menu entry or hotkey: Ctrl + right-click opens it; showGemBench(host, item) opens the gem bench from code.
// Narrow layout (below a 1040 px layer): the three columns wrap (bench on top, the lists side by side below).

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { BindMode, ItemType, ItemTypeName, SpecialItemMode, Stat, StatName } from "../../../protocol/enums.generated.ts";
import { SOCKET_GRID_SLOTS } from "../../../game/itemChanges.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { serverRules, type ServerRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, h, holdButton, injectCss, pretty, restartAnim, setStyle, setText, tabs, type Btn,
} from "../core/index.ts";
import { gradeVar } from "../core/styles.ts";
import type { CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: Ctrl + right-click opens it). */
export const SOCKET_KEY = "socket";

/** ItemType.Gem shapes this bench applies (PlayerObject.CombineItem): 3 gem, 4 orb, 7 socket stone. */
const GEM = 3, ORB = 4, SLOT_STONE = 7;
const BENCH_SHAPES: ReadonlySet<number> = new Set([GEM, ORB, SLOT_STONE]);
/** ValidGemForItem: the target's ItemType -> the SpecialItemMode flag the gem must carry. */
const GEM_FITS: Readonly<Partial<Record<number, number>>> = {
  [ItemType.Weapon]: SpecialItemMode.Paralize, [ItemType.Armour]: SpecialItemMode.Teleport, [ItemType.Helmet]: SpecialItemMode.ClearRing,
  [ItemType.Necklace]: SpecialItemMode.Protection, [ItemType.Bracelet]: SpecialItemMode.Revival, [ItemType.Ring]: SpecialItemMode.Muscle,
  [ItemType.Amulet]: SpecialItemMode.Flame, [ItemType.Belt]: SpecialItemMode.Healing, [ItemType.Boots]: SpecialItemMode.Probe,
  [ItemType.Stone]: SpecialItemMode.Skill,
};
/** The stat a gem adds, in CombineItem's / GetCurrentStatCount's order ("dura" = a durability gem: ItemInfo.Durability). */
const COMBINE_ORDER: readonly (number | "dura")[] = [
  Stat.MaxDC, Stat.MaxMC, Stat.MaxSC, Stat.MaxAC, Stat.MaxMAC, "dura", Stat.AttackSpeed, Stat.Agility, Stat.Accuracy,
  Stat.PoisonAttack, Stat.Freezing, Stat.MagicResist, Stat.PoisonResist, Stat.Luck,
];
/** GetGemType's order (GemStatIndependent: the penalty multiplies the item's added points of this stat). */
const GEM_TYPE_ORDER: readonly number[] = [
  Stat.MaxDC, Stat.MaxMC, Stat.MaxSC, Stat.MaxAC, Stat.MaxMAC, Stat.AttackSpeed, Stat.Agility, Stat.Accuracy, Stat.PoisonAttack,
  Stat.Freezing, Stat.MagicResist, Stat.PoisonResist, Stat.Luck, Stat.PoisonRecovery, Stat.HP, Stat.MP, Stat.HealthRecovery,
];

const statText = (s: number | "dura"): string => s === "dura" ? "Max durability" : pretty(StatName[s] ?? `Stat ${s}`);
const total = (item: UserItem, info: ItemInfo, s: number): number => (info.Stats[s] ?? 0) + (item.AddedStats[s] ?? 0); // UserItem.GetTotal

/** What applying `gem` to `item` would do, by the server's rules (no roll). */
export interface GemPlan {
  /** "" = the server would try it, else why not. */
  readonly why: string;
  readonly shape: number;
  /** The stat it adds (null for a socket stone or an unknown gem). */
  readonly stat: number | "dura" | null;
  /** How much it adds on success (points, or max durability). */
  readonly gain: number;
  /** Gems already on the item / the most this gem allows. */
  readonly gems: number; readonly maxGems: number;
  /** Points of that stat already added / the most this gem allows. */
  readonly have: number; readonly maxStat: number;
  /** The success chance in % (null = unknown: S.ServerRules not received) and its parts. */
  readonly chance: number | null; readonly base: number; readonly penalty: number; readonly times: number; readonly gemRate: number;
  /** % chance the item is destroyed (gems only: 3 in 15 failures). */
  readonly destroy: number | null;
}

/** PlayerObject.CombineItem's checks and chance for a gem / orb / socket stone on a bag item. `rules` null = the server
 *  rules have not arrived (the chance stays unknown); `gemRate` = the user's Stat.GemRatePercent. */
export function gemPlan(gem: UserItem, gemInfo: ItemInfo, item: UserItem, info: ItemInfo, rules: ServerRules | null, gemRate: number): GemPlan {
  const shape = gemInfo.Shape;
  const st = gemInfo.Stats;
  const base = st[Stat.CriticalRate] ?? 0, penalty = st[Stat.Reflect] ?? 0, maxGems = st[Stat.CriticalDamage] ?? 0, maxStat = st[Stat.HPDrainRatePercent] ?? 0;
  const plan = { why: "", shape, stat: null as number | "dura" | null, gain: 0, gems: item.GemCount, maxGems, have: 0, maxStat, chance: null as number | null,
    base, penalty, times: 0, gemRate, destroy: null as number | null };
  const fit = GEM_FITS[info.Type];
  if (info.Type < ItemType.Weapon || info.Type > ItemType.Stone) return { ...plan, why: "Gems don't go on this kind of item" };
  if ((info.Bind & BindMode.DontUpgrade) || info.Unique !== SpecialItemMode.None) return { ...plan, why: "This item can't be upgraded" };
  if (item.Rental && (item.Rental.BindingFlags & BindMode.DontUpgrade)) return { ...plan, why: `Rented from ${item.Rental.OwnerName}: can't be upgraded` };
  const fits = fit !== undefined && (gemInfo.Unique & fit) !== 0;
  if (shape === SLOT_STONE) {
    if (!fits) return { ...plan, why: `Doesn't fit a ${pretty(ItemTypeName[info.Type] ?? "").toLowerCase()}` };
    return { ...plan, chance: 100, destroy: 0 }; // the server also checks the item's socket limit (RandomStats.SlotMaxStat)
  }
  // The stat (CombineItem's branch order) and how much of it the item has (GetCurrentStatCount)
  let stat: number | "dura" | null = null;
  for (const s of COMBINE_ORDER) {
    if (s === "dura" ? gemInfo.Durability > 0 : total(gem, gemInfo, s) > 0) { stat = s; break; }
  }
  const have = stat === null ? 0 : stat === "dura"
    ? (info.Durability > item.MaxDura ? 0 : Math.trunc((item.MaxDura - info.Durability) / 1000))
    : item.AddedStats[stat] ?? 0;
  const gain = stat === null ? 0 : stat === "dura" ? gem.MaxDura : total(gem, gemInfo, stat);
  const out = { ...plan, stat, gain, have };
  if (item.GemCount >= maxGems || have >= maxStat) return { ...out, why: "This item already holds as much as this gem allows" };
  if (!fits) return { ...out, why: `Doesn't fit a ${pretty(ItemTypeName[info.Type] ?? "").toLowerCase()}` };
  if (stat === null) return { ...out, why: "These items can't be combined" };
  if (!rules) return out;
  // successchance = Reflect x (GemStatIndependent ? the item's added points of GetGemType : GemCount)
  let times = item.GemCount;
  if (rules.GemStatIndependent) {
    const type = GEM_TYPE_ORDER.find((s) => total(gem, gemInfo, s) > 0);
    if (type !== undefined) times = item.AddedStats[type] ?? 0;
  }
  const chance = penalty * times >= base ? 0 : base - penalty * times + gemRate;
  const c = Math.max(0, Math.min(100, chance));
  return { ...out, times, chance, destroy: shape === GEM ? (100 - c) * 3 / 15 : 0 };
}

const CSS = `
.hudm .skt { width: 900px; }
.hudm .skt .panel { display: flex; flex-direction: column; }
.hudm .skt .ui-tabs { margin: 6px 12px 0; }
.hudm .skt .body { display: flex; min-height: 0; height: 470px; }
.hudm .skt .col { padding: 10px; overflow-y: auto; display: flex; flex-direction: column; gap: 5px; scrollbar-width: thin; }
.hudm .skt .col > * { flex-shrink: 0; }
.hudm .skt .col.l { width: 220px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .skt .col.r { width: 250px; flex: none; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .skt .lbl { display: flex; justify-content: space-between; margin-top: 4px; }
.hudm .skt .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .skt .pc { display: flex; gap: 8px; align-items: center; padding: 4px 6px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .skt .pc:hover { border-color: color-mix(in srgb, var(--ac) 55%, transparent); }
.hudm .skt .pc.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .skt .pc.no { opacity: .4; }
.hudm .skt .pc .t { flex: 1; min-width: 0; font-size: 12px; }
.hudm .skt .pc .t b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .skt .pc .t small { font-size: 10px; color: var(--dim); }
.hudm .skt .pc .n { font-weight: 700; font-size: 12px; color: var(--tx); }
.hudm .skt .pc .orb { font-size: 8px; font-weight: 800; color: var(--gold); }
.hudm .skt .mid { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; padding: 12px;
  background: radial-gradient(circle at 50% 42%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 55%); }
.hudm .skt .pair { display: flex; align-items: center; gap: 22px; }
.hudm .skt .sk { display: flex; flex-direction: column; align-items: center; gap: 4px; font-size: 11px; color: var(--dim); text-align: center; max-width: 150px; }
.hudm .skt .sk .icell { border-radius: 50%; } .hudm .skt .sk .icell::after { border-radius: 50%; }
.hudm .skt .sk b { color: #fff; font-size: 13px; }
.hudm .skt .arrow { font-size: 28px; color: var(--ac); }
.hudm .skt .sk.win .icell { animation: hudm-sktwin .9s ease-out; } @keyframes hudm-sktwin { 30% { transform: scale(1.2); } }
.hudm .skt .sk.boom .icell { animation: hudm-sktboom .9s ease-in; } @keyframes hudm-sktboom { 40% { transform: scale(1.25); } 100% { transform: scale(.2); opacity: 0; } }
.hudm .skt .sk.dud .icell { animation: hudm-sktdud .5s; } @keyframes hudm-sktdud { 25%, 75% { transform: translateX(-4px); } 50% { transform: translateX(4px); } }
.hudm .skt .added { display: flex; flex-wrap: wrap; justify-content: center; gap: 5px; }
.hudm .skt .added span { font-size: 11px; padding: 2px 9px; border-radius: 9px; border: 1px solid var(--fr); } .hudm .skt .added span b { color: var(--ok); }
.hudm .skt .cap { display: flex; gap: 14px; font-size: 11px; color: var(--dim); } .hudm .skt .cap b { color: #fff; } .hudm .skt .cap b.max { color: var(--bad-soft); }
.hudm .skt .big { font-size: 30px; font-weight: 800; color: #fff; text-align: center; line-height: 1; } .hudm .skt .big small { display: block; font-size: 9px; letter-spacing: .14em; color: var(--dim); margin-top: 4px; }
.hudm .skt .calc { font-size: 11px; color: var(--dim); text-align: center; } .hudm .skt .calc b { color: var(--tx); }
.hudm .skt .odds { display: flex; height: 12px; width: 100%; max-width: 360px; border-radius: 6px; overflow: hidden; border: 1px solid var(--fr); } .hudm .skt .odds i { display: block; height: 100%; width: 0; }
.hudm .skt .odds .w { background: #4fbf6a; } .hudm .skt .odds .n { background: #556; } .hudm .skt .odds .d { background: #b33; }
.hudm .skt .ol { display: flex; justify-content: space-between; width: 100%; max-width: 360px; font-size: 10px; color: var(--dim); } .hudm .skt .ol b { color: #fff; }
.hudm .skt .go { width: 100%; max-width: 360px; height: 36px; }
.hudm .skt .warn { font-size: 11px; color: var(--bad-soft); text-align: center; } .hudm .skt .warn.ok { color: var(--ok); }
.hudm .skt .log { font-size: 11px; color: var(--dim); width: 100%; max-width: 360px; } .hudm .skt .log div { padding: 2px 0; }
.hudm .skt .note { font-size: 11px; line-height: 1.45; color: var(--dim); padding: 8px 10px; border-radius: var(--rad); border: 1px dashed var(--fr); } .hudm .skt .note b { color: var(--tx); }
.hudm .skt .sgrid { display: grid; grid-template-columns: repeat(6, 44px); gap: 6px; }
.hudm .skt .empty-note { padding: 8px 4px; }
.hudm.narrow .skt { width: 640px; }
.hudm.narrow .skt .body { flex-wrap: wrap; height: auto; max-height: 560px; overflow-y: auto; }
.hudm.narrow .skt .mid { order: -1; flex: 1 0 100%; }
.hudm.narrow .skt .col.l, .hudm.narrow .skt .col.r { width: 50%; max-height: 220px; border: 0; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
@media (prefers-reduced-motion: reduce) { .hudm .skt .sk .icell { animation: none !important; } }
`;

type Fx = "" | "win" | "boom" | "dud";
/** A combine waiting for S.CombineItem (the model has locked both cells). */
interface PendingCombine { readonly gem: bigint; readonly to: bigint; readonly gemName: string; readonly itemName: string; upgraded: boolean; socket: boolean }

class SocketWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: 0 | 1 = 0;
  /** The selected target (bag item) and gem (ItemIndex): resolved per render, so a destroyed item never stays selected. */
  private targetId: bigint | null = null;
  private gemIndex = -1;
  private pending: PendingCombine | null = null;
  private fx: Fx = "";
  private msg: [string, boolean] = ["", false];
  private readonly log: string[] = [];
  private listKey = "";
  private sockKey = "";
  private goKey = "";
  private listScope: AbortController | null = null;

  // — DOM —
  private readonly tabBar: ReturnType<typeof tabs>;
  private readonly gemsView = h("div", "body");
  private readonly sockView = h("div", "body");
  private readonly gearCol = h("div", "col l");
  private readonly gemCol = h("div", "col r");
  private readonly mid = h("div", "mid");
  private readonly gemSlot = h("div", "sk");
  private readonly targetSlot = h("div", "sk");
  private readonly gemName = h("b");
  private readonly gemSub = h("span");
  private readonly targetName = h("b");
  private readonly targetSub = h("span");
  private readonly gemCell: ItemCell;
  private readonly targetCell: ItemCell;
  private readonly added = h("div", "added");
  private readonly cap = h("div", "cap");
  private readonly big = h("div", "big");
  private readonly calc = h("div", "calc");
  private readonly odds = h("div", "odds");
  private readonly oddsW = h("i", "w");
  private readonly oddsN = h("i", "n");
  private readonly oddsD = h("i", "d");
  private readonly ol = h("div", "ol");
  private readonly goBox = h("div", "go");
  private go: Btn | null = null;
  private readonly warnEl = h("div", "warn");
  private readonly msgEl = h("div", "warn");
  private readonly logEl = h("div", "log");
  // Sockets bench
  private readonly sockLeft = h("div", "col l");
  private readonly sockMid = h("div", "mid");
  private readonly sockRight = h("div", "col r");
  private readonly sockTitle = h("b");
  private readonly sockGrid = h("div", "sgrid");
  private readonly sockStats = h("div", "added");
  private readonly sockCells: ItemCell[] = [];
  private readonly sockItemCell: ItemCell;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "skt", 120, 40);
    injectCss("win-socket", CSS);
    this.m = ui.ctx.models;
    const sig = ui.rt.signal;

    this.tabBar = tabs(["Gems & orbs", "Sockets"], 0, (i) => { this.tab = i as 0 | 1; this.invalidate(); });

    // Gems bench: [gem] -> [target], the item's added stats, caps, the chance, odds, apply, the result
    this.gemCell = ui.cells.cell("inv", 0, { size: 64, readOnly: true });
    this.targetCell = ui.cells.cell("inv", 0, { size: 74, readOnly: true });
    this.gemSlot.append(this.gemCell.el, this.gemName, this.gemSub);
    this.targetSlot.append(this.targetCell.el, this.targetName, this.targetSub);
    this.odds.append(this.oddsW, this.oddsN, this.oddsD);
    this.mid.append(h("div", "pair", this.gemSlot, h("div", "arrow", "➜"), this.targetSlot), this.added, this.cap, this.big, this.calc,
      this.odds, this.ol, this.goBox, this.warnEl, this.msgEl, this.logEl);
    this.gemsView.append(this.gearCol, this.mid, this.gemCol);

    // Sockets bench: the opened item, its socket cells (6 x 2 like SocketDialog), the socket items in your bag
    this.sockItemCell = ui.cells.cell("inv", 0, { size: 64, readOnly: true });
    for (let i = 0; i < SOCKET_GRID_SLOTS; i++) {
      const c = ui.cells.cell("socket", i, { size: 44, filter: (_it, info) => info && info.Type !== ItemType.Socket ? "Only socket items" : "" });
      this.sockCells.push(c);
      this.sockGrid.append(c.el);
    }
    this.sockLeft.append(h("div", "lbl", "Socketed item"), h("div", "sk", this.sockItemCell.el, this.sockTitle),
      h("div", "note", "Right-click a filled socket (or drag it to your bag) to take the item out. Drag a socket item onto a socket, click it on the right, or right-click it in your bag to socket it."));
    this.sockMid.append(h("div", "lbl", "Sockets"), this.sockGrid, h("div", "lbl", "From sockets"), this.sockStats);
    this.sockView.append(this.sockLeft, this.sockMid, this.sockRight);

    this.el.append(h("div", "panel", this.titleBar("SOCKETS & GEMS", "Close"), this.tabBar.el, this.gemsView, this.sockView));

    // Model events: re-render while shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.m.inventory.subscribe(() => this.invalidate()));
    on(serverRules(ui.ctx.session).changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.listKey = this.sockKey = ""; this.invalidate(); } }));
    on(ui.packets.on((p) => this.answer(p)));
  }

  /** Ctrl + right-click opened an item's sockets: show the Sockets bench (and select the item when it is in the bag). */
  showSockets(): void {
    const item = this.m.itemChanges.socketItem();
    if (item && this.m.inventory.inventory.some((i) => i?.UniqueID === item.UniqueID)) this.targetId = item.UniqueID;
    this.tab = item && item.Slots.length > 0 ? 1 : 0;
    this.sockKey = "";
    this.invalidate();
  }

  /** The gem bench, with `item` (a bag item) selected when given. */
  showGems(item?: UserItem | null): void {
    if (item) this.targetId = item.UniqueID;
    this.tab = 0;
    this.invalidate();
  }

  protected override closed(): void {
    // SocketDialog.Hide: GameScene.SelectedItem = null (the socket cells go with it)
    const c = this.m.itemChanges;
    if (c.socketId !== null) c.closeSockets();
    const pick = this.m.itemPick;
    if (pick.cell?.grid === "socket") pick.clear();
    this.fx = "";
    this.msg = ["", false];
  }

  // — Render —

  protected override render(): void {
    const sockItem = this.m.itemChanges.socketItem();
    const hasSockets = !!sockItem && sockItem.Slots.length > 0;
    const tabBtn = this.tabBar.el.children[1] as HTMLElement | undefined;
    if (tabBtn) tabBtn.hidden = !hasSockets;
    if (!hasSockets && this.tab === 1) this.tab = 0;
    if (this.tabBar.active !== this.tab) this.tabBar.select(this.tab);
    this.gemsView.hidden = this.tab !== 0;
    this.sockView.hidden = this.tab !== 1;
    if (this.tab === 0) this.renderGems(); else this.renderSockets(sockItem!);
  }

  /** The bag's upgradable items and its gems (ItemType.Gem shapes 3 / 4 / 7, grouped by ItemIndex). */
  private scan(): { gear: number[]; gems: Map<number, { index: number; count: number }> } {
    const inv = this.m.inventory, gear: number[] = [], gems = new Map<number, { index: number; count: number }>();
    for (let i = 0; i < inv.inventory.length; i++) {
      const it = inv.inventory[i];
      const info = it ? this.m.itemInfos.get(it.ItemIndex) : undefined;
      if (!it || !info) continue;
      if (info.Type >= ItemType.Weapon && info.Type <= ItemType.Stone) gear.push(i);
      else if (info.Type === ItemType.Gem && BENCH_SHAPES.has(info.Shape)) {
        const g = gems.get(it.ItemIndex);
        if (!g) gems.set(it.ItemIndex, { index: i, count: it.Count });
        else { g.count += it.Count; if (inv.isLocked(inv.inventory[g.index] ?? null) && !inv.isLocked(it)) g.index = i; }
      }
    }
    return { gear, gems };
  }

  private renderGems(): void {
    const inv = this.m.inventory, { gear, gems } = this.scan();
    // Resolve the selections (a destroyed / moved item falls back to the first one)
    let tIndex = this.targetId === null ? -1 : inv.inventory.findIndex((i) => i?.UniqueID === this.targetId);
    if (tIndex < 0 || !gear.includes(tIndex)) { tIndex = gear[0] ?? -1; this.targetId = tIndex >= 0 ? inv.inventory[tIndex]!.UniqueID : null; }
    if (this.gemIndex >= 0 && !gems.has(this.gemIndex)) this.gemIndex = -1;
    const target = tIndex >= 0 ? inv.inventory[tIndex]! : null;
    const tInfo = target ? this.m.itemInfos.get(target.ItemIndex) : undefined;
    const g = this.gemIndex >= 0 ? gems.get(this.gemIndex)! : null;
    const gem = g ? inv.inventory[g.index] ?? null : null;
    const gInfo = gem ? this.m.itemInfos.get(gem.ItemIndex) : undefined;
    const rules = serverRules(this.ui.ctx.session).rules;
    const gemRate = this.ui.ctx.player.stats?.stats[Stat.GemRatePercent] ?? 0;
    const plan = gem && gInfo && target && tInfo ? gemPlan(gem, gInfo, target, tInfo, rules, gemRate) : null;

    this.renderLists(gear, gems, tIndex, target, tInfo);

    // The pair
    this.gemCell.setRef({ grid: "inv", index: g ? g.index : -1 });
    this.targetCell.setRef({ grid: "inv", index: tIndex });
    setText(this.gemName, gInfo ? this.nameOf(gem!, gInfo) : "Pick a gem or orb");
    setText(this.gemSub, gInfo ? (gInfo.Shape === ORB ? "Orb" : gInfo.Shape === SLOT_STONE ? "Socket stone" : "Gem") : "from the right");
    setText(this.targetName, tInfo ? this.nameOf(target!, tInfo) : "No item to upgrade");
    this.targetName.style.color = tInfo ? gradeVar(tInfo.Grade) : "";
    setText(this.targetSub, tInfo ? `${pretty(ItemTypeName[tInfo.Type] ?? "")} · ${target!.GemCount} ${target!.GemCount === 1 ? "gem" : "gems"} · ${target!.Slots.length} sockets` : "Put an upgradable item in your bag");
    if (this.fx) {
      for (const c of ["win", "boom", "dud"]) if (c !== this.fx) this.targetSlot.classList.remove(c);
      restartAnim(this.targetSlot, this.fx);
      this.fx = "";
    }

    // The item's added stats
    const kids: HTMLElement[] = [];
    if (target) for (const [k, v] of Object.entries(target.AddedStats)) if (v) kids.push(h("span", "", `${statText(Number(k))} `, h("b", "", `+${fmt(v)}`)));
    if (target && !kids.length) kids.push(h("span", "", "No added stats yet"));
    this.added.replaceChildren(...kids);

    // Caps, chance, odds
    const showOdds = !!plan && !plan.why && plan.chance !== null;
    this.cap.hidden = !plan || plan.shape === SLOT_STONE;
    if (plan && plan.shape !== SLOT_STONE) {
      this.cap.replaceChildren(h("span", "", "Gems ", h("b", plan.gems >= plan.maxGems ? "max" : "", `${plan.gems}/${plan.maxGems}`)),
        h("span", "", `${plan.stat === null ? "Stat" : statText(plan.stat)} `, h("b", plan.have >= plan.maxStat ? "max" : "", `${plan.have}/${plan.maxStat}`)));
    }
    this.big.hidden = !plan || !!plan.why;
    this.calc.hidden = this.big.hidden;
    if (plan && !plan.why) {
      if (plan.shape === SLOT_STONE) {
        this.big.replaceChildren("+1", h("small", "", "SOCKET"));
        setText(this.calc, "Always works if the item can take another socket (the server checks its limit).");
      } else if (plan.chance === null) {
        this.big.replaceChildren("?", h("small", "", "SUCCESS CHANCE"));
        setText(this.calc, "The server's gem rules have not arrived: the chance is the server's.");
      } else {
        this.big.replaceChildren(`${Math.max(0, plan.chance)}%`, h("small", "", "SUCCESS CHANCE"));
        const per = rules?.GemStatIndependent && plan.stat !== "dura" ? `${plan.stat === null ? "" : statText(plan.stat)} points added` : "gems on the item";
        this.calc.replaceChildren("base ", h("b", "", `${plan.base}%`), " − ", h("b", "", `${plan.penalty}%`), ` × ${plan.times} ${per}`,
          plan.gemRate ? ` + ${plan.gemRate}% gem rate` : "");
      }
    }
    this.odds.hidden = this.ol.hidden = !showOdds || plan!.shape === SLOT_STONE;
    if (showOdds && plan!.shape !== SLOT_STONE) {
      const w = Math.max(0, Math.min(100, plan!.chance!)), d = plan!.destroy ?? 0, n = Math.max(0, 100 - w - d);
      setStyle(this.oddsW, "width", `${w}%`); setStyle(this.oddsN, "width", `${n}%`); setStyle(this.oddsD, "width", `${d}%`);
      this.ol.replaceChildren(h("span", "", h("b", "", `${w.toFixed(0)}%`), ` +${fmt(plan!.gain)} ${plan!.stat === null ? "" : statText(plan!.stat)}`),
        h("span", "", h("b", "", `${n.toFixed(0)}%`), " no effect"), h("span", "", h("b", "", `${d.toFixed(0)}%`), " destroyed"));
    }

    // Apply: hold for a gem (it can destroy the item), a click + question for orbs / socket stones (YouWantCombineItems)
    const risky = !!gInfo && gInfo.Shape === GEM;
    const key = risky ? "hold" : "click";
    if (key !== this.goKey || !this.go) {
      this.goKey = key;
      this.go = risky
        ? holdButton("Hold to apply", () => this.apply(), { cls: "main", danger: true, ms: 1100 })
        : button("Apply", () => void this.confirmApply(), { cls: "main" });
      this.go.el.classList.add("go");
      this.goBox.replaceChildren(this.go.el);
    }
    let why = "";
    if (this.pending) why = "Waiting for the server";
    else if (!target) why = "Put an upgradable item in your bag";
    else if (!gem) why = "Pick a gem or orb";
    else if (plan?.why) why = plan.why;
    else if (inv.isLocked(gem) || inv.isLocked(target)) why = "Waiting for the server";
    this.go.setDisabled(!!why, why);
    if (risky && plan && !plan.why && plan.destroy !== null) this.go.setLabel(`Hold to apply · ${plan.destroy.toFixed(0)}% risk`);
    else if (risky) this.go.setLabel("Hold to apply");
    setText(this.warnEl, plan?.why ?? (gem || !target ? "" : "Pick a gem or orb that fits this item (lit on the right)."));
    this.warnEl.classList.remove("ok");
    setText(this.msgEl, this.msg[0]);
    this.msgEl.classList.toggle("ok", this.msg[1]);
    this.logEl.replaceChildren(...this.log.slice(0, 4).map((l) => h("div", "", l)));
  }

  /** The gear and gem lists (rebuilt only when what they show changed). */
  private renderLists(gear: number[], gems: Map<number, { index: number; count: number }>, tIndex: number, target: UserItem | null, tInfo: ItemInfo | undefined): void {
    const inv = this.m.inventory;
    const rules = serverRules(this.ui.ctx.session).rules, gemRate = this.ui.ctx.player.stats?.stats[Stat.GemRatePercent] ?? 0;
    const gearKey = gear.map((i) => { const it = inv.inventory[i]!; return `${i}:${it.UniqueID}:${it.GemCount}:${JSON.stringify(it.AddedStats)}:${it.MaxDura}`; }).join(",");
    const gemKey = [...gems].map(([k, v]) => `${k}:${v.index}:${v.count}`).join(",");
    const key = `${gearKey}|${gemKey}|${tIndex}|${this.gemIndex}|${!!rules}`;
    if (key === this.listKey) return;
    this.listKey = key;
    this.sockKey = ""; // the socket list shared the scope
    this.listScope?.abort();
    const scope = this.listScope = new AbortController();
    const cell = (index: number) => this.ui.cells.cell("inv", index, { size: 36, readOnly: true, signal: scope.signal });

    const gearRows: HTMLElement[] = [];
    for (const i of gear) {
      const it = inv.inventory[i]!, info = this.m.itemInfos.get(it.ItemIndex)!;
      const nm = h("b", "", this.nameOf(it, info));
      nm.style.color = gradeVar(info.Grade);
      const cant = (info.Bind & BindMode.DontUpgrade) || info.Unique !== SpecialItemMode.None;
      const row = h("div", "pc" + (i === tIndex ? " sel" : "") + (cant ? " no" : ""), cell(i).el,
        h("div", "t", nm, h("small", "", pretty(ItemTypeName[info.Type] ?? ""))), h("span", "n", `◆${it.GemCount}`));
      if (cant) row.title = "This item can't be upgraded";
      row.addEventListener("click", () => { if (this.pending) return; this.targetId = it.UniqueID; this.msg = ["", false]; this.invalidate(); }, { signal: scope.signal });
      gearRows.push(row);
    }
    this.gearCol.replaceChildren(h("div", "lbl", "Your bag", h("b", "", "gems used")),
      ...(gearRows.length ? gearRows : [h("div", "empty-note", "No upgradable items in your bag (gems work on bag items only).")]));

    const gemRows: HTMLElement[] = [];
    const sorted = [...gems].sort(([a], [b]) => {
      const ia = this.m.itemInfos.get(a), ib = this.m.itemInfos.get(b);
      return (ia?.Shape ?? 0) - (ib?.Shape ?? 0) || (ia?.Name ?? "").localeCompare(ib?.Name ?? "");
    });
    for (const [itemIndex, gg] of sorted) {
      const gem = inv.inventory[gg.index]!, gInfo = this.m.itemInfos.get(itemIndex)!;
      const p = target && tInfo ? gemPlan(gem, gInfo, target, tInfo, rules, gemRate) : null;
      const row = h("div", "pc" + (itemIndex === this.gemIndex ? " sel" : "") + (p?.why ? " no" : ""), cell(gg.index).el,
        h("div", "t", h("b", "", this.nameOf(gem, gInfo)), h("small", "", p?.why || (p ? "Fits this item" : ""))),
        gInfo.Shape === ORB ? h("span", "orb", "ORB") : gInfo.Shape === SLOT_STONE ? h("span", "orb", "SOCKET") : null,
        h("span", "n", `×${fmt(gg.count)}`));
      if (p?.why) row.title = p.why;
      row.addEventListener("click", () => { if (this.pending) return; this.gemIndex = itemIndex; this.msg = ["", false]; this.invalidate(); }, { signal: scope.signal });
      gemRows.push(row);
    }
    this.gemCol.replaceChildren(h("div", "lbl", "Gems & orbs", h("b", "", "click to pick")),
      ...(gemRows.length ? gemRows : [h("div", "empty-note", "No gems, orbs or socket stones in your bag.")]),
      h("div", "note", h("b", "", "Gems"), " can destroy the item when they fail (3 in 15 failures). ", h("b", "", "Orbs"), " never do. The gem is used up either way."));
  }

  private renderSockets(item: UserItem): void {
    const info = this.m.itemInfos.get(item.ItemIndex);
    const n = Math.min(item.Slots.length, SOCKET_GRID_SLOTS);
    const bagIndex = this.m.inventory.inventory.findIndex((i) => i?.UniqueID === item.UniqueID);
    const eqIndex = bagIndex < 0 ? this.m.inventory.equipment.findIndex((i) => i?.UniqueID === item.UniqueID) : -1;
    this.sockItemCell.setRef(bagIndex >= 0 ? { grid: "inv", index: bagIndex } : { grid: "eq", index: eqIndex });
    setText(this.sockTitle, info ? this.nameOf(item, info) : "");
    this.sockTitle.style.color = info ? gradeVar(info.Grade) : "";
    this.sockCells.forEach((c, i) => { c.el.hidden = i >= n; });
    for (const s of item.Slots) if (s && !this.m.itemInfos.get(s.ItemIndex)) this.m.gameInfo.requestItem(s.ItemIndex); // GameScene.Bind

    // Stats from the socketed items
    const sum = new Map<number, number>();
    for (const s of item.Slots) {
      const si = s ? this.m.itemInfos.get(s.ItemIndex) : undefined;
      if (!s || !si) continue;
      for (const [k, v] of Object.entries(si.Stats)) if (v) sum.set(Number(k), (sum.get(Number(k)) ?? 0) + v);
      for (const [k, v] of Object.entries(s.AddedStats)) if (v) sum.set(Number(k), (sum.get(Number(k)) ?? 0) + v);
    }
    const chips = [...sum].map(([k, v]) => h("span", "", `${statText(k)} `, h("b", "", `+${fmt(v)}`)));
    this.sockStats.replaceChildren(...(chips.length ? chips : [h("span", "", "Empty sockets add nothing")]));

    // The socket items in your bag (shape 1 weapons, 2 armour, 3 jewellery: MirItemCell.UseSlotItem)
    const inv = this.m.inventory;
    const list: number[] = [];
    for (let i = 0; i < inv.inventory.length; i++) {
      const it = inv.inventory[i], ii = it ? this.m.itemInfos.get(it.ItemIndex) : undefined;
      if (it && ii && ii.Type === ItemType.Socket) list.push(i);
    }
    const key = `${item.UniqueID}|${list.map((i) => `${i}:${inv.inventory[i]!.UniqueID}`).join(",")}`;
    if (key === this.sockKey) return;
    this.sockKey = key;
    this.listScope?.abort();
    const scope = this.listScope = new AbortController();
    this.listKey = ""; // the gem lists shared the scope
    const rows = list.map((i) => {
      const it = inv.inventory[i]!, ii = this.m.itemInfos.get(it.ItemIndex)!;
      const why = socketFit(ii, info);
      const row = h("div", "pc" + (why ? " no" : ""), this.ui.cells.cell("inv", i, { size: 36, readOnly: true, signal: scope.signal }).el,
        h("div", "t", h("b", "", this.nameOf(it, ii)), h("small", "", why || "Click to socket it")));
      if (why) row.title = why;
      row.addEventListener("click", () => {
        if (why) { this.ui.drag.toast(why); return; }
        const ref: CellRef = { grid: "inv", index: i };
        this.ui.drag.request(() => this.m.itemChanges.useSlotItem(i), [ref]);
      }, { signal: scope.signal });
      return row;
    });
    this.sockRight.replaceChildren(h("div", "lbl", "Socket items", h("b", "", "in your bag")),
      ...(rows.length ? rows : [h("div", "empty-note", "No socket items in your bag.")]));
  }

  // — Actions —

  /** Orbs / socket stones: the desktop's question (YouWantCombineItems), then apply. */
  private async confirmApply(): Promise<void> {
    if (!await this.ui.rt.popups.ask("Do you want to try and combine these items?", { kind: "question" })) return;
    this.apply();
  }

  /** C.CombineItem through ItemChanges.combine (locks both cells until S.CombineItem); paced like every item request. */
  private apply(): void {
    if (this.pending) return;
    const inv = this.m.inventory, { gems } = this.scan();
    const g = gems.get(this.gemIndex);
    const from = g ? g.index : -1, to = this.targetId === null ? -1 : inv.inventory.findIndex((i) => i?.UniqueID === this.targetId);
    const gem = from >= 0 ? inv.inventory[from] : null, target = to >= 0 ? inv.inventory[to] : null;
    if (!gem || !target) return;
    const gInfo = this.m.itemInfos.get(gem.ItemIndex), tInfo = this.m.itemInfos.get(target.ItemIndex);
    if (!gInfo || !tInfo) return;
    const plan = gemPlan(gem, gInfo, target, tInfo, serverRules(this.ui.ctx.session).rules, this.ui.ctx.player.stats?.stats[Stat.GemRatePercent] ?? 0);
    if (plan.why) { this.ui.drag.toast(plan.why); return; }
    this.pending = { gem: gem.UniqueID, to: target.UniqueID, gemName: this.nameOf(gem, gInfo), itemName: this.nameOf(target, tInfo), upgraded: false, socket: false };
    this.msg = ["", false];
    const ids = [gem.UniqueID, target.UniqueID];
    this.ui.drag.request(() => {
      // Re-read the cells when the pacer runs: the model refuses empty / locked cells
      const f = inv.inventory.findIndex((i) => i?.UniqueID === ids[0]), t = inv.inventory.findIndex((i) => i?.UniqueID === ids[1]);
      if (f < 0 || t < 0) { this.pending = null; this.invalidate(); return; }
      this.m.itemChanges.combine(f, t);
      if (!inv.isLocked(inv.inventory[f] ?? null)) { this.pending = null; this.invalidate(); } // refused locally (locked)
    }, [{ grid: "inv", index: from }, { grid: "inv", index: to }]);
    this.invalidate();
  }

  /** The server's answer to the pending combine (the models applied it already). Never consumes. */
  private answer(p: ServerPacket): void {
    const pd = this.pending;
    if (!pd) return;
    switch (p.type) {
      case "ItemUpgraded": if (p.Item.UniqueID === pd.to) pd.upgraded = true; return;
      case "ItemSlotSizeChanged": if (p.UniqueID === pd.to) pd.socket = true; return;
      case "CombineItem": {
        if (p.IDFrom !== pd.gem || p.IDTo !== pd.to) return;
        this.pending = null;
        let text: string, good = false;
        if (!p.Success) { text = "The server refused the combination (see the chat)."; this.fx = "dud"; }
        else if (p.Destroy) { text = `${pd.gemName} failed and ${pd.itemName} was destroyed.`; this.fx = "boom"; this.targetId = null; }
        else if (pd.socket) { text = `${pd.itemName} gained a socket.`; this.fx = "win"; good = true; }
        else if (pd.upgraded) { text = `${pd.itemName} was upgraded by ${pd.gemName}.`; this.fx = "win"; good = true; }
        else { text = `${pd.gemName} had no effect.`; this.fx = "dud"; }
        this.msg = [text, good];
        this.log.unshift(text);
        if (this.log.length > 8) this.log.length = 8;
        this.listKey = "";
        this.invalidate();
        return;
      }
    }
  }

  private nameOf(item: UserItem, info: ItemInfo): string {
    return itemFriendlyName(info) || info.Name || `Item #${item.ItemIndex}`;
  }
}

/** MirItemCell.UseSlotItem's shape check for a socket item: "" = it fits the open item. */
function socketFit(socket: ItemInfo, target: ItemInfo | undefined): string {
  if (!target) return "";
  if (socket.Shape === 1 && target.Type !== ItemType.Weapon) return "Weapons only";
  if (socket.Shape === 2 && target.Type !== ItemType.Armour) return "Armour only";
  if (socket.Shape === 3 && target.Type !== ItemType.Ring && target.Type !== ItemType.Bracelet && target.Type !== ItemType.Necklace) return "Rings, bracelets and necklaces only";
  return "";
}

registerWindow(SOCKET_KEY, (host) => new SocketWindow(host), { binds: [], watch: (host, open) => watchSockets(host, open) });

/** Wires the drag controller's Ctrl + right-click hook (core/drag.ts hooks.onSockets: ItemChanges.openSockets found an
 *  item with sockets) to this window. Run once per game by the registry entry's watch hook; returns its removal, which
 *  puts the previous hook back. */
export function watchSockets(host: ModernUiHost, open: (show?: boolean) => boolean): () => void {
  const drag = host.drag;
  const prev = drag.hooks.onSockets;
  const mine = (): void => {
    open(true);
    host.rt.peekWindow<SocketWindow>(SOCKET_KEY)?.showSockets();
  };
  drag.hooks = { ...drag.hooks, onSockets: mine };
  return () => { if (drag.hooks.onSockets === mine) drag.hooks = { ...drag.hooks, onSockets: prev }; };
}

/** Opens the gem bench, with `item` (a bag item) selected when given (an inventory context action may call it). */
export function showGemBench(host: ModernUiHost, item?: UserItem | null): boolean {
  if (!host.openWindow(SOCKET_KEY, true)) return false;
  host.rt.peekWindow<SocketWindow>(SOCKET_KEY)?.showGems(item);
  return true;
}
