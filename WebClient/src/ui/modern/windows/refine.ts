// Ported from play/hudRefine.ts: the new UI's refine window (the smith) over the real models. Desktop counterpart:
// RefineDialog + NPCDropDialog PanelType.Refine / CheckRefine (Client/MirScenes/Dialogs/NPCDialogs.cs) and
// GameScene.NPCRefine / NPCCheckRefine / NPCCollectRefine / RefineItem; the classic port is src/ui/npcServices.ts
// (RefineDialog, C.RefineCancel on hide) + src/ui/npcWindow.ts (the drop panel). Server: PlayerObject.RefineItem /
// CollectRefine / CheckRefine, each gated on the NPC page key.
//
// Crystal refines in three NPC pages, so the window has two modes and collecting has none:
//  - [@REFINE] (S.NPCRefine, not refining): "refine" mode = the drop panel's weapon cell (the anvil, NpcDrop.take) and
//    the 16-cell refine grid (Inventory.refine, cells "refine" through the drag routes: C.DepositRefineItem /
//    C.RetrieveRefineItem / C.MoveItem). Refine = NpcDrop.confirm (C.RefineItem; the weapon stays locked until
//    S.RepairItem, S.RefineItem takes it to the smith and closes the NPC dialog). Closing the window sends C.RefineCancel
//    (RefineDialog.Hide): the server hands the grid back with S.RetrieveRefineItem;
//  - [@REFINECOLLECT] has no window: the server answers S.NPCCollectRefine (the item comes back with S.GainedItem) and
//    closes the dialog; its "ready in N minutes" refusal is a chat line;
//  - [@REFINECHECK] (S.NPCCheckRefine): "check" mode = the drop panel's cell for a refined item, its stored chance and
//    stat; Test = NpcDrop.confirm (C.CheckRefine). S.ItemUpgraded = success (crit when it gained more than its
//    RefineAdded), S.RefineItem = smashed (the server closes the dialog: the result is a popup).
// The odds come from S.ServerRules (RefineBaseChance, RefineIncrease, RefineCritChance / CritIncrease, RefineWep /
// ItemStatReduce, RefineOreName, OnlyRefineWeapon, RefineTime) with the server's integer divisions; without the rules the
// formula is hidden. The test rolls Random(1, 100) (1-99): smashed when the roll > chance, crit when a second roll <
// RefineCritChance. HIDDEN: the live forge countdown (CollectTime is never sent; the server's chat line says when) and the
// mock's history and fake timer. Weapons put in the grid are destroyed by the server: the window warns.
// Narrow layout (a layer below 1040 px): a 4 x 4 grid and narrower columns.

