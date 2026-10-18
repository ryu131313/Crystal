// Ported from play/hudFishing.ts (FishingMock): the new UI's fishing window, wired to the real game. One window for the
// desktop's FishingDialog (the rod and its parts) and FishingStatusDialog (chance / progress bars, the fish button, auto
// cast, "Esc exit"); src/ui/fishingWindow.ts is the classic reference. Left: the rod (the Weapon equipment cell) and its
// five parts (Hook / Float / Bait / Finder / Reel), the catch chance. Right: the water scene (the float out while the
// user fishes, bobbing while the progress fills, dipping on a bite), the status line, the progress bar, Cast / Reel in,
// the auto-cast and Esc-exit switches.
// Changes from the mock:
//  - every state comes from the Fishing model (src/game/fishing.ts, S.FishingUpdate: Fishing, ChancePercent,
//    ProgressPercent, FoundFish): no timers, no rolls, no simulated bite; the scene follows `updated`;
//  - the parts are core/itemCells.ts cells on the "fishing" grid (index = FishingSlot): pick / drag / drop / right-click
//    through core/drag.ts + dragRoutes.ts (C.EquipSlotItem / C.RemoveSlotItem / C.MergeItem via ItemChanges, the cell
//    locked until the server answers); the bait count is the bait cell's stack count;
//  - a click on a part row opens a picker of the bag items of that type (usable first, best marked; CanWearItem gives
//    the refusal): Fit = ItemChanges.useSlotItem; Swap = removeSlotItem, then useSlotItem once the slot is free (the
//    server fits nothing over a part); same-kind bait tops the stack up. Part stats read in fishing terms (partStats,
//    as PlayerObject.FishingCast uses them), durability as a bar; a part at 0 breaks on its next use (DamagedFishingItem);
//  - Cast = what the classic map click does (game.ts checkInput: GameScene.CanFish, then User.FishingTime and
//    C.FishingCast CastOut = true), facing the user's own direction; the button (and a click on the water) works only
//    while canFish holds: holding a rod, standing, facing a fishing cell 3 cells ahead, past the cast delay;
//  - Reel in = Fishing.reelIn (C.FishingCast CastOut = false: FishButton while a fish bites, FishingStatusDialog.Cancel
//    while waiting); auto cast = Fishing.toggleAutoCast (C.FishingChangeAutocast, needs a Reel in the rod); Esc exit =
//    Fishing.toggleEscExit: closing the window (Esc, Shift+Esc, the cross) reels in while it is ticked (the classic
//    Closeall + bEscExit; here the window has to be open, the front one for Esc);
//  - opens only while holding a rod (FishingDialog.Show / ClientTextKeys.NoFishingRod note) and closes when the rod
//    goes; it opens by itself when a cast starts or a fish bites (the desktop shows FishingStatusDialog while
//    S.FishingUpdate says the user is fishing), from the registry watch hook;
//  - the bite is shown without a countdown (the server sends no bite deadline);
//  - each cast's result is worked out client-side (track(): caught / got away / bag full / missed bite / empty line)
//    and shown as a card on the water, with a per-session tally (casts, caught, got away; not saved).
// Hidden (no system in Crystal): the catch card's size, rarity and "new record", the fish journal, fish shadows for
// the Finder, the reaction timing ring, the mock's chance formula (the server's ChancePercent is shown).
// Hotkeys: the registry default (MENU_WINDOWS: KeybindOptions.Fishing, N) through the input router.

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { EquipmentSlot, FishingSlot, ItemType, MirGridType, Stat } from "../../../protocol/enums.generated.ts";
import { fishingRod } from "../../../game/fishing.ts";
import { canFish } from "../../../game/riding.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { UiWindow } from "../core/wm.ts";
import { asButton, h, injectCss, setStyle, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { fmt } from "../core/format.ts";
import { button, toggleSwitch, type Btn } from "../core/widgets.ts";
import { gradeVar } from "../core/styles.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import { FishingScene } from "./fishingScene.ts";

export const FISHING_KEY = "fishing";

/** [FishingSlot, the ItemType that fits it, name, plural, what it is for (Crystal's fishing rules), empty-slot icon]. */
type PartDef = readonly [number, number, string, string, string, IconName | string];
const PARTS: readonly PartDef[] = [
  [FishingSlot.Hook, ItemType.Hook, "Hook", "hooks", "Needed to cast · flexibility helps reel in late bites", "M14 3v10a4 4 0 01-8 0v-2l-2 2"],
  [FishingSlot.Float, ItemType.Float, "Float", "floats", "Raises the bite chance", "M12 3v4M12 7a4 4 0 110 8 4 4 0 010-8zM12 15v6"],
  [FishingSlot.Bait, ItemType.Bait, "Bait", "bait", "Needed to cast · one is used per cast", "M4 12c3-5 11-5 14 0-3 5-11 5-14 0zM18 12l3-3v6z"],
  [FishingSlot.Finder, ItemType.Finder, "Finder", "finders", "Raises the catch chance after a miss", "M11 4a7 7 0 110 14 7 7 0 010-14zM16 16l5 5"],
  [FishingSlot.Reel, ItemType.Reel, "Reel", "reels", "Lets you auto cast · may reel in for you", "M12 5a7 7 0 110 14 7 7 0 010-14zM12 9a3 3 0 110 6 3 3 0 010-6zM19 12h3"],
];

/** A part's stats in fishing terms: how PlayerObject.FishingCast reads them (the desktop label's fishing wording:
 *  Hook CriticalRate = flexibility, Float AC = nibble chance, Finder AC = increase after a miss, other parts MaxAC =
 *  success chance, MaxMAC = auto reel chance). `score` ranks parts of one type (the picker's "Best"). */
function partStats(item: UserItem, info: ItemInfo): { text: string; score: number } {
  const st = (k: number) => info.Stats?.[k] ?? 0, add = (k: number) => item.AddedStats?.[k] ?? 0;
  const out: string[] = [];
  let score = 0;
  const flex = st(Stat.CriticalRate) + add(Stat.CriticalRate);
  if (flex) { out.push(`Flexibility +${flex}`); score += flex; }
  const minAc = st(Stat.MinAC), maxAc = st(Stat.MaxAC) + add(Stat.MaxAC);
  if (minAc || maxAc) {
    if (info.Type === ItemType.Float) out.push(`Bite chance +${minAc}~${maxAc}%`);
    else if (info.Type === ItemType.Finder) out.push(`After a miss +${minAc}~${maxAc}%`);
    else out.push(`Catch chance +${maxAc}%`);
    score += info.Type === ItemType.Float || info.Type === ItemType.Finder ? (minAc + maxAc) / 2 : maxAc;
  }
  const reel = st(Stat.MaxMAC) + add(Stat.MaxMAC);
  if (reel) { out.push(`Auto reel +${reel}%`); score += reel; }
  return { text: out.join(" · "), score };
}

/** Durability in whole points like the item label (CurrentDura / 1000), the ratio for the bar, or null (stacks, none). */
function partDura(item: UserItem, info: ItemInfo): { cur: number; max: number; ratio: number } | null {
  if (info.StackSize > 1 || item.MaxDura <= 0) return null;
  return { cur: Math.floor(item.CurrentDura / 1000), max: Math.floor(item.MaxDura / 1000), ratio: Math.max(0, Math.min(1, item.CurrentDura / item.MaxDura)) };
}

/** A bag item that fits the open picker's slot. */
interface Candidate { readonly idx: number; readonly item: UserItem; readonly info: ItemInfo; readonly why: string; readonly score: number }

const CSS = `
.hudm .fswin { --winh: 470px; width: 660px; }
.hudm .fswin .panel { height: var(--winh); display: flex; flex-direction: column; box-sizing: border-box; }
.hudm .fswin .fbody { flex: 1; min-height: 0; display: flex; }
.hudm .fswin .lbl { display: flex; justify-content: space-between; margin-bottom: 6px; }
.hudm .fswin .lbl b { letter-spacing: 0; color: var(--tx); text-transform: none; font-weight: 600; }
/* Rod */
.hudm .fswin .rod { width: 230px; flex: none; box-sizing: border-box; padding: 12px; display: flex; flex-direction: column; gap: 6px;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .fswin .rodcard { display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: var(--rad); background: color-mix(in srgb, var(--ac) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--ac) 45%, transparent); }
.hudm .fswin .rodcard .t { min-width: 0; } .hudm .fswin .rodcard b { display: block; color: var(--gc, #fff); font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .fswin .rodcard small { color: var(--dim); font-size: 10px; }
.hudm .fswin .part { display: flex; align-items: center; gap: 8px; padding: 4px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .fswin .part .icell .ic { width: 16px; height: 16px; }
.hudm .fswin .part .t { flex: 1; min-width: 0; font-size: 12px; }
.hudm .fswin .part .t b { display: block; color: var(--gc, var(--tx)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .fswin .part .t small { display: block; color: var(--dim); font-size: 10px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .fswin .part.empty .t b { color: var(--dim); font-style: italic; }
.hudm .fswin .part { cursor: pointer; border: 1px solid transparent; }
.hudm .fswin .part:hover, .hudm .fswin .part.open { border-color: color-mix(in srgb, var(--ac) 55%, transparent); }
.hudm .fswin .part.spare .t small { color: var(--ac); }
.hudm .fswin .part.worn .t small { color: var(--bad); }
.hudm .fswin .part .dura { height: 3px; margin-top: 3px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .fswin .part .dura i { display: block; height: 100%; background: #4fe07a; }
.hudm .fswin .part .dura.low i { background: var(--warn); } .hudm .fswin .part .dura.out i { background: var(--bad); }
/* Part picker (over the water scene, next to its row) */
.hudm .fswin .pick { position: absolute; z-index: 6; width: 290px; max-height: 330px; display: flex; flex-direction: column; border-radius: var(--rad); overflow: hidden;
  background: color-mix(in srgb, var(--pb) 97%, #000); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 10px 24px rgba(0,0,0,.7); animation: hudm-popin .12s ease-out; }
.hudm .fswin .pick .ph { padding: 7px 10px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .fswin .pick .ph b { display: block; color: #fff; font-size: 12px; } .hudm .fswin .pick .ph small { color: var(--dim); font-size: 10px; }
.hudm .fswin .pick .pl { overflow-y: auto; padding: 4px; display: flex; flex-direction: column; gap: 3px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .fswin .pick .pr { display: flex; align-items: center; gap: 8px; width: 100%; padding: 5px 6px; border-radius: var(--rad); cursor: pointer; text-align: left;
  font: inherit; font-size: 11px; color: var(--tx); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid transparent; }
.hudm .fswin .pick .pr:hover:not(:disabled) { border-color: var(--ac); }
.hudm .fswin .pick .pr:disabled { cursor: not-allowed; opacity: .5; }
.hudm .fswin .pick .pr .pi { width: 34px; height: 34px; flex: none; border-radius: 4px; border: 1px solid var(--fr); background: var(--pb) center / auto no-repeat; image-rendering: pixelated; }
.hudm .fswin .pick .pr .t { flex: 1; min-width: 0; }
.hudm .fswin .pick .pr .t b { display: block; color: var(--gc, var(--tx)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .fswin .pick .pr .t small { display: block; color: var(--dim); font-size: 10px; overflow-wrap: anywhere; }
.hudm .fswin .pick .pr .t em { font-style: normal; color: var(--bad); }
.hudm .fswin .pick .pr .best { margin-left: 5px; padding: 0 4px; border-radius: 3px; font-size: 9px; font-weight: 700; color: #000; background: var(--gold); vertical-align: 1px; }
.hudm .fswin .pick .pr .act { flex: none; font-size: 10px; font-weight: 700; color: var(--ac); text-transform: uppercase; letter-spacing: .05em; }
.hudm .fswin .pick .pr.off .act { color: var(--dim); }
.hudm .fswin .pick .pe { padding: 14px 10px; text-align: center; color: var(--dim); font-size: 11px; }
.hudm .fswin .chance { margin-top: auto; padding-top: 6px; }
.hudm .fswin .cbar { height: 8px; border-radius: 4px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .fswin .cbar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, #2f9e5a, #4fe07a); }
.hudm .fswin .hint { font-size: 10px; color: var(--dim); }
/* Water scene: a canvas (fishingScene.ts) under the HUD overlay */
.hudm .fswin .scene { position: relative; flex: 1; min-width: 0; overflow: hidden; border-radius: 0 0 var(--rad) 0; background: #040c1a; }
.hudm .fswin .water { position: absolute; inset: 0; }
.hudm .fswin .water canvas { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
.hudm .fswin .water.can { cursor: pointer; }
.hudm .fswin .hudbar { position: absolute; left: 16px; right: 16px; bottom: 12px; display: flex; flex-direction: column; gap: 7px; }
.hudm .fswin .status { text-align: center; font-size: 14px; font-weight: 800; letter-spacing: .09em; text-transform: uppercase; color: #e8f1ff;
  text-shadow: 0 1px 2px #000, 0 0 12px rgba(120, 180, 255, .45); }
.hudm .fswin .scene.bite .status { color: var(--gold); font-size: 16px; text-shadow: 0 1px 2px #000, 0 0 16px color-mix(in srgb, var(--gold) 70%, transparent);
  animation: hudm-fs-pulse .6s ease-in-out infinite; }
@keyframes hudm-fs-pulse { 50% { transform: scale(1.06); } }
.hudm .fswin .pbar { position: relative; height: 8px; border-radius: 4px; background: rgba(0,0,0,.6); overflow: hidden;
  box-shadow: inset 0 1px 2px rgba(0,0,0,.8), 0 0 0 1px rgba(150, 190, 255, .2); transition: opacity .3s; }
.hudm .fswin .scene.idle .pbar { opacity: .35; }
.hudm .fswin .pbar i { position: relative; display: block; height: 100%; width: 0; overflow: hidden; border-radius: 4px; transition: width .3s;
  background: linear-gradient(180deg, rgba(255,255,255,.35), transparent 55%), linear-gradient(90deg, #1d63a8, #59c2ff); }
.hudm .fswin .scene.bite .pbar i { background: linear-gradient(180deg, rgba(255,255,255,.4), transparent 55%), linear-gradient(90deg, #a8661a, var(--gold)); }
.hudm .fswin .pbar i::after { content: ""; position: absolute; top: 0; bottom: 0; left: 0; width: 40px;
  background: linear-gradient(90deg, transparent, rgba(255,255,255,.5), transparent); animation: hudm-fs-sheen 2.4s linear infinite; }
@keyframes hudm-fs-sheen { from { transform: translateX(-40px); } to { transform: translateX(480px); } }
.hudm .fswin .ctrl { display: flex; gap: 8px; justify-content: center; align-items: center; flex-wrap: wrap; }
.hudm .fswin .ctrl .btn { min-width: 120px; }
.hudm .fswin .scene.bite .ctrl .btn.main { box-shadow: 0 0 0 1px #000, 0 0 18px color-mix(in srgb, var(--gold) 55%, transparent); }
.hudm .fswin .baitchip { padding: 3px 10px; border-radius: 10px; font-size: 11px; font-weight: 600; white-space: nowrap; color: #dbe6f5;
  background: rgba(0,0,0,.6); border: 1px solid rgba(150, 190, 255, .25); text-shadow: 0 1px 2px #000; }
.hudm .fswin .baitchip.low { color: var(--warn); border-color: var(--warn); }
.hudm .fswin .baitchip.out { color: var(--bad); border-color: var(--bad); }
.hudm .fswin .opts { display: flex; gap: 14px; justify-content: center; }
.hudm .fswin .opt { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #dbe6f5; text-shadow: 0 1px 2px #000; }
/* Cast results */
.hudm .fswin .result { position: absolute; left: 50%; top: 14px; translate: -50% 0; z-index: 2; max-width: 88%; display: flex; align-items: center; gap: 10px;
  padding: 8px 16px 8px 8px; border-radius: 10px; font-size: 12px; color: #fff; background: linear-gradient(180deg, rgba(14,24,42,.94), rgba(4,9,18,.94));
  border: 1px solid rgba(150, 190, 255, .25); box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 8px 24px rgba(0,0,0,.6);
  animation: hudm-fs-res .35s cubic-bezier(.2, 1.4, .4, 1); pointer-events: none; }
@keyframes hudm-fs-res { from { opacity: 0; transform: translateY(-10px) scale(.92); } }
.hudm .fswin .result i { width: 40px; height: 40px; flex: none; border-radius: 8px; background: rgba(0,0,0,.35) center / auto no-repeat; image-rendering: pixelated;
  box-shadow: inset 0 0 0 1px rgba(255,255,255,.08); }
.hudm .fswin .result .eb { display: block; font-size: 9px; font-weight: 700; letter-spacing: .18em; text-transform: uppercase; color: var(--dim); }
.hudm .fswin .result b { display: block; font-size: 13px; } .hudm .fswin .result small { display: block; color: var(--dim); font-size: 10px; }
.hudm .fswin .result.catch { border-color: color-mix(in srgb, var(--gold) 70%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 0 28px color-mix(in srgb, var(--gold) 40%, transparent), 0 8px 24px rgba(0,0,0,.6); }
.hudm .fswin .result.catch i { background-color: color-mix(in srgb, var(--gold) 18%, #000); box-shadow: inset 0 0 12px color-mix(in srgb, var(--gold) 45%, transparent); }
.hudm .fswin .result.catch b, .hudm .fswin .result.catch .eb { color: var(--gold); }
.hudm .fswin .result.lost .eb { color: var(--bad); }
.hudm .fswin .tally { align-self: center; padding: 2px 10px; border-radius: 10px; font-size: 10px; color: #b8c6da; background: rgba(0,0,0,.45); text-shadow: 0 1px 2px #000; }
.hudm .fswin .tally b { color: #fff; }
.hudm.narrow .fswin { width: 620px; }
.hudm.narrow .fswin .rod { width: 210px; padding: 10px 8px; }
`;

type Phase = "idle" | "wait" | "bite";

export class FishingWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly rodName = h("b");
  private readonly rodSub = h("small");
  private readonly partRows: HTMLElement[] = [];
  private readonly partNames: HTMLElement[] = [];
  private readonly partLines: HTMLElement[] = [];
  private readonly partCells: ItemCell[] = [];
  private readonly partDuras: HTMLElement[] = [];
  private readonly rodCol: HTMLElement;
  private readonly panel: HTMLElement;
  /** The open part picker: its FishingSlot (-1 = closed), element and anchor row. */
  private pickSlot = -1;
  private pickEl: HTMLElement | null = null;
  private pickRow: HTMLElement | null = null;
  private pickSig = "";
  /** A swap in flight: the old part is coming off `slot`; the new one (bag UniqueID `to`) is fitted once the slot is free. */
  private swap: { slot: number; from: bigint; to: bigint } | null = null;
  // Cast results: the server says nothing on most outcomes (see track()), so the window works them out.
  /** The cast in progress: whether a fish bit, whether that bite ran out, the furthest progress, and what arrived since
   *  the last S.FishingUpdate (the reel-in's GainedItem / chat come right before its Fishing = false update). */
  private line: { bit: boolean; slipped: boolean; progress: number; gained: UserItem[]; gotAway: boolean; bagFull: boolean } | null = null;
  private wasFishing = false;
  private wasBite = false;
  private readonly tally = { casts: 0, caught: 0, lost: 0 };
  private readonly tallyEl = h("div", "tally");
  private readonly resultEl = h("div", "result");
  /** Casts left: the rod's bait stack (GetBait reads only the bait slot; each cast uses one). */
  private readonly baitChip = h("span", "baitchip");
  private resultTimer = 0;
  private readonly chanceText = h("b");
  private readonly chanceBar = h("i");
  private readonly scene: HTMLElement;
  private readonly water: HTMLElement;
  /** The animated lake (rod, line, float, splashes); drawn only while the window is shown. */
  private readonly fx = new FishingScene();
  private readonly statusEl = h("div", "status");
  private readonly progressBar = h("i");
  private readonly castBtn: Btn;
  private readonly reelBtn: Btn;
  private readonly autoCast: ReturnType<typeof toggleSwitch>;
  private readonly escExit: ReturnType<typeof toggleSwitch>;
  private phase: Phase | "" = "";

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "fswin", null, 120);
    injectCss("win-fishing", CSS);
    this.m = ui.ctx.models;
    const f = this.m.fishing;

    // Rod + parts
    const rodCell = ui.cells.cell("eq", EquipmentSlot.Weapon, { size: 40, readOnly: true });
    const rodCard = h("div", "rodcard", rodCell.el, h("div", "t", this.rodName, this.rodSub));
    const parts = h("div", "");
    for (const [slot, , name, , , icon] of PARTS) {
      const cell = ui.cells.cell("fishing", slot, { size: 38, empty: svg(icon) });
      const nm = h("b"), line = h("small"), dura = h("div", "dura", h("i"));
      const row = h("div", "part", cell.el, h("div", "t", nm, line, dura));
      row.title = `${name}: click to choose one from your bag · right-click the part to take it off`;
      this.partRows.push(row); this.partNames.push(nm); this.partLines.push(line); this.partCells.push(cell); this.partDuras.push(dura);
      parts.append(row);
      // The picker: a click on the row, or on the cell while it is empty and nothing is picked up (a filled cell's click
      // stays drag.ts's pick / place, its right-click the take-off).
      this.listen(row, "click", (e) => {
        if ((e.target as Element).closest(".icell") && (cell.item || this.m.itemPick.cell)) return;
        if (this.m.itemPick.cell) return;
        if (this.pickSlot === slot) this.closePicker(); else this.openPicker(slot, row);
      });
    }
    const chance = h("div", "chance", h("div", "lbl", "Catch chance", this.chanceText), h("div", "cbar", this.chanceBar),
      h("div", "hint", "From the server while you fish."));
    const rod = this.rodCol = h("div", "rod", rodCard, h("div", "lbl", "Parts"), parts, chance);
    this.listen(rod, "scroll", () => this.closePicker());

    // Water scene
    this.water = h("div", "water", this.fx.el);
    const ro = new ResizeObserver(() => this.fx.resize(this.water.clientWidth, this.water.clientHeight));
    ro.observe(this.water);
    this.signal.addEventListener("abort", () => ro.disconnect(), { once: true });
    this.frameShown((now) => this.fx.frame(now));
    this.listen(this.water, "click", (e) => {
      if ((e.target as HTMLElement).closest(".hudbar")) return;
      if (f.fishing && f.foundFish) f.reelIn();
      else if (!f.fishing) this.cast();
    });

    this.castBtn = button("Cast", () => this.cast(), { cls: "main big", icon: "fishing" });
    this.reelBtn = button("Reel in", () => { if (f.fishing) f.reelIn(); }, { cls: "big" });
    this.autoCast = toggleSwitch(f.autoCast, () => { f.toggleAutoCast(); this.autoCast.set(f.autoCast); }, { small: true, label: "Auto cast" });
    this.escExit = toggleSwitch(f.escExit, () => { f.toggleEscExit(); this.escExit.set(f.escExit); },
      { small: true, label: "Esc exit", title: "Closing this window (Esc) reels in" });
    const help = asButton(h("div", "btn big", svg("help")), "Help: Fishing");
    help.title = "Help: Fishing";
    this.listen(help, "click", () => { ui.help("Fishing"); });
    this.resultEl.hidden = true;
    this.water.append(this.resultEl);
    const hudbar = h("div", "hudbar", this.statusEl, this.tallyEl, h("div", "pbar", this.progressBar),
      h("div", "ctrl", this.castBtn.el, this.reelBtn.el, this.baitChip, help),
      h("div", "opts", h("label", "opt", this.autoCast.el, "Auto cast"), h("label", "opt", this.escExit.el, "Esc exit")));
    this.water.append(hudbar);
    this.scene = h("div", "scene idle", this.water);

    this.panel = h("div", "panel", this.titleBar("FISHING", "Close"), h("div", "fbody", rod, this.scene));
    this.el.append(this.panel);
    this.el.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: this.signal });
    // An outside press closes the picker (its own row toggles it in the click handler).
    window.addEventListener("pointerdown", (e) => {
      const t = e.target;
      if (this.pickEl && !(t instanceof Node && (this.pickEl.contains(t) || this.pickRow?.contains(t)))) this.closePicker();
    }, { capture: true, signal: this.signal });

    const off = [
      // PlayerObject.FishingUpdate: the dialog hides once the rod is gone, else a refresh
      f.updated.on(() => { if (!fishingRod(this.m.inventory)) this.toggle(false); else this.invalidate(); }),
      this.m.inventory.subscribe(() => {
        if (this.swap) queueMicrotask(() => this.stepSwap()); // not from inside the model's notify
        if (this.open && !fishingRod(this.m.inventory)) this.toggle(false); else this.invalidate();
      }),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
      ui.packets.on((p) => this.track(p)), // before game.ts feeds FishingUpdate to the Fishing model: track reads the packet
    ];
    this.signal.addEventListener("abort", () => clearTimeout(this.resultTimer), { once: true });
    this.signal.addEventListener("abort", () => { for (const o of off) o(); }, { once: true });
    // canFish reads the user's action, direction and cell (no events): re-checked while shown (writes are diffed).
    this.everyShown(200, () => this.renderCast());
  }

  /** FishingDialog.Show: refused with NoFishingRod without a rod (the desktop's message box). */
  override toggle(show = !this.open): void {
    if (show && !this.open && !fishingRod(this.m.inventory)) {
      void this.ui.rt.popups.confirm({ title: "Fishing", text: "You are not holding a fishing rod.", kind: "info", buttons: "ok" });
      return;
    }
    super.toggle(show);
  }

  /** "Esc exit" ticked: every close reels in while the line is out (FishingStatusDialog.Cancel on Closeall). */
  protected override shown(): void { this.fx.setPhase(this.phase || "idle"); }

  protected override closed(): void {
    const f = this.m.fishing;
    this.fx.snap(); // reopened later: jump to the then-current pose instead of replaying what was missed
    this.closePicker();
    if (f.escExit && f.fishing) f.reelIn();
  }

  /** MapControl.CheckInput's cast, facing the user's own direction: GameScene.CanFish, then FishingTime and
   *  C.FishingCast CastOut = true (the same throttle the map click uses). */
  private cast(): void {
    const ctx = this.ui.ctx, u = ctx.player.user, now = ctx.now();
    if (!u || this.m.fishing.fishing || !canFish(u, u.direction, ctx.world.map, now)) return;
    u.fishingTime = now;
    ctx.send(ClientPackets.FishingCast({ CastOut: true }));
  }

  /** Why the user cannot cast now ("" = can). */
  private castRefusal(): string {
    const ctx = this.ui.ctx, u = ctx.player.user;
    if (!u) return "Not in the game";
    if (this.m.fishing.fishing) return "Your line is out";
    if (canFish(u, u.direction, ctx.world.map, ctx.now())) return "";
    return u.hasFishingRod ? "Stand still facing fishing water 3 cells ahead" : "You are not holding a fishing rod";
  }

  private renderCast(): void {
    const why = this.castRefusal();
    this.castBtn.setDisabled(!!why, why);
    this.water.classList.toggle("can", !why || (this.m.fishing.fishing && this.m.fishing.foundFish));
    if (!this.m.fishing.fishing) setText(this.statusEl, why ? "Face fishing water and stand still to cast" : "Cast your line");
  }

  protected override render(): void {
    const f = this.m.fishing, inv = this.m.inventory;
    const rod = fishingRod(inv), info = this.info(rod);
    if (rod && !info) this.m.gameInfo.requestItem(rod.ItemIndex);

    // FishingDialog: title + parts
    setText(this.rodName, rod ? this.nameOf(rod, info) : "No fishing rod");
    setStyle(this.rodName, "--gc", info ? gradeVar(info.Grade) : "");
    const slots = rod?.Slots ?? [];
    setText(this.rodSub, rod ? `Equipped · ${slots.length} part slots` : "Hold a fishing rod to fish");
    PARTS.forEach(([slot, type, name, plural, hint], i) => {
      const item = slots[slot] ?? null, ii = this.info(item);
      if (item && !ii) this.m.gameInfo.requestItem(item.ItemIndex); // GameScene.Bind
      const row = this.partRows[i]!;
      row.hidden = slot >= slots.length;
      row.classList.toggle("empty", !item);
      setText(this.partNames[i]!, item ? this.nameOf(item, ii) : `No ${name.toLowerCase()}`);
      setStyle(this.partNames[i]!, "--gc", ii ? gradeVar(ii.Grade) : "");
      const spare = this.candidates(type).length;
      const d = item && ii ? partDura(item, ii) : null;
      const worn = !!d && item!.CurrentDura <= 0; // DamagedFishingItem deletes a part at 0 on its next use
      let line: string;
      if (!item) line = spare ? `${spare} in your bag · click to choose` : `None in your bag · ${hint.toLowerCase()}`;
      else if (worn) line = "Worn out · breaks on next use";
      else {
        const stats = ii ? partStats(item, ii).text : "";
        line = slot === FishingSlot.Bait ? `${fmt(item.Count)} left` + (stats ? ` · ${stats}` : "") : stats || hint;
      }
      setText(this.partLines[i]!, line);
      row.classList.toggle("spare", !item && spare > 0);
      row.classList.toggle("worn", worn);
      row.title = `${name}: ${hint}\nClick to choose ${plural === "bait" ? "bait" : "a " + name.toLowerCase()} from your bag · right-click the part to take it off`
        + (d ? `\nDurability ${d.cur}/${d.max}` : "");
      const bar = this.partDuras[i]!;
      bar.hidden = !d;
      if (d) {
        setStyle(bar.firstElementChild as HTMLElement, "width", `${Math.round(d.ratio * 100)}%`);
        bar.classList.toggle("low", d.ratio > 0 && d.ratio < 0.2);
        bar.classList.toggle("out", d.ratio <= 0);
      }
    });
    if (this.pickSlot >= 0) this.renderPicker();

    // Casts left (the fitted bait only)
    const bait = slots[FishingSlot.Bait]?.Count ?? 0;
    this.baitChip.hidden = !rod || slots.length <= FishingSlot.Bait;
    setText(this.baitChip, bait ? `${fmt(bait)} cast${bait === 1 ? "" : "s"} left` : "No bait");
    this.baitChip.classList.toggle("low", bait > 0 && bait <= 5);
    this.baitChip.classList.toggle("out", bait <= 0);
    this.baitChip.title = bait ? "Each cast uses one bait from the rod's bait slot" : "Fit bait in the rod to cast";

    // FishingStatusDialog: chance, progress, phase
    const fishing = f.fishing;
    const phase: Phase = !fishing ? "idle" : f.foundFish ? "bite" : "wait";
    if (phase !== this.phase) {
      this.phase = phase;
      this.scene.className = "scene " + phase;
    }
    this.fx.setPhase(phase);
    setText(this.chanceText, fishing ? `${f.chancePercent}%` : "-");
    setStyle(this.chanceBar, "width", `${fishing ? Math.max(0, Math.min(100, f.chancePercent)) : 0}%`);
    setStyle(this.progressBar, "width", `${fishing ? Math.max(0, Math.min(100, f.progressPercent)) : 0}%`);
    const t = this.tally;
    this.tallyEl.hidden = !t.casts;
    const tally = `This session: <b>${t.casts}</b> cast${t.casts === 1 ? "" : "s"} · <b>${t.caught}</b> caught · <b>${t.lost}</b> got away`; // numbers only
    if (t.casts && this.tallyEl.innerHTML !== tally) this.tallyEl.innerHTML = tally;
    if (phase === "wait") setText(this.statusEl, this.line?.slipped ? "The fish is gone · reel up and cast again" : "Waiting for a bite…");
    else if (phase === "bite") setText(this.statusEl, "A fish is biting! Reel in");
    this.reelBtn.setLabel(phase === "bite" ? "Reel in!" : "Reel up");
    this.reelBtn.el.classList.toggle("main", phase === "bite");
    this.reelBtn.setDisabled(!fishing, "Your line is not out");
    this.castBtn.el.hidden = fishing;
    this.reelBtn.el.hidden = !fishing;

    // Auto cast (FishingStatusDialog_BeforeDraw: needs a Reel in the rod; losing it turns it off on the server)
    f.refreshAutoCast();
    this.autoCast.set(f.autoCast);
    this.autoCast.lock(!f.canAutoCast, "Auto cast needs a reel in the rod");
    this.escExit.set(f.escExit);
    this.renderCast();
  }

  // — Cast results —

  /** Works out each cast's result from the packets (PlayerObject.FishingCast / UpdateFish):
   *  - a cast gets at most one bite (FishFirstFound); it lasts ~3 s (FishingFoundTime), then FoundFish drops and nothing
   *    else bites that cast: "slipped";
   *  - reel-in on a bite: S.GainedItem (caught), "Your fish got away!" (the catch roll failed, or this water has no
   *    catch) or "You do not have enough space." (bag full), then S.FishingUpdate Fishing = false;
   *  - reel-in without a bite, or the line running out at 100% (the server reels in): no message at all; a full line
   *    raises the next cast's chance (FishingChanceCounter).
   *  Auto cast reels in and casts again: a false update, then a true one. The chat texts are the server's English
   *  ServerTextKeys (FishGotAway, NoBagSpace). */
  private track(p: ServerPacket): void {
    const c = this.line;
    if (p.type === "GainedItem") { if (c && this.wasFishing) c.gained.push(p.Item); return; }
    if (p.type === "Chat") {
      if (c && p.Message === "Your fish got away!") c.gotAway = true;
      else if (c && p.Message === "You do not have enough space.") c.bagFull = true;
      return;
    }
    if (p.type !== "FishingUpdate" || p.ObjectID !== this.ui.ctx.player.user?.id) return;
    // The packet's own fields: ui.handle (this listener) runs before game.ts feeds FishingUpdate to the Fishing model,
    // so the model still holds the previous update here.
    const f = { fishing: p.Fishing, foundFish: p.FoundFish, progressPercent: p.ProgressPercent };
    if (f.fishing && !this.wasFishing) {
      this.line = { bit: false, slipped: false, progress: 0, gained: [], gotAway: false, bagFull: false };
      this.tally.casts++;
    } else if (!f.fishing && this.wasFishing && c) {
      this.finish(c);
      this.line = null;
    }
    const cur = this.line;
    if (cur && f.fishing) {
      cur.progress = Math.max(cur.progress, f.progressPercent);
      if (f.foundFish) cur.bit = true;
      else if (this.wasBite && !cur.slipped) { cur.slipped = true; this.fx.slip(); this.showResult("lost", "It slipped the hook", "Too slow: reel in within about 3 seconds of a bite. No more bites this cast."); }
      // only what arrives between the last update and the reel-in's update belongs to the reel-in
      cur.gained.length = 0; cur.gotAway = cur.bagFull = false;
    }
    this.wasFishing = f.fishing;
    this.wasBite = f.fishing && f.foundFish;
    this.invalidate();
  }

  /** The reel-in's result (Fishing went false). */
  private finish(c: NonNullable<FishingWindow["line"]>): void {
    if (c.gained.length) {
      this.tally.caught++;
      const item = c.gained[0]!, info = this.info(item);
      if (!info) this.m.gameInfo.requestItem(item.ItemIndex);
      const name = info ? this.nameOf(item, info) : "a fish";
      this.fx.outcome("catch");
      this.showResult("catch", `Caught ${name}!`, c.gained.length > 1 ? `and ${c.gained.length - 1} more` : "Added to your bag", info?.Image);
    } else if (c.bagFull) {
      this.tally.lost++;
      this.fx.outcome("lost");
      this.showResult("lost", "Caught one, but your bag is full", "Make room in your bag before casting again.");
    } else if (c.gotAway) {
      this.tally.lost++;
      this.fx.outcome("lost");
      this.showResult("lost", "Your fish got away!", "A better hook, bait or reel raises the catch chance.");
    } else if (c.slipped) {
      this.tally.lost++;
      this.showResult("lost", "Nothing caught", "The bite was missed: reel in as soon as a fish bites.");
    } else if (c.progress >= 100) {
      this.showResult("", "Nothing bit this cast", "The line ran out. Your next cast has a better catch chance.");
    } else {
      this.showResult("", "Reeled in empty", "Nothing was biting. Wait for a bite before you reel in.");
    }
  }

  /** The result card at the top of the water for a few seconds. */
  private showResult(kind: "" | "catch" | "lost", title: string, sub: string, image?: number): void {
    const icon = h("i");
    icon.hidden = image === undefined;
    if (image !== undefined) void libPicture(Lib.Items, image).then((pic) => { if (pic) icon.style.backgroundImage = `url(${pic.url})`; });
    this.resultEl.className = "result" + (kind ? " " + kind : "");
    const eyebrow = kind === "catch" ? "Catch" : kind === "lost" ? "Missed" : "Line in";
    this.resultEl.replaceChildren(icon, h("div", "", h("span", "eb", eyebrow), h("b", "", title), h("small", "", sub)));
    this.resultEl.hidden = false;
    clearTimeout(this.resultTimer);
    this.resultTimer = window.setTimeout(() => { this.resultEl.hidden = true; }, kind === "catch" ? 7000 : 5000);
  }

  // — Part picker —

  /** Bag items of `type`, the usable ones first, best first (MirItemCell.CanWearItem decides "usable"). */
  private candidates(type: number): Candidate[] {
    const inv = this.m.inventory, out: Candidate[] = [];
    inv.inventory.forEach((item, idx) => {
      const info = item ? this.info(item) : undefined;
      if (!item || !info || info.Type !== type) return;
      const why = inv.isLocked(item) ? "Waiting for the server" : inv.canWear(item, -1) ?? "";
      const d = partDura(item, info);
      out.push({ idx, item, info, why, score: partStats(item, info).score + (d ? d.ratio / 100 : 0) });
    });
    return out.sort((a, b) => Number(!!a.why) - Number(!!b.why) || b.score - a.score);
  }

  private openPicker(slot: number, row: HTMLElement): void {
    this.closePicker();
    this.pickSlot = slot;
    this.pickRow = row;
    row.classList.add("open");
    this.renderPicker();
  }

  private closePicker(): void {
    this.pickEl?.remove();
    this.pickRow?.classList.remove("open");
    this.pickEl = this.pickRow = null;
    this.pickSlot = -1;
    this.pickSig = "";
  }

  /** Builds the picker for pickSlot next to its row (rebuilt on every model change while open; it is a few rows). */
  private renderPicker(): void {
    const slot = this.pickSlot, row = this.pickRow, def = PARTS.find((p) => p[0] === slot);
    const inv = this.m.inventory, rod = fishingRod(inv);
    if (!row || !def || !rod || slot >= rod.Slots.length) { this.closePicker(); return; }
    const [, type, name, plural] = def;
    const cur = rod.Slots[slot] ?? null, curInfo = this.info(cur);
    const list = this.candidates(type);
    const fishing = this.m.fishing.fishing;
    const freeBag = inv.inventory.some((i) => !i);
    const busy = this.swap !== null || this.m.itemChanges.isSlotPending(MirGridType.Fishing, slot) || (!!cur && inv.isLocked(cur));
    // Rebuilt only when what it shows changed (S.FishingUpdate redraws the window every fishing tick; a rebuild under
    // the pointer would drop a click).
    const sig = [slot, cur ? `${cur.UniqueID}:${cur.Count}:${cur.CurrentDura}:${curInfo ? 1 : 0}` : "", fishing, freeBag, busy,
      ...list.map((c) => `${c.item.UniqueID}:${c.item.Count}:${c.item.CurrentDura}:${c.why}`)].join("|");
    if (this.pickEl && sig === this.pickSig) return;
    this.pickSig = sig;

    const el = h("div", "pick");
    el.append(h("div", "ph", h("b", "", cur ? `Change ${name.toLowerCase()}` : `Choose ${plural === "bait" ? "bait" : "a " + name.toLowerCase()}`),
      h("small", "", list.length ? `${list.length} in your bag` : `No ${plural} in your bag`)));
    const body = h("div", "pl");
    if (cur) {
      const why = busy ? "Waiting for the server" : fishing ? "Reel in first" : !freeBag ? "Your bag is full" : "";
      body.append(this.pickRow2(null, curInfo ? `Take off ${this.nameOf(cur, curInfo)}` : "Take off", "Back to your bag", "Remove", why,
        () => this.m.itemChanges.removeSlotItem(MirGridType.Fishing, slot)));
    }
    const best = list.length > 1 && !list[0]!.why ? list[0] : null;
    for (const c of list) {
      const merge = !!cur && type === ItemType.Bait && cur.ItemIndex === c.item.ItemIndex && cur.Count < c.info.StackSize;
      const act = !cur ? "Fit" : merge ? "Add" : "Swap";
      const why = c.why || (busy ? "Waiting for the server" : cur && !merge && fishing ? "Reel in first" : cur && !merge && !freeBag ? "Your bag is full" : "");
      const d = partDura(c.item, c.info), stats = partStats(c.item, c.info).text;
      const sub = [type === ItemType.Bait ? `${fmt(c.item.Count)} in stack` : "", stats, d ? `Durability ${d.cur}/${d.max}` : ""].filter(Boolean).join(" · ");
      body.append(this.pickRow2(c, this.nameOf(c.item, c.info), sub, act, why, () => this.choose(slot, c, merge), c === best));
    }
    if (!list.length) body.append(h("div", "pe", `Get ${plural} from a fishing merchant, then drop or right-click them here.`));
    el.append(body);

    this.pickEl?.remove();
    this.pickEl = el;
    this.panel.append(el);
    // Beside the rod column, level with the row; kept inside the panel.
    const top = row.offsetTop - this.rodCol.scrollTop;
    el.style.left = `${this.rodCol.offsetLeft + this.rodCol.offsetWidth + 6}px`;
    el.style.top = `${Math.max(36, Math.min(top, this.panel.clientHeight - el.offsetHeight - 6))}px`;
  }

  /** One picker row: a bag candidate (icon) or the take-off row (null). */
  private pickRow2(c: Candidate | null, title: string, sub: string, act: string, why: string, run: () => void, best = false): HTMLElement {
    const icon = h("i", "pi");
    if (c) void libPicture(Lib.Items, c.info.Image).then((pic) => { if (pic) icon.style.backgroundImage = `url(${pic.url})`; });
    else icon.append(svg("M12 4v12M6 10l6 6 6-6M5 20h14"));
    const b = h("b", "", title);
    if (c) setStyle(b, "--gc", gradeVar(c.info.Grade));
    if (best) b.append(h("span", "best", "BEST"));
    const t = h("div", "t", b, h("small", "", sub));
    if (why) t.append(h("small", "", h("em", "", why)));
    const btn = h("button", "pr" + (why ? " off" : ""), icon, t, h("span", "act", act)) as HTMLButtonElement;
    btn.type = "button";
    btn.disabled = !!why;
    if (why) btn.title = why;
    btn.addEventListener("click", () => { this.closePicker(); run(); });
    return btn;
  }

  /** Fit `c` into `slot`: straight in when the slot is empty (or bait of the same kind tops up the stack: ItemChanges
   *  sends C.MergeItem); otherwise a swap: C.RemoveSlotItem first (the server fits nothing over a part), then the new
   *  part once the slot is free (stepSwap). */
  private choose(slot: number, c: Candidate, merge: boolean): void {
    const cur = fishingRod(this.m.inventory)?.Slots[slot] ?? null;
    if (!cur || merge) { this.m.itemChanges.useSlotItem(c.idx); return; }
    this.swap = { slot, from: cur.UniqueID, to: c.item.UniqueID };
    this.m.itemChanges.removeSlotItem(MirGridType.Fishing, slot);
    this.stepSwap(); // refused before sending (fishing, bag full): the old part is still there and unlocked
  }

  /** Advances a swap after an item change: fits the new part once the old one has left the slot; drops the swap when
   *  the removal was refused or the new part is gone. */
  private stepSwap(): void {
    const s = this.swap, inv = this.m.inventory;
    if (!s) return;
    const rod = fishingRod(inv);
    if (!rod) { this.swap = null; return; }
    const now = rod.Slots[s.slot] ?? null;
    if (now) {
      if (now.UniqueID !== s.from || !inv.isLocked(now)) this.swap = null;
      return;
    }
    if (this.m.itemChanges.isSlotPending(MirGridType.Fishing, s.slot)) return;
    this.swap = null;
    const idx = inv.inventory.findIndex((i) => i?.UniqueID === s.to);
    if (idx >= 0 && !inv.isLocked(inv.inventory[idx]!)) this.m.itemChanges.useSlotItem(idx);
    this.invalidate();
  }

  private info(item: UserItem | null | undefined): ItemInfo | undefined { return item ? this.m.itemInfos.get(item.ItemIndex) : undefined; }
  private nameOf(item: UserItem, info: ItemInfo | undefined): string {
    return info ? (itemFriendlyName(info) || info.Name) + (item.Count > 1 ? ` (${item.Count})` : "") : `Item #${item.ItemIndex}`;
  }
}

registerWindow(FISHING_KEY, (host) => new FishingWindow(host), {
  // FishingStatusDialog shows while S.FishingUpdate says the user is fishing: open on a cast starting and on a bite
  // (once per transition, so a window the player closed stays closed while they wait).
  watch: (host, open) => {
    const f = host.ctx.models.fishing;
    let wasFishing = f.fishing, wasBite = f.foundFish;
    return f.updated.on(() => {
      const start = f.fishing && !wasFishing, bite = f.fishing && f.foundFish && !wasBite;
      wasFishing = f.fishing;
      wasBite = f.foundFish;
      if ((start || bite) && !host.rt.isOpen(FISHING_KEY) && fishingRod(host.ctx.models.inventory)) open(true);
    });
  },
});