import type { ItemInfo, ServerPacket, ServerRules, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { BindMode, ItemType, PanelType, Stat } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import { BELT_IDX, REFINE_SLOTS } from "../../../game/inventory.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { refineCost } from "../../../game/items.ts";
import { serverRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, h, injectCss, later, plural, progressRing, restartAnim, setStyle, setText, type Btn,
} from "../core/index.ts";
import { routeDrop, type CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const REFINE_WINDOW = "refine";

const ANVIL = "x:anvil" as const;    // the drop panel's cell (NPCDropDialog.ItemCell for Refine / CheckRefine)
const ANSWER_MS = 6000;              // a test's lock is released if the server never answers (it returns silently off-page)
/** RefinedValue (Shared/Enums.cs: None, DC, MC, SC) -> the stat it adds to. */
const REFINED = ["", "DC", "MC", "SC"] as const;
const MAX_STAT = [0, Stat.MaxDC, Stat.MaxMC, Stat.MaxSC] as const;

/** ClientTextKeys (English defaults), as the classic npcSession.ts. */
const T = {
  ItemHasntBeenRefinedNoChecking: (n: string) => `Your ${n} hasn't been refined so it doesn't need checking.`,
  YouHaventDepositedItemsToRefine: (n: string) => `You haven't deposited any items to refine your ${n} with.`,
  YouDontHaveEnoughGoldToRefine: (n: string) => `You don't have enough gold to refine your ${n}.`,
};

/** "refine" = [@REFINE] (anvil + grid), "check" = [@REFINECHECK] (the test). */
export type RefineMode = "refine" | "check";
type Outcome = "win" | "crit" | "smash";

const isMine = (type: number): boolean => type === PanelType.Refine || type === PanelType.CheckRefine;
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

// — The session (DOM-free apart from the popup it shows after a smash) —

/** The refine pages' state outside the window: the open drop panel's mode, the test waiting for its answer and the last
 *  result. One per UI runtime (refineSession), so a smash (which closes the NPC dialog and the window) still shows. */
export class RefineSession {
  /** The mode or the result changed. */
  readonly changed = new Emitter<void>();
  private _mode: RefineMode | null = null;
  private test: { id: bigint; name: string; value: number; before: number; added: number; cancel: () => void } | null = null;
  private _result: { kind: Outcome; text: string } | null = null;

  constructor(private readonly host: ModernUiHost) {
    const drop = host.ctx.models.npcSession.drop, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(drop.shown.on(({ type }) => {
      this._mode = type === PanelType.Refine ? "refine" : type === PanelType.CheckRefine ? "check" : null;
      this._result = null;
      this.changed.emit();
    }));
    on(drop.hidden.on(() => { if (this._mode) { this._mode = null; this.changed.emit(); } }));
  }

  /** The drop panel the server opened ([@REFINE] / [@REFINECHECK]), or null. */
  get mode(): RefineMode | null { return this._mode; }
  /** The last test's result while on the check page (null = none). */
  get result(): { kind: Outcome; text: string } | null { return this._result; }
  /** A test waits for the server. */
  get testing(): boolean { return this.test !== null; }

  /** Test = NpcDrop.confirm on the check page (C.CheckRefine). The cell stays locked until the answer. */
  async runTest(): Promise<void> {
    const m = this.host.ctx.models, drop = m.npcSession.drop;
    const item = drop.currentItem();
    if (this._mode !== "check" || !item || this.test) return;
    const name = this.name(item);
    if (item.RefineAdded === 0) { this.host.chat.system(T.ItemHasntBeenRefinedNoChecking(name)); return; }
    const rules = serverRules(this.host.ctx.session).rules;
    const o = testOdds(item.RefineSuccessChance, item.RefinedValue !== 0, rules?.RefineCritChance ?? null);
    const text = item.RefinedValue === 0
      ? `Test your ${name}?\nNo stat was chosen when it was refined: the test will smash it.`
      : `Test your ${name}?\n${pctText(o.smash)} it is smashed; otherwise it gains ${REFINED[item.RefinedValue]}.`;
    if (!await this.host.rt.popups.ask(text, { title: "Check refine", kind: "warning", okCancel: true })) return;
    const now = drop.currentItem();
    if (this._mode !== "check" || !now || now.UniqueID !== item.UniqueID || this.test) return;
    drop.confirm(); // C.CheckRefine; the panel's target clears (its lock is ours from here)
    m.inventory.locked.add(String(item.UniqueID));
    m.inventory.changed();
    const value = item.RefinedValue, stat = MAX_STAT[value] ?? 0;
    this.test = {
      id: item.UniqueID, name, value, before: stat ? item.AddedStats[stat] ?? 0 : 0, added: item.RefineAdded,
      cancel: later(() => this.endTest(), ANSWER_MS, this.host.rt.signal),
    };
    this._result = null;
    this.changed.emit();
  }

  /** S.ItemUpgraded (the test passed: AddedStats are new) / S.RefineItem (smashed). The models already ran. */
  handle(p: ServerPacket): void {
    const t = this.test;
    if (!t) return;
    if (p.type === "ItemUpgraded" && p.Item.UniqueID === t.id) {
      const stat = MAX_STAT[t.value] ?? 0;
      const gain = stat ? (p.Item.AddedStats[stat] ?? 0) - t.before : 0;
      const what = REFINED[t.value] || "stat";
      const crit = gain > t.added;
      this.endTest();
      this._result = { kind: crit ? "crit" : "win", text: `${crit ? "Critical! " : ""}Your ${t.name} gained +${gain} ${what}.` };
      this.changed.emit();
    } else if (p.type === "RefineItem" && p.UniqueID === t.id) {
      this.endTest();
      this._result = { kind: "smash", text: `Your ${t.name} was smashed in the test.` };
      this.changed.emit();
      // GameScene.RefineItem closed the NPC dialog (and this window): say it in a popup
      void this.host.rt.popups.confirm({ title: "Check refine", text: this._result.text, kind: "danger", buttons: "ok" });
    }
  }

  private endTest(): void {
    const t = this.test;
    if (!t) return;
    this.test = null;
    t.cancel();
    const inv = this.host.ctx.models.inventory;
    inv.locked.delete(String(t.id));
    inv.changed();
    this.changed.emit();
  }

  private name(item: UserItem): string {
    const info = this.host.ctx.models.itemInfos.get(item.ItemIndex);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }

  dispose(): void { this.endTest(); this._mode = null; }
}

/** The runtime's refine session (built on first use). */
export function refineSession(host: ModernUiHost): RefineSession {
  return host.rt.service("refineSession", () => new RefineSession(host));
}

// — The server's formulas —

/** One part of PlayerObject.RefineItem's success chance, for the explanation. */
export interface RefinePart { readonly name: string; readonly why: string; readonly value: number; readonly max: number }

/** PlayerObject.RefineItem's tally over the refine grid, and the chance it would store on the weapon (CheckRefine reads it). */
export interface RefineCalc {
  readonly dc: number; readonly mc: number; readonly sc: number;
  /** RefinedValue the server would set (0 = none: a tie or no stat, the test smashes). */
  readonly value: number;
  readonly chance: number;
  readonly parts: readonly RefinePart[];
  /** Why the chance is 0 regardless of the parts ("" = the parts apply). */
  readonly zero: string;
  /** Weapons in the grid (the server destroys them). */
  readonly weaponsLost: number;
  /** Grid items with no ItemInfo yet (the totals may be low). */
  readonly unknown: number;
}

/** RefineItem's formula with the server's types: short totals, integer divisions, the byte-cast added stats. */
export function refineCalc(weapon: UserItem, wInfo: ItemInfo | undefined, grid: readonly (UserItem | null)[],
  info: (item: UserItem) => ItemInfo | undefined, rules: ServerRules): RefineCalc {
  let dc = 0, mc = 0, sc = 0, lv = 0, items = 0, durability = 0, currentDura = 0, ores = 0, purity = 0, weaponsLost = 0, unknown = 0;
  for (const it of grid) {
    if (!it) continue;
    const ii = info(it);
    if (!ii) { unknown++; continue; }
    if (ii.Type === ItemType.Weapon) { weaponsLost++; continue; }
    const s = ii.Stats, a = it.AddedStats;
    if ((s[Stat.MaxDC] ?? 0) > 0 || (s[Stat.MaxMC] ?? 0) > 0 || (s[Stat.MaxSC] ?? 0) > 0) {
      dc += (s[Stat.MinDC] ?? 0) + (s[Stat.MaxDC] ?? 0) + (a[Stat.MaxDC] ?? 0);
      mc += (s[Stat.MinMC] ?? 0) + (s[Stat.MaxMC] ?? 0) + (a[Stat.MaxMC] ?? 0);
      sc += (s[Stat.MinSC] ?? 0) + (s[Stat.MaxSC] ?? 0) + (a[Stat.MaxSC] ?? 0);
      lv += ii.RequiredAmount;
      if (Math.floor(it.MaxDura / 1000) === Math.floor(ii.Durability / 1000)) durability++;
      if (Math.floor(it.CurrentDura / 1000) === Math.floor(it.MaxDura / 1000)) currentDura++;
      items++;
    }
    if (itemFriendlyName(ii) === rules.RefineOreName) { purity += Math.floor(it.CurrentDura / 1000); ores++; }
  }
  const value = dc > mc && dc > sc ? 1 : mc > dc && mc > sc ? 2 : sc > dc && sc > mc ? 3 : 0;
  const stat = value === 1 ? dc : value === 2 ? mc : value === 3 ? sc : 0;
  const wlv = wInfo?.RequiredAmount ?? 0;
  const zero = !dc && !mc && !sc ? "No jewellery with DC, MC or SC in the grid: the chance is 0 and the test smashes it."
    : !ores ? `No ${rules.RefineOreName} in the grid: the chance is 0.` : "";
  const itemS = clamp(stat * 5 - wlv + 5, 0, 10);
  const avgLv = items ? Math.trunc(lv / items) : 0, avgPur = ores ? Math.trunc(purity / ores) : 0, avgStat = items ? Math.trunc(stat / items) : 0;
  const lvB = items && avgLv > wlv - 5 ? 10 : 0, maxB = items && durability === items ? 10 : 0, curB = items && currentDura === items ? 5 : 0;
  const oreA = items && ores >= items ? 15 : 0, oreP = ores && items && avgPur >= avgStat ? 15 : 0, oreE = ores && purity === stat ? 5 : 0;
  const wa = weapon.AddedStats;
  const luck = clamp((wa[Stat.Luck] ?? 0) + 5, 0, 10);
  const addedRaw = ((wa[Stat.MaxDC] ?? 0) + (wa[Stat.MaxMC] ?? 0) + (wa[Stat.MaxSC] ?? 0)) & 0xff; // (byte) cast
  const reduce = wInfo?.Type === ItemType.Weapon ? rules.RefineWepStatReduce : rules.RefineItemStatReduce;
  const penalty = Math.min(50, addedRaw * reduce);
  const sName = REFINED[value] || "Stat";
  const parts: RefinePart[] = [
    { name: `${sName} strength`, why: `${sName} ${stat} × 5 − level ${wlv} + 5`, value: itemS, max: 10 },
    { name: "Jewellery level", why: `average level ${avgLv} > ${wlv - 5}`, value: lvB, max: 10 },
    { name: "Jewellery max durability", why: `${durability} / ${items} never worn down`, value: maxB, max: 10 },
    { name: "Jewellery durability", why: `${currentDura} / ${items} fully repaired`, value: curB, max: 5 },
    { name: "Ore amount", why: `${ores} ore ≥ ${items} jewellery`, value: oreA, max: 15 },
    { name: "Ore purity", why: `average ${avgPur} ≥ average ${sName} ${avgStat} (whole numbers)`, value: oreP, max: 15 },
    { name: "Exact purity", why: `purity ${purity} = ${sName} ${stat}`, value: oreE, max: 5 },
    { name: "Luck", why: `item luck ${wa[Stat.Luck] ?? 0} + 5`, value: luck, max: 10 },
    { name: "Base chance", why: "the server's base", value: rules.RefineBaseChance, max: Math.max(1, rules.RefineBaseChance) },
  ];
  if (penalty) parts.push({ name: "Already refined", why: `+${addedRaw} added × ${reduce}`, value: -penalty, max: 50 });
  const chance = zero ? 0 : itemS + lvB + maxB + curB + oreA + oreP + oreE + luck + rules.RefineBaseChance - penalty;
  return { dc, mc, sc, value, chance, parts, zero, weaponsLost, unknown };
}

/** CheckRefine's two rolls (Random(1, 100) = 1..99): passed when roll <= chance (and a stat was chosen), crit when a
 *  second roll < RefineCritChance. Fractions 0..1; crit unknown without the rules (counted as a plain win). */
export function testOdds(chance: number, hasStat: boolean, critChance: number | null): { win: number; crit: number; smash: number } {
  const pass = hasStat ? clamp(chance, 0, 99) / 99 : 0;
  const c = critChance === null ? 0 : clamp(critChance - 1, 0, 99) / 99;
  return { win: pass * (1 - c), crit: pass * c, smash: 1 - pass };
}

const pctText = (f: number): string => `${Math.round(f * 100)}%`;

// — The window —

const CSS = `
.hudm .rfwin2 { width: 940px; }
.hudm .rfwin2 .panel { height: 560px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .rfwin2 .body { flex: 1; min-height: 0; display: flex; }
.hudm .rfwin2 .slbl { display: flex; justify-content: space-between; }
.hudm .rfwin2 .slbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .rfwin2 .bag { width: 228px; flex: none; padding: 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 6px; min-height: 0; }
.hudm .rfwin2 .bgrid { display: grid; grid-template-columns: repeat(5, 38px); gap: 4px; overflow-y: auto; min-height: 0; scrollbar-width: thin; padding-right: 2px; }
.hudm .rfwin2 .bgrid .icell.dim { opacity: .25; }
.hudm .rfwin2 .hint { font-size: 10px; color: var(--dim); line-height: 1.4; }
.hudm .rfwin2 .forge { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 12px; overflow-y: auto;
  background: radial-gradient(ellipse 55% 45% at 50% 62%, rgba(255,120,40,.12), transparent 70%); }
.hudm .rfwin2 .anvil { position: relative; display: flex; flex-direction: column; align-items: center; gap: 4px; text-align: center; }
.hudm .rfwin2 .anvil .icell { border-color: var(--ac); }
.hudm .rfwin2 .anvil b { font-size: 15px; color: #fff; } .hudm .rfwin2 .anvil small { font-size: 11px; color: var(--dim); }
.hudm .rfwin2 .anvil .fx { position: absolute; top: -10px; left: 50%; width: 96px; height: 96px; margin-left: -48px; border-radius: 50%; pointer-events: none; opacity: 0; }
.hudm .rfwin2 .anvil.win .fx { background: radial-gradient(circle, var(--ok), transparent 65%); animation: hudm-rf2fx 1.2s ease-out; }
.hudm .rfwin2 .anvil.crit .fx { background: radial-gradient(circle, var(--gold), transparent 65%); animation: hudm-rf2fx 1.4s ease-out; }
@keyframes hudm-rf2fx { 25% { opacity: .9; } }
.hudm .rfwin2 .crul { align-self: stretch; }
.hudm .rfwin2 .cru { display: grid; grid-template-columns: repeat(8, 40px); gap: 4px; padding: 10px; border-radius: var(--rad); border: 1px solid rgba(255,140,40,.4);
  background: linear-gradient(180deg, rgba(255,120,40,.08), rgba(0,0,0,.3)); }
.hudm .rfwin2 .stat3 { display: flex; gap: 8px; }
.hudm .rfwin2 .stat3 div { min-width: 70px; text-align: center; padding: 5px 8px; border-radius: var(--rad); border: 1px solid var(--fr); font-size: 10px; color: var(--dim); }
.hudm .rfwin2 .stat3 b { display: block; font-size: 18px; color: #fff; }
.hudm .rfwin2 .stat3 div.win { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 16%, transparent); } .hudm .rfwin2 .stat3 div.win b { color: var(--ac); }
.hudm .rfwin2 .stat3 div.tie { border-color: var(--bad); }
.hudm .rfwin2 .warn { font-size: 11px; color: var(--bad-soft); text-align: center; white-space: pre-line; }
.hudm .rfwin2 .card { width: 100%; max-width: 360px; padding: 10px 12px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; }
.hudm .rfwin2 .card div { display: flex; justify-content: space-between; padding: 2px 0; } .hudm .rfwin2 .card b { color: #fff; }
.hudm .rfwin2 .side { width: 310px; flex: none; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 9px; overflow-y: auto;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .rfwin2 .side > * { flex-shrink: 0; }
.hudm .rfwin2 .side .ui-ring { margin: 0 auto; }
.hudm .rfwin2 .oddsbox { display: flex; flex-direction: column; gap: 4px; }
.hudm .rfwin2 .odds { display: flex; height: 12px; border-radius: 6px; overflow: hidden; border: 1px solid var(--fr); background: #05070c; }
.hudm .rfwin2 .odds i { display: block; height: 100%; width: 0; } .hudm .rfwin2 .odds .w { background: var(--ok); } .hudm .rfwin2 .odds .c { background: var(--gold); } .hudm .rfwin2 .odds .s { background: var(--bad); }
.hudm .rfwin2 .ol { display: flex; justify-content: space-between; gap: 6px; font-size: 10px; color: var(--dim); } .hudm .rfwin2 .ol b { color: #fff; }
.hudm .rfwin2 .go { height: 38px; font-size: 14px; font-weight: 800; letter-spacing: .08em; }
.hudm .rfwin2 .part { display: grid; grid-template-columns: 1fr 90px 32px; gap: 8px; align-items: center; font-size: 11px; padding: 1px 0; }
.hudm .rfwin2 .part small { display: block; font-size: 9px; color: var(--dim); }
.hudm .rfwin2 .part .bar { height: 6px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .rfwin2 .part .bar i { display: block; height: 100%; background: var(--ac); }
.hudm .rfwin2 .part.neg .bar i { background: var(--bad); } .hudm .rfwin2 .part b { text-align: right; font-variant-numeric: tabular-nums; color: #fff; } .hudm .rfwin2 .part.neg b { color: var(--bad-soft); }
.hudm .rfwin2 .res { text-align: center; font-size: 13px; font-weight: 800; padding: 8px; border-radius: var(--rad); }
.hudm .rfwin2 .res.win { color: var(--ok); background: color-mix(in srgb, var(--ok) 10%, transparent); }
.hudm .rfwin2 .res.crit { color: var(--gold); background: color-mix(in srgb, var(--gold) 12%, transparent); }
.hudm .rfwin2 .res.smash { color: var(--bad-soft); background: color-mix(in srgb, var(--bad) 12%, transparent); }
.hudm .rfwin2 .note { font-size: 11px; color: var(--dim); text-align: center; white-space: pre-line; }
/* Narrow layer */
.hudm.narrow .rfwin2 { width: 740px; }
.hudm.narrow .rfwin2 .panel { height: 520px; }
.hudm.narrow .rfwin2 .bag { width: 190px; } .hudm.narrow .rfwin2 .bgrid { grid-template-columns: repeat(4, 38px); }
.hudm.narrow .rfwin2 .cru { grid-template-columns: repeat(4, 40px); }
.hudm.narrow .rfwin2 .side { width: 270px; padding: 10px; }
.hudm.narrow .rfwin2 .part { grid-template-columns: 1fr 60px 30px; }
`;

class RefineWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  /** The mode the window was opened in (kept after the panel closed, for closed()). */
  private mode: RefineMode = "refine";
  /** The weapon sent with C.RefineItem (locked until S.RepairItem / S.RefineItem). */
  private sentId: bigint | null = null;
  /** The bag cell pressed in the window's bag column (a click on it places it); cleared by any other press. */
  private sidePress = "";
  private shownResult: object | null = null;

  // — DOM, built once —
  private readonly bagGrid = h("div", "bgrid");
  private readonly bagHint = h("div", "hint");
  private readonly anvil = h("div", "anvil");
  private readonly anvilName = h("b");
  private readonly anvilSub = h("small");
  private readonly removeBtn: Btn;
  private readonly cruLbl = h("div", "lbl slbl crul", "Refine grid");
  private readonly cruCount = h("b");
  private readonly cru = h("div", "cru");
  private readonly stat3 = h("div", "stat3");
  private readonly statBoxes: { el: HTMLElement; v: HTMLElement }[] = [];
  private readonly warn = h("div", "warn");
  private readonly card = h("div", "card");
  private readonly ring = progressRing(110, 10);
  private readonly ringNote = h("div", "note");
  private readonly odds = h("div", "odds");
  private readonly oddsW = h("i", "w");
  private readonly oddsC = h("i", "c");
  private readonly oddsS = h("i", "s");
  private readonly olW = h("span");
  private readonly olC = h("span");
  private readonly olS = h("span");
  private readonly oddsBox = h("div", "oddsbox");
  private readonly goBtn: Btn;
  private readonly cost = h("div", "note");
  private readonly res = h("div", "res");
  private readonly partsLbl = h("div", "lbl slbl", "How it adds up", h("b", "", "server formula"));
  private readonly partsBox = h("div");
  private readonly steps = h("div", "note");
  private partsKey = "";
  private bagCells: ItemCell[] = [];
  private readonly gridCells: ItemCell[] = [];

  constructor(private readonly ui: ModernUiHost, private readonly session: RefineSession) {
    super(ui.rt, "rfwin2", 40, 30);
    injectCss("win-refine", CSS);
    this.m = ui.ctx.models;
    const m = this.m, sig = this.signal, drop = m.npcSession.drop;

    // The anvil: the drop panel's cell as a custom drop target (NpcDrop.take locks the bag item)
    const offTarget = ui.cells.addTarget(ANVIL, {
      item: () => this.session.mode ? drop.currentItem() : null,
      accepts: (from, item) => from.grid === "inv" && this.session.mode ? this.refusal(item) : null,
      verb: () => this.session.mode === "check" ? "Check" : "Refine",
      drop: (from) => drop.take(from.index),
      use: () => drop.take(-1),
    });
    sig.addEventListener("abort", offTarget, { once: true });
    const anvilCell = ui.cells.cell(ANVIL, 0, { size: 64, signal: sig });
    this.removeBtn = button("Take it back", () => drop.take(-1), { icon: "back", title: "Clear the cell (right-click works too)" });
    this.anvil.append(h("div", "fx"), anvilCell.el, this.anvilName, this.anvilSub, this.removeBtn.el);

    // The refine grid (Inventory.refine): drag in / out, double-click takes an item back
    for (let i = 0; i < REFINE_SLOTS; i++) {
      const c = ui.cells.cell("refine", i, { size: 40, signal: sig });
      this.gridCells.push(c);
      this.cru.append(c.el);
    }
    this.cru.addEventListener("dblclick", (e) => { const c = ui.cells.cellOf(e.target); if (c) this.takeBack(c.ref.index); }, { signal: sig });
    this.cruLbl.append(this.cruCount);
    for (const k of ["DC", "MC", "SC"]) {
      const v = h("b"), el = h("div", "", v, `${k} from jewellery`);
      this.statBoxes.push({ el, v });
      this.stat3.append(el);
    }

    // Bag column: a click on a bag item places it (weapons on the anvil, the rest in the grid; on the check page, the anvil)
    ui.rt.root.addEventListener("pointerdown", () => { this.sidePress = ""; }, { capture: true, signal: sig });
    this.bagGrid.addEventListener("pointerdown", (e) => {
      const c = e.button === 0 ? ui.cells.cellOf(e.target) : undefined;
      this.sidePress = c ? `${c.ref.grid}:${c.ref.index}` : "";
    }, { capture: true, signal: sig });
    const offClick = ui.drag.addClickHook((ref) => {
      if (!this.open || !this.session.mode || this.sidePress !== `${ref.grid}:${ref.index}`) return false;
      this.sidePress = "";
      this.placeFromBag(ref.index);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    // Side: chance ring, odds, the button, the explanation
    this.odds.append(this.oddsW, this.oddsC, this.oddsS);
    this.oddsBox.append(h("div", "lbl", "Outcome of the test"), this.odds, h("div", "ol", this.olW, this.olC, this.olS));
    this.goBtn = button("REFINE", () => void this.go(), { cls: "main big go", icon: "craft" });

    const bag = h("div", "bag", h("div", "lbl slbl", "Your bag", h("b", "", "click to add")), this.bagGrid, this.bagHint);
    const forge = h("div", "forge", this.anvil, this.cruLbl, this.cru, this.stat3, this.card, this.warn);
    const side = h("div", "side", this.ring.el, this.ringNote, this.oddsBox, this.goBtn.el, this.cost, this.res, this.partsLbl, this.partsBox, this.steps);
    this.el.append(h("div", "panel", this.titleBar("REFINE", "Close"), h("div", "body", bag, forge, side)));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(m.inventory.subscribe(() => this.invalidate()));
    on(drop.changed.on(() => this.invalidate()));
    on(this.session.changed.on(() => {
      if (!this.session.mode && this.open) { this.toggle(false); return; }
      this.invalidate();
    }));
    on(serverRules(ui.ctx.session).changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.partsKey = ""; this.invalidate(); } }));
  }

  protected override shown(): void {
    this.mode = this.session.mode ?? "refine";
    this.sentId = null;
    this.partsKey = "";
  }

  protected override closed(): void {
    const drop = this.m.npcSession.drop;
    if (drop.open && isMine(drop.type)) drop.hide(); // NPCDropDialog.Hide (the target unlocks)
    if (this.mode === "refine") this.ui.ctx.send(ClientPackets.RefineCancel()); // RefineDialog.Hide: the grid comes back
    const pick = this.m.itemPick;
    if (pick.cell?.grid === "refine") pick.clear();
  }

  // — Helpers —

  private info(item: UserItem): ItemInfo | undefined { return this.m.itemInfos.get(item.ItemIndex); }
  private name(item: UserItem): string {
    const info = this.info(item);
    return info ? itemFriendlyName(info) || info.Name : `Item #${item.ItemIndex}`;
  }
  private rules(): ServerRules | null { return serverRules(this.ui.ctx.session).rules; }

  /** Why the anvil refuses `item` ("" = it takes it): RefineItem's / CheckRefine's own refusals, before the request. */
  private refusal(item: UserItem): string {
    const info = this.info(item);
    if (this.session.mode === "check") return item.RefineAdded === 0 ? "Not refined: nothing to check" : "";
    if (item.RefineAdded !== 0) return "Have it checked first";
    if (!info) return "";
    if (info.Durability === 0) return "Can't be refined";
    if ((this.rules()?.OnlyRefineWeapon ?? true) && info.Type !== ItemType.Weapon) return "Only weapons can be refined";
    if (info.Bind & BindMode.DontUpgrade) return "Can't be refined";
    if (item.Rental && (item.Rental.BindingFlags & BindMode.DontUpgrade)) return "Can't be refined";
    return "";
  }

  /** Whether a bag item has any use here (the bag column dims the rest). */
  private useful(item: UserItem): boolean {
    if (this.session.mode === "check") return item.RefineAdded !== 0;
    const info = this.info(item);
    if (!info) return true;
    if (info.Type === ItemType.Weapon) return !this.refusal(item);
    const s = info.Stats, ore = this.rules()?.RefineOreName;
    return (s[Stat.MaxDC] ?? 0) > 0 || (s[Stat.MaxMC] ?? 0) > 0 || (s[Stat.MaxSC] ?? 0) > 0 || (!!ore && itemFriendlyName(info) === ore);
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const mode = this.session.mode ?? this.mode;
    this.mode = mode;
    const drop = this.m.npcSession.drop, inv = this.m.inventory;
    const item = this.session.mode ? drop.currentItem() : null;
    const info = item ? this.info(item) : undefined;
    const rules = this.rules();
    const check = mode === "check";

    this.renderBag();
    setText(this.bagHint, check ? "Click a refined item (or drag it) to put it on the anvil for the test."
      : "Click a weapon to put it on the anvil; jewellery with DC / MC / SC and ore go in the grid. Double-click a grid item to take it back.");

    // Anvil
    const sending = this.sentId !== null && inv.inventory.some((i) => i?.UniqueID === this.sentId && inv.isLocked(i));
    if (!sending) this.sentId = null;
    setText(this.anvilName, item ? this.name(item) : check ? "Put a refined item here" : "Put a weapon on the anvil");
    const wa = item?.AddedStats ?? {};
    const added = item ? ["DC", "MC", "SC"].map((k, j) => [k, wa[MAX_STAT[j + 1]!] ?? 0] as const).filter(([, v]) => v > 0).map(([k, v]) => `+${v} ${k}`).join(" ") : "";
    setText(this.anvilSub, item && info ? `Level ${info.RequiredAmount}${added ? ` · refined ${added}` : ""}${wa[Stat.Luck] ? ` · luck +${wa[Stat.Luck]}` : ""}`
      : check ? "The smith tests a refine you collected" : (rules?.OnlyRefineWeapon ?? true) ? "Only weapons can be refined" : "Weapons and gear can be refined");
    this.removeBtn.el.hidden = !item;

    // The grid (refine page only)
    this.cruLbl.hidden = this.cru.hidden = this.stat3.hidden = check;
    this.card.hidden = !check;
    let used = 0;
    for (const it of inv.refine) if (it) used++;
    setText(this.cruCount, `${used} / ${REFINE_SLOTS}`);

    const calc = !check && item && rules ? refineCalc(item, info, inv.refine, (i) => this.info(i), rules) : null;
    if (!check) {
      const vals = calc ? [calc.dc, calc.mc, calc.sc] : this.totals();
      const top = Math.max(...vals);
      this.statBoxes.forEach((b, j) => {
        setText(b.v, String(vals[j]));
        b.el.classList.toggle("win", !!calc && calc.value === j + 1);
        b.el.classList.toggle("tie", (!calc || calc.value === 0) && top > 0 && vals[j] === top && vals.filter((v) => v === top).length > 1);
      });
    } else this.renderCard(item, rules);

    // Warnings
    const warns: string[] = [];
    if (!check) {
      if (item && this.refusal(item)) warns.push(this.refusal(item));
      if (calc?.weaponsLost) warns.push(`The smith destroys weapons put in the grid (${calc.weaponsLost} here).`);
      if (calc && !calc.zero && calc.value === 0) warns.push("DC, MC and SC are tied: nothing will be refined and the test will smash it.");
      if (calc?.zero) warns.push(calc.zero);
      if (calc?.unknown) warns.push("Some items' details haven't arrived yet: the totals may change.");
    }
    setText(this.warn, warns.join("\n"));

    this.renderSide(mode, item, info, calc, rules, sending);
  }

  /** DC / MC / SC of the grid before a weapon is on the anvil (the same tally as refineCalc). */
  private totals(): [number, number, number] {
    let dc = 0, mc = 0, sc = 0;
    for (const it of this.m.inventory.refine) {
      const ii = it ? this.info(it) : undefined;
      if (!it || !ii || ii.Type === ItemType.Weapon) continue;
      const s = ii.Stats, a = it.AddedStats;
      if ((s[Stat.MaxDC] ?? 0) <= 0 && (s[Stat.MaxMC] ?? 0) <= 0 && (s[Stat.MaxSC] ?? 0) <= 0) continue;
      dc += (s[Stat.MinDC] ?? 0) + (s[Stat.MaxDC] ?? 0) + (a[Stat.MaxDC] ?? 0);
      mc += (s[Stat.MinMC] ?? 0) + (s[Stat.MaxMC] ?? 0) + (a[Stat.MaxMC] ?? 0);
      sc += (s[Stat.MinSC] ?? 0) + (s[Stat.MaxSC] ?? 0) + (a[Stat.MaxSC] ?? 0);
    }
    return [dc, mc, sc];
  }

  /** The check page: what the refine stored on the item (RefinedValue, RefineAdded, RefineSuccessChance). */
  private renderCard(item: UserItem | null, rules: ServerRules | null): void {
    if (!item) { this.card.replaceChildren(h("div", "", h("span", "", "No item on the anvil."))); return; }
    const stat = REFINED[item.RefinedValue] ?? "";
    const rows: [string, string][] = [
      ["Refined stat", stat || "none (the test smashes it)"],
      ["Bonus if it passes", item.RefineAdded ? `+${item.RefineAdded} ${stat || ""}`.trim() : "—"],
      ["Crit bonus", rules && item.RefineAdded ? `+${item.RefineAdded * rules.RefineCritIncrease} ${stat}`.trim() : "—"],
      ["Stored chance", `${item.RefineSuccessChance}%`],
    ];
    this.card.replaceChildren(...rows.map(([k, v]) => h("div", "", h("span", "", k), h("b", "", v))));
  }

  private renderSide(mode: RefineMode, item: UserItem | null, info: ItemInfo | undefined, calc: RefineCalc | null, rules: ServerRules | null, sending: boolean): void {
    const check = mode === "check";
    const inv = this.m.inventory;
    // Chance + odds: the check page reads the item; the refine page the formula (needs the rules and a weapon)
    let chance: number | null = null, hasStat = false;
    if (check && item) { chance = item.RefineSuccessChance; hasStat = item.RefinedValue !== 0; }
    else if (calc) { chance = calc.chance; hasStat = calc.value !== 0; }
    const known = chance !== null;
    this.ring.set(known ? clamp(chance!, 0, 100) / 100 : 0, known ? `${Math.max(0, chance!)}%` : "—", "SUCCESS CHANCE");
    setText(this.ringNote, !known ? (check ? "Put a refined item on the anvil." : rules ? "Put a weapon on the anvil to see its chance." : "The server's refine rules haven't arrived.")
      : check ? "Stored on the item when it was refined." : "What the refine will store on it; the test rolls against it.");
    this.oddsBox.hidden = !known;
    if (known) {
      const o = testOdds(chance!, hasStat, rules?.RefineCritChance ?? null);
      setStyle(this.oddsW, "width", `${(o.win * 100).toFixed(1)}%`);
      setStyle(this.oddsC, "width", `${(o.crit * 100).toFixed(1)}%`);
      setStyle(this.oddsS, "width", `${(o.smash * 100).toFixed(1)}%`);
      const inc = check ? item!.RefineAdded : rules?.RefineIncrease ?? 0;
      const stat = (check ? REFINED[item!.RefinedValue] : REFINED[calc?.value ?? 0]) || "stat";
      this.olW.replaceChildren(h("b", "", pctText(o.win)), ` +${inc} ${stat}`);
      this.olC.replaceChildren(h("b", "", rules ? pctText(o.crit) : "?"), ` crit +${rules ? inc * rules.RefineCritIncrease : "?"}`);
      this.olS.replaceChildren(h("b", "", pctText(o.smash)), " smashed");
    }

    // The button
    if (check) {
      const testing = this.session.testing;
      this.goBtn.setLabel(testing ? "TESTING…" : "TEST");
      this.goBtn.setDisabled(testing || !item || item.RefineAdded === 0,
        testing ? "Waiting for the smith" : !item ? "Put a refined item on the anvil" : "This item hasn't been refined");
      setText(this.cost, "Testing is free. A failed test destroys the item.");
    } else {
      const cost = item ? Math.trunc(refineCost(info, this.m.npcSession.drop.rate)) : 0;
      const empty = !inv.refine.some((i) => i);
      const why = sending ? "Waiting for the smith" : !item ? "Put a weapon on the anvil" : this.refusal(item)
        || (empty ? T.YouHaventDepositedItemsToRefine(this.name(item)) : inv.gold < cost ? T.YouDontHaveEnoughGoldToRefine(this.name(item)) : "");
      this.goBtn.setLabel(sending ? "REFINING…" : item ? `REFINE · ${fmt(cost)} GOLD` : "REFINE");
      this.goBtn.setDisabled(!!why, why);
      const time = rules?.RefineTime;
      setText(this.cost, item ? `Cost = level × 10 × ${fmt(this.m.npcSession.drop.rate)} gold. Everything in the grid is used up.`
        + (time === undefined ? "" : time > 0 ? ` The smith keeps it for ${time} ${plural(time, "minute")}.` : " It is ready at once.") : "");
    }

    // The last test's result (check page)
    const r = check ? this.session.result : null;
    this.res.hidden = !r;
    if (r) {
      this.res.className = "res " + r.kind;
      setText(this.res, r.text);
      if (r !== this.shownResult) { this.shownResult = r; if (r.kind !== "smash") restartAnim(this.anvil, r.kind); }
    }

    // How it adds up (refine page with the rules)
    const parts = calc && !calc.zero ? calc.parts : null;
    this.partsLbl.hidden = this.partsBox.hidden = !parts;
    if (parts) {
      const key = parts.map((p) => `${p.name}|${p.why}|${p.value}`).join(",");
      if (key !== this.partsKey) {
        this.partsKey = key;
        this.partsBox.replaceChildren(...parts.map((p) => {
          const fill = h("i");
          setStyle(fill, "width", `${Math.min(100, Math.abs(p.value) / p.max * 100).toFixed(0)}%`);
          return h("div", "part" + (p.value < 0 ? " neg" : ""), h("span", "", p.name, h("small", "", p.why)), h("div", "bar", fill), h("b", "", (p.value > 0 ? "+" : "") + p.value));
        }));
      }
    }
    setText(this.steps, check ? "The test rolls 1-99: at or under the chance it passes."
      : "Three visits: refine here, come back to collect it when the smith says it's ready, then ask for the check (the test).");
  }

  /** The bag column (Inventory.inventory[BELT_IDX..]); cells rebuilt only when the bag size changed. */
  private renderBag(): void {
    const bag = this.m.inventory.inventory;
    const n = Math.max(0, bag.length - BELT_IDX);
    if (this.bagCells.length !== n) {
      for (const c of this.bagCells) c.dispose();
      this.bagCells = [];
      for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 38, signal: this.signal }));
      this.bagGrid.replaceChildren(...this.bagCells.map((c) => c.el));
    }
    for (const c of this.bagCells) {
      const it = c.item;
      c.el.classList.toggle("dim", !!it && !this.useful(it));
    }
  }

  // — Actions (through the routes / models: nothing moves until the server answers) —

  /** A click on a bag item: the anvil (weapons, or anything on the check page), else the grid (a matching stack, else
   *  the first free cell). */
  private placeFromBag(bagIndex: number): void {
    const item = this.m.inventory.inventory[bagIndex];
    if (!item) return;
    const info = this.info(item);
    const from: CellRef = { grid: "inv", index: bagIndex };
    if (this.session.mode === "check" || info?.Type === ItemType.Weapon) {
      const v = routeDrop(this.ui.cells.deps, from, { grid: ANVIL, index: 0 });
      if (v.ok && v.run) v.run(); // NpcDrop.take: a local lock, no packet
      else if (v.label) this.ui.drag.toast(v.label);
      return;
    }
    const grid = this.m.inventory.refine;
    let to = -1;
    for (let i = 0; i < grid.length && to < 0; i++) {
      const g = grid[i];
      if (g && g.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && g.Count < info.StackSize) to = i;
    }
    for (let i = 0; i < grid.length && to < 0; i++) if (!grid[i]) to = i;
    if (to < 0) { this.ui.drag.toast("The refine grid is full"); return; }
    const target: CellRef = { grid: "refine", index: to };
    const v = routeDrop(this.ui.cells.deps, from, target);
    if (!v.ok || !v.run) { if (v.label) this.ui.drag.toast(v.label); return; }
    this.ui.drag.request(v.run, [from, target]);
  }

  /** A double-click on a grid item: back to a matching bag stack, else the first free bag cell. */
  private takeBack(index: number): void {
    const inv = this.m.inventory, item = inv.refine[index];
    if (!item) return;
    const info = this.info(item);
    let to = -1;
    for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) {
      const b = inv.inventory[i];
      if (b && b.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && b.Count < info.StackSize) to = i;
    }
    for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) if (!inv.inventory[i]) to = i;
    if (to < 0) { this.ui.drag.toast("Your bag is full"); return; }
    const from: CellRef = { grid: "refine", index }, target: CellRef = { grid: "inv", index: to };
    const v = routeDrop(this.ui.cells.deps, from, target);
    if (!v.ok || !v.run) { if (v.label) this.ui.drag.toast(v.label); return; }
    this.ui.drag.request(v.run, [from, target]);
  }

  /** Refine (C.RefineItem after a question) or Test (RefineSession.runTest). */
  private async go(): Promise<void> {
    if (this.mode === "check") { await this.session.runTest(); return; }
    const drop = this.m.npcSession.drop, item = drop.currentItem();
    if (!item || this.sentId !== null) return;
    const info = this.info(item), name = this.name(item);
    const cost = Math.trunc(refineCost(info, drop.rate));
    const time = this.rules()?.RefineTime;
    const ok = await this.ui.rt.popups.ask(
      `Refine your ${name}?\nEverything in the refine grid is used up.${time ? ` The smith keeps it for ${time} ${plural(time, "minute")}; then collect it and have it checked.` : ""}`,
      { title: "Refine", kind: "question", okCancel: true, gold: cost, item: { name, img: info?.Image ?? -1 } });
    const now = drop.currentItem();
    if (!ok || !this.open || this.session.mode !== "refine" || !now || now.UniqueID !== item.UniqueID) return;
    this.sentId = item.UniqueID;
    drop.confirm(); // C.RefineItem (or its own refusal line); the weapon stays locked until S.RepairItem
    this.invalidate();
  }
}

registerWindow(REFINE_WINDOW, (host) => new RefineWindow(host, refineSession(host)), {
  binds: [],
  // GameScene.NPCRefine / NPCCheckRefine show the drop panel (NpcSession, only while the NPC dialog is open)
  watch: (host, open) => {
    const s = refineSession(host);
    const npc = host.ctx.models.npcSession, drop = npc.drop;
    const close = () => { if (host.rt.isOpen(REFINE_WINDOW)) open(false); };
    const offs = [
      drop.shown.on(({ type }) => { if (isMine(type)) open(true); else close(); }),
      // GameScene.NPCResponse / NPCDialog.Hide hide the RefineDialog (closed() sends C.RefineCancel, as RefineDialog.Hide)
      drop.hidden.on(close),
      npc.closed.on(close),
      host.packets.on((p) => s.handle(p)),
    ];
    return () => { for (const off of offs) off(); };
  },
});
