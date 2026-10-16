// Ported from play/hudCreature.ts (CreatureMock): the new UI's intelligent creature window over the real Creatures model
// (src/game/creatures.ts: UserInformation, S.UpdateIntelligentCreatureList, S.NewIntelligentCreature,
// S.IntelligentCreatureEnableRename). Desktop counterpart: IntelligentCreatureDialog + its Options / Grade dialogs; the
// classic port is src/ui/creatureWindow.ts, whose rules are kept:
//  - showing the window asks the server for live updates (C.RequestIntelligentCreatureUpdates true; the server then
//    re-sends the list every second while a creature is out), closing stops them;
//  - opening selects the summoned creature; the selection is a PetType resolved per render (never a stale object);
//  - Summon is off while another creature is out (the server refuses it), Release is off while this one is out and asks
//    for the creature's name again (case-insensitive; a mismatch is the "Verification Failed!!" chat line);
//  - Rename only after S.IntelligentCreatureEnableRename (a Mirror was used) and once per grant, CREATURE_NAME rule;
//  - the pickup mode switches only to a mode the creature's rules allow; the item filter follows setItemFilter (the
//    desktop's exact rules); the minimum grade is Filter.PickupGrade (the server picks up Grade >= it);
//  - a NEW creature's naming prompt (S.NewIntelligentCreature) is a popup input wired at UI start (the registry's watch
//    hook), so it works while this window is closed or not built yet.
// Server authority: Summon / Dismiss / Release go out as C.UpdateIntelligentCreature and lock the window's actions until
// the server's S.UpdateIntelligentCreatureList (or 4 s, for the refusals that send nothing back, e.g. a no-creature map).
// A plain update (name, pickup mode, filter, grade) gets NO answer (MirConnection.UpdateIntelligentCreature stores the
// fields and replies nothing), so the model entry is written after sending, like the classic window and the desktop.
// Changes from the mock:
//  - Feed uses a real creature food (ItemType.Pets, Shape 23: PlayerObject's FairyMoss / Clam / Mackerel / Cherry case)
//    from the bag through Inventory.use (the cell locks until the server answers); only for the summoned creature;
//  - the blackstone ring is BlackstoneTime / BLACKSTONE_PRODUCE_TIME (production runs only while the creature is out)
//    with a countdown; no "N ready" / Collect (the server awards blackstones itself);
//  - pickup / auto / semi-auto ranges and the minimum fullness are read-only server rules (CreatureRules), not sliders;
//  - the pickup feed is a count: S.IntelligentCreaturePickup carries only the creature's ObjectID and is sent to the
//    master only for Legendary / Mythical / Heroic finds (the item name comes as a chat hint), counted from UI start;
//  - HIDDEN: the "spend pearls on new creatures" prompt (no in-window system; pearls are a count), the mock's local
//    fullness drain, random loot and auto-dismiss of the other creature on summon.
// Animation: the Prguse2 loops (creatureFrames: the default loop, the extra loop once every 8 s) run on the UI ticker at
// 100 ms only while shown; each element is written only when its frame changes; reduced motion shows the first frame.
// Narrow layout (a layer below 1040 px): the stage and settings stack in one scrolling column beside the list.

import { ClientPackets } from "../../../protocol/client.ts";
import type { ClientIntelligentCreature, ItemInfo } from "../../../protocol/server.ts";
import { IntelligentCreaturePickupMode as PM, IntelligentCreatureTypeName, ItemGrade, ItemType } from "../../../protocol/enums.generated.ts";
import { BLACKSTONE_PRODUCE_TIME, CREATURE_NAME, creatureFrames, itemFilterFlags, setItemFilter, type Creatures } from "../../../game/creatures.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { Emitter } from "../../../game/emitter.ts";
import { KeybindOptions, keyLabel } from "../../../game/keybinds.ts";
import { Lib, libPicture, libPictureNow } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, asButton, button, chips, fmt, fmtClock, fmtDuration, gradeName, gradeVar, h, injectCss, later, plural, pretty, progressRing,
  reducedMotion, segmented, setStyle, setText, type Btn, type Choice,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.creatures). */
export const CREATURES_WINDOW = "creatures";

/** IntelligentCreatureObject: Fullness runs 0..10000. */
const MAX_FULL = 10000;
/** PlayerObject UseItem ItemType.Pets: Shape 23 = fullness food (+Effect x 100). */
const FOOD_SHAPE = 23;
/** How long Summon / Dismiss / Release stay locked without an answer (refusals send nothing back). */
const PENDING_MS = 4000;
/** CreatureButton: the extra loop plays once every 8 s. */
const EXTRA_EVERY = 8000;

/** IntelligentCreatureOptionsDialog.OptionNames order (setItemFilter / itemFilterFlags indices). */
const FILTERS: readonly Choice<number>[] = ["All items", "Gold", "Weapons", "Armours", "Helmets", "Boots", "Belts", "Jewellery", "Others"]
  .map((label, value) => ({ value, label }));
const GRADES: readonly number[] = [ItemGrade.None, ItemGrade.Common, ItemGrade.Rare, ItemGrade.Legendary, ItemGrade.Mythical, ItemGrade.Heroic];

// — Per-game dialog state that must live while the window is closed or unbuilt (watch hook) —

interface CreatureUiState {
  /** GameScene.IntelligentCreatureEnableRename: the Rename button shows until it is used. */
  renameEnabled: boolean;
  /** S.IntelligentCreaturePickup since UI start (rare finds only, see the header). */
  rareFinds: number;
  /** Fires when either changes or a name was set from the new-creature prompt. */
  readonly changed: Emitter<void>;
}
const states = new WeakMap<Creatures, CreatureUiState>();
function stateOf(c: Creatures): CreatureUiState {
  let s = states.get(c);
  if (!s) states.set(c, (s = { renameEnabled: false, rareFinds: 0, changed: new Emitter<void>() }));
  return s;
}

/** C.UpdateIntelligentCreature (IntelligentCreatureDialog.SendUpdate). */
function sendUpdate(host: ModernUiHost, c: ClientIntelligentCreature, o: { summon?: boolean; unsummon?: boolean; release?: boolean } = {}): void {
  host.ctx.send(ClientPackets.UpdateIntelligentCreature({ Creature: c, SummonMe: !!o.summon, UnSummonMe: !!o.unsummon, ReleaseMe: !!o.release }));
}

/** A name check for the popups (Globals name rule; the server ignores any other name). */
function nameProblem(v: string): string { return CREATURE_NAME.test(v) ? "" : "Creature name must be between 3 and 15 characters."; }

function typeName(t: number): string { return pretty(IntelligentCreatureTypeName[t] ?? "Creature"); }

/** The expiry line: DateTime.MinValue = never (PlayerObject.RefreshCreaturesTimeLeft). */
function expiry(c: ClientIntelligentCreature): { text: string; never: boolean } {
  if (BigInt.asUintN(62, BigInt(c.Expire)) === 0n) return { text: "Permanent", never: true };
  const left = (dateFromBinary(BigInt(c.Expire)).getTime() - Date.now()) / 1000;
  return { text: left > 0 ? `${fmtDuration(left)} left` : "Expiring", never: false };
}

const CSS = `
.hudm .crwin { width: 920px; }
.hudm.narrow .crwin { width: 640px; }
.hudm .crwin .panel { height: 560px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .crwin .cbody { flex: 1; min-height: 0; display: flex; }
.hudm .crwin .list { width: 200px; flex: none; padding: 10px; display: flex; flex-direction: column; gap: 6px; overflow-y: auto; scrollbar-width: thin;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .crwin .list { width: 170px; }
.hudm .crwin .list > * { flex-shrink: 0; }
.hudm .crwin .ccard { position: relative; display: flex; align-items: center; gap: 9px; padding: 7px; border-radius: var(--rad); cursor: pointer; outline: none;
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .crwin .ccard:hover, .hudm .crwin .ccard:focus-visible { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .crwin .ccard.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .crwin .mini { width: 46px; height: 46px; flex: none; border-radius: 50%; overflow: hidden; border: 1px solid var(--fr);
  background: radial-gradient(circle at 50% 60%, color-mix(in srgb, var(--ac) 20%, transparent), var(--pb) 70%); }
.hudm .crwin .mini i { display: block; width: 100%; height: 100%; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .crwin .ccard .t { flex: 1; min-width: 0; }
.hudm .crwin .ccard b { display: block; font-size: 13px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .crwin .ccard small { display: block; font-size: 10px; color: var(--dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .crwin .fb { height: 4px; margin-top: 5px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .crwin .fb i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .crwin .fb.hungry i { background: var(--bad); }
.hudm .crwin .out { position: absolute; right: 5px; top: 5px; font-size: 8px; font-weight: 800; letter-spacing: .08em; padding: 1px 5px; border-radius: 6px; color: #fff; background: var(--ac); }
.hudm .crwin .pearls { margin-top: auto; padding: 8px; border-radius: var(--rad); font-size: 11px; color: var(--dim); text-align: center; background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .crwin .pearls b { display: block; font-size: 16px; color: #e8f4ff; }
.hudm .crwin .main { flex: 1; min-width: 0; display: flex; }
.hudm.narrow .crwin .main { flex-direction: column; overflow-y: auto; scrollbar-width: thin; }
.hudm .crwin .main[hidden] { display: none; }
.hudm .crwin .none { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; color: var(--dim); font-size: 12px; text-align: center; padding: 20px; }
.hudm .crwin .none b { font-size: 15px; color: var(--tx); }
/* Stage */
.hudm .crwin .stage { width: 300px; flex: none; display: flex; flex-direction: column; align-items: center; padding: 12px; gap: 8px;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .crwin .stage { width: auto; border-right: 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .crwin .ped { position: relative; width: 240px; height: 190px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 62%, color-mix(in srgb, var(--ac) 28%, transparent), transparent 62%), color-mix(in srgb, var(--pb) 75%, #000); }
.hudm.narrow .crwin .ped { height: 160px; }
.hudm .crwin .ped::before { content: ""; position: absolute; bottom: 26px; left: 50%; width: 150px; height: 26px; transform: translateX(-50%); border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 50%, transparent), transparent 70%); }
.hudm .crwin .ped::after { content: ""; position: absolute; inset: 0; background: radial-gradient(circle, transparent 55%, rgba(0,0,0,.6)); pointer-events: none; }
.hudm .crwin .sprite { position: relative; width: 120px; height: 90px; background: center 70% / auto no-repeat; image-rendering: pixelated; transform: scale(2); transform-origin: center 70%; }
.hudm .crwin .spark { position: absolute; width: 4px; height: 4px; border-radius: 50%; background: #fff; opacity: 0; animation: hudm-crspark 3s ease-in-out infinite; }
@keyframes hudm-crspark { 0%, 100% { opacity: 0; transform: translateY(0); } 50% { opacity: .9; transform: translateY(-14px); } }
.hudm .crwin .nameRow { display: flex; align-items: center; gap: 8px; max-width: 100%; }
.hudm .crwin .nameRow h3 { margin: 0; font-size: 19px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .crwin .sub { font-size: 11px; color: var(--dim); text-align: center; }
.hudm .crwin .acts { display: flex; gap: 6px; width: 100%; }
.hudm .crwin .acts .btn { flex: 1; }
.hudm .crwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; text-align: center; }
.hudm .crwin .finds { width: 100%; display: flex; justify-content: space-between; font-size: 11px; color: var(--dim); padding-top: 6px; border-top: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .crwin .finds b { color: var(--tx); }
/* Settings */
.hudm .crwin .cfg { flex: 1; min-width: 0; padding: 12px 14px; display: flex; flex-direction: column; gap: 12px; overflow-y: auto; scrollbar-width: thin; }
.hudm.narrow .crwin .cfg { overflow: visible; }
.hudm .crwin .cfg > * { flex-shrink: 0; }
.hudm .crwin .lbl.sl { display: flex; justify-content: space-between; margin-bottom: 6px; }
.hudm .crwin .lbl.sl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .crwin .gauges { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.hudm .crwin .gauge { display: flex; align-items: center; gap: 10px; padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .crwin .gauge .t { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); line-height: 1.4; }
.hudm .crwin .gauge .t > b { display: block; font-size: 14px; color: #fff; margin-bottom: 2px; }
.hudm .crwin .gauge .t > b.bad { color: var(--bad); }
.hudm .crwin .gauge .btn { margin-top: 6px; }
.hudm .crwin .hint { font-size: 11px; color: var(--dim); margin-top: 6px; line-height: 1.4; }
.hudm .crwin .rules { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; margin-top: 8px; }
.hudm .crwin .rules div { padding: 5px 8px; border-radius: var(--rad); font-size: 11px; color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .crwin .rules div b { display: block; font-size: 13px; color: var(--tx); }
.hudm .crwin .rules div.off b { color: var(--dim); }
.hudm .crwin .grow { display: flex; align-items: center; gap: 10px; margin-top: 8px; font-size: 12px; color: var(--dim); }
.hudm .crwin .grow select { min-width: 140px; }
.hudm .crwin .ui-seg button:disabled { opacity: .4; cursor: not-allowed; }
.hudm .crwin .locked { opacity: .55; pointer-events: none; }
`;

interface Card { el: HTMLElement; sprite: HTMLElement; bar: HTMLElement; fill: HTMLElement; sub: HTMLElement; type: number; lastIdx: number }

class CreaturesWindow extends UiWindow {
  private readonly cr: Creatures;
  private readonly st: CreatureUiState;
  /** The selected creature's PetType (resolved per render; -1 = none yet). */
  private sel = -1;
  /** The PetType a Summon / Dismiss / Release is waiting on (actions locked), and its timeout's cancel. */
  private pending = -1;
  private pendingCancel: (() => void) | null = null;
  private listKey = "";
  // animation
  private still = false;
  private extra = false;
  private exStart = 0;
  private nextExtra = 0;
  private spriteIdx = -1;
  private cards: Card[] = [];

  // — DOM, built once —
  private readonly listEl = h("div", "list");
  private readonly cardsEl = h("div", "");
  private readonly pearlsN = h("b");
  private readonly main = h("div", "main");
  private readonly none = h("div", "none", h("b", "", "No creatures"), "You do not own any creatures.");
  private readonly sprite = h("div", "sprite");
  private readonly nameEl = h("h3");
  private readonly renameBtn: Btn;
  private readonly subEl = h("div", "sub");
  private readonly summonBtn: Btn;
  private readonly releaseBtn: Btn;
  private readonly stageNote = h("div", "note");
  private readonly findsN = h("b");
  private readonly fullRing = progressRing(66, 7);
  private readonly fullHead = h("b");
  private readonly fullText = h("div");
  private readonly feedBtn: Btn;
  private readonly stoneRing = progressRing(66, 7, "#b48cff");
  private readonly stoneHead = h("b");
  private readonly stoneText = h("div");
  private readonly modeSeg: ReturnType<typeof segmented<number>>;
  private readonly modeHint = h("div", "hint");
  private readonly rulesEl = h("div", "rules");
  private readonly filterChips: ReturnType<typeof chips<number>>;
  private readonly filterCount = h("b");
  private readonly gradeSel = h("select", "fld");
  private readonly cfg: HTMLElement;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "crwin", 42, 30);
    injectCss("win-creatures", CSS);
    this.cr = ui.ctx.models.creatures;
    this.st = stateOf(this.cr);

    // Stage
    const ped = h("div", "ped", this.sprite);
    for (let i = 0; i < 4; i++) {
      const s = h("span", "spark");
      Object.assign(s.style, { left: 40 + i * 50 + "px", top: 50 + ((i * 37) % 60) + "px", animationDelay: i * 0.7 + "s" });
      ped.append(s);
    }
    this.renameBtn = button("Rename", () => void this.rename(), { icon: "memo", title: "Give this creature a new name (granted by a Mirror)" });
    this.summonBtn = button("Summon", () => this.summon(), { cls: "main big" });
    this.releaseBtn = button("Release", () => void this.release(), { cls: "warn big", title: "Let this creature go for good" });
    const stage = h("div", "stage", ped, h("div", "nameRow", this.nameEl, this.renameBtn.el), this.subEl,
      h("div", "acts", this.summonBtn.el, this.releaseBtn.el), this.stageNote,
      h("div", "finds", h("span", "", "Rare finds this session"), this.findsN));
    this.findsN.parentElement!.title = "Legendary, Mythical and Heroic items your creature picked up (the chat names each one)";

    // Gauges
    this.feedBtn = button("Feed", () => this.feed(), { cls: "main" });
    const full = h("div", "gauge", this.fullRing.el, h("div", "t", this.fullHead, this.fullText, this.feedBtn.el));
    const stone = h("div", "gauge", this.stoneRing.el, h("div", "t", this.stoneHead, this.stoneText));

    // Pickup mode (only to a mode the rules allow) + the read-only ranges
    this.modeSeg = segmented<number>([
      { value: PM.Automatic, label: "Automatic", title: "Collects matching items around it on its own" },
      { value: PM.SemiAutomatic, label: "Semi-automatic", title: "Collects when you press the creature pickup keys" },
    ], PM.SemiAutomatic, (v) => this.setMode(v));

    // Filter (setItemFilter's rules) + minimum grade
    this.filterChips = chips<number>(FILTERS, [0], (next) => this.toggleFilter(next));
    for (const g of GRADES) {
      const o = h("option", "", g === ItemGrade.None ? "Any grade" : `${gradeName(g)} +`);
      o.value = String(g);
      o.style.color = g === ItemGrade.None ? "" : gradeVar(g);
      this.gradeSel.append(o);
    }
    this.listen(this.gradeSel, "change", () => this.setGrade(Number(this.gradeSel.value)));

    this.cfg = h("div", "cfg", h("div", "gauges", full, stone),
      h("div", "", h("div", "lbl sl", "Pickup"), this.modeSeg.el, this.modeHint, this.rulesEl),
      h("div", "", h("div", "lbl sl", "Collect", this.filterCount), this.filterChips.el,
        h("div", "grow", "Minimum grade", this.gradeSel),
        h("div", "hint", "Ticking every kind turns into “All items”; unticking the last one does too.")));
    this.main.append(stage, this.cfg);

    const pearls = h("div", "pearls", this.pearlsN, "Pearls");
    pearls.title = "Pearls your creatures produced, used to buy creature items";
    this.listEl.append(this.cardsEl, pearls);
    const close = keyLabel(KeybindOptions.Creature);
    this.el.append(h("div", "panel", this.titleBar("CREATURES", close ? `Close (${close})` : "Close"), h("div", "cbody", this.listEl, this.main, this.none)));

    // Model events: dirty-when-closed. Any list packet answers a pending Summon / Dismiss / Release.
    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    on(this.cr.changed.on((ch) => {
      if (ch.kind === "load" || ch.kind === "replaced" || ch.kind === "updated") this.unlock();
      this.invalidate();
    }));
    on(this.st.changed.on(() => this.invalidate()));
    on(ui.ctx.models.inventory.subscribe(() => { if (this.open) this.refreshFeed(); }));

    // Shown-only timers: the sprite loops (100 ms) and the expiry / countdown texts (1 s).
    this.everyShown(100, (now) => this.animate(now));
    this.everyShown(1000, () => this.refreshLive());
  }

  protected override shown(): void {
    this.ui.ctx.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: true }));
    // IntelligentCreatureDialog.Show -> RefreshInfo: the summoned creature's button is selected
    if (this.cr.summoned && this.cr.list.some((c) => c.PetType === this.cr.summonedType)) this.select(this.cr.summonedType);
    this.still = reducedMotion();
    this.invalidate();
  }

  protected override closed(): void {
    this.ui.ctx.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: false }));
  }

  protected override disposed(): void { this.pendingCancel?.(); }

  // — State —

  /** The selected creature (falls back to the first; never a stale object). */
  private cur(): ClientIntelligentCreature | undefined {
    const list = this.cr.list;
    let c = list.find((x) => x.PetType === this.sel);
    if (!c && list.length) { c = list[0]!; this.select(c.PetType); }
    return c;
  }
  private byType(t: number): ClientIntelligentCreature | undefined { return this.cr.list.find((x) => x.PetType === t); }
  private isOut(c: ClientIntelligentCreature): boolean { return this.cr.summoned && this.cr.summonedType === c.PetType; }

  private select(t: number): void {
    if (t === this.sel) return;
    this.sel = t;
    this.extra = false;
    this.spriteIdx = -1;
    this.nextExtra = performance.now() + 3000;
  }

  private lock(t: number): void {
    this.pending = t;
    this.pendingCancel?.();
    this.pendingCancel = later(() => { this.pendingCancel = null; this.pending = -1; this.invalidate(); }, PENDING_MS, this.signal);
    this.invalidate();
  }
  private unlock(): void {
    if (this.pending < 0) return;
    this.pending = -1;
    this.pendingCancel?.();
    this.pendingCancel = null;
  }

  /** The first creature food in the bag (ItemType.Pets, Shape 23), not locked. */
  private food(): { index: number; info: ItemInfo } | null {
    const inv = this.ui.ctx.models.inventory;
    for (let i = 0; i < inv.inventory.length; i++) {
      const it = inv.inventory[i];
      if (!it || inv.isLocked(it)) continue;
      const info = inv.info(it);
      if (info && info.Type === ItemType.Pets && info.Shape === FOOD_SHAPE) return { index: i, info };
    }
    return null;
  }

  // — Actions —

  /** SummonButton / DismissButton: locked until the server's list update. */
  private summon(): void {
    const c = this.cur();
    if (!c || this.pending >= 0) return;
    const out = this.isOut(c);
    if (!out && this.cr.summoned) return; // another creature is out (the server refuses)
    sendUpdate(this.ui, c, out ? { unsummon: true } : { summon: true });
    this.lock(c.PetType);
  }

  /** ReleaseButton: type the creature's name again (case-insensitive), then ReleaseMe. */
  private async release(): Promise<void> {
    const c = this.cur();
    if (!c || this.pending >= 0 || this.isOut(c)) return;
    const type = c.PetType, name = c.CustomName;
    const v = await this.ui.rt.popups.input({ title: "Release creature",
      text: `Releasing ${name} is permanent: it leaves you for good.\nPlease enter the creature's name for verification.`, max: 15 });
    if (v === null || this.signal.aborted) return;
    const now = this.byType(type);
    if (!now || this.pending >= 0 || this.isOut(now)) return;
    if (v.toLowerCase() !== now.CustomName.toLowerCase()) { this.ui.chat.system("Verification Failed!!"); return; }
    sendUpdate(this.ui, now, { release: true });
    this.lock(type);
  }

  /** CreatureRenameButton (after S.IntelligentCreatureEnableRename; once per grant). */
  private async rename(): Promise<void> {
    const c = this.cur();
    if (!c || !this.st.renameEnabled) return;
    const type = c.PetType;
    const v = await this.ui.rt.popups.input({ title: "Rename creature", text: "Please enter a new name for the creature.", value: c.CustomName,
      min: 3, max: 15, chars: "A-Za-z0-9", check: nameProblem });
    if (v === null || this.signal.aborted || nameProblem(v) || !this.st.renameEnabled) return;
    const now = this.byType(type);
    if (!now) return;
    this.st.renameEnabled = false;
    sendUpdate(this.ui, { ...now, CustomName: v });
    now.CustomName = v; // no answer for a plain update (header)
    this.st.changed.emit();
  }

  /** Semi-auto / Automatic: only to a mode the creature's rules allow. */
  private setMode(v: number): void {
    const c = this.cur();
    if (!c || c.petMode === v || this.pending >= 0) { this.refreshSettings(); return; }
    const r = c.CreatureRules;
    if ((v === PM.Automatic && !r.AutoPickupEnabled) || (v === PM.SemiAutomatic && !r.SemiAutoPickupEnabled)) { this.refreshSettings(); return; }
    sendUpdate(this.ui, { ...c, petMode: v });
    c.petMode = v;
    this.invalidate();
  }

  /** IntelligentCreatureOptionsDialog CheckBoxClick: the chip that changed goes through setItemFilter, sent at once. */
  private toggleFilter(next: Set<number>): void {
    const c = this.cur();
    if (!c) return;
    const flags = itemFilterFlags(c.Filter);
    const idx = flags.findIndex((on, i) => on !== next.has(i));
    if (idx < 0 || this.pending >= 0) { this.refreshSettings(); return; }
    const f = { ...c.Filter };
    setItemFilter(f, idx);
    sendUpdate(this.ui, { ...c, Filter: f });
    c.Filter = f;
    this.invalidate();
  }

  /** IntelligentCreatureOptionsGradeDialog -> Filter.PickupGrade. */
  private setGrade(g: number): void {
    const c = this.cur();
    if (!c || c.Filter.PickupGrade === g || this.pending >= 0) { this.refreshSettings(); return; }
    const f = { ...c.Filter, PickupGrade: g };
    sendUpdate(this.ui, { ...c, Filter: f });
    c.Filter = f;
    this.invalidate();
  }

  /** Feed: C.UseItem of a creature food through the drag pacer + Inventory.use (the cell locks until the server answers). */
  private feed(): void {
    const c = this.cur();
    if (!c || !this.isOut(c) || c.Fullness >= MAX_FULL) return;
    const f = this.food();
    if (f) {
      const inv = this.ui.ctx.models.inventory;
      const uid = inv.inventory[f.index]!.UniqueID;
      this.ui.drag.request(() => {
        const i = inv.inventory.findIndex((it) => it?.UniqueID === uid); // re-read when the pacer runs
        if (i >= 0) inv.use(i, this.ui.ctx.now());
      }, [{ grid: "inv", index: f.index }]);
    }
    this.refreshFeed();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const list = this.cr.list, has = list.length > 0;
    this.main.hidden = !has;
    this.none.hidden = has;
    setText(this.pearlsN, `◈ ${fmt(this.cr.pearlCount)}`);
    const c = this.cur();
    // The cards: rebuilt only when the set / names / icons / summoned state change.
    const key = list.map((x) => `${x.PetType}:${x.CustomName}`).join("|") + `#${this.cr.summoned ? this.cr.summonedType : -1}#${this.sel}`;
    if (key !== this.listKey) {
      this.listKey = key;
      this.cards = list.map((x) => this.card(x, x === c));
      this.cardsEl.replaceChildren(...this.cards.map((k) => k.el));
    }
    this.refreshLive();
    this.refreshSettings();
    this.refreshFeed();
  }

  private card(x: ClientIntelligentCreature, selected: boolean): Card {
    const sprite = h("i"), fill = h("i"), bar = h("div", "fb", fill), sub = h("small");
    const el = asButton(h("div", "ccard" + (selected ? " sel" : ""), h("div", "mini", sprite), h("div", "t", h("b", "", x.CustomName), sub, bar)), x.CustomName);
    if (this.isOut(x)) el.append(h("span", "out", "OUT"));
    const type = x.PetType;
    el.addEventListener("click", () => { if (this.sel !== type) { this.select(type); this.invalidate(); } });
    return { el, sprite, bar, fill, sub, type, lastIdx: -1 };
  }

  /** Values that move without a structural change: fullness, blackstone, expiry, the stage texts and buttons. */
  private refreshLive(): void {
    for (const k of this.cards) {
      const x = this.byType(k.type);
      if (!x) continue;
      setStyle(k.fill, "width", Math.min(100, x.Fullness / MAX_FULL * 100).toFixed(1) + "%");
      k.bar.classList.toggle("hungry", x.Fullness < x.CreatureRules.MinimalFullness);
      setText(k.sub, `${typeName(x.PetType)} · ${expiry(x).text}`);
    }
    const c = this.cur();
    if (!c) return;
    const out = this.isOut(c), r = c.CreatureRules, busy = this.pending >= 0;
    setText(this.nameEl, c.CustomName);
    this.renameBtn.el.hidden = !this.st.renameEnabled;
    const ex = expiry(c);
    setText(this.subEl, `${typeName(c.PetType)} · ${ex.never ? "never expires" : ex.text}${out ? " · following you" : ""}`);
    setText(this.findsN, fmt(this.st.rareFinds));

    // Summon / Dismiss / Release (IntelligentCreatureDialog.RefreshUI)
    const other = this.cr.summoned && !out ? this.byType(this.cr.summonedType) : undefined;
    this.summonBtn.setLabel(busy && this.pending === c.PetType ? "Waiting…" : out ? "Dismiss" : "Summon");
    this.summonBtn.el.classList.toggle("main", !out);
    if (busy) this.summonBtn.setDisabled(true, "Waiting for the server");
    else if (this.cr.summoned && !out) this.summonBtn.setDisabled(true, `Dismiss ${other?.CustomName ?? "your other creature"} first`);
    else this.summonBtn.setDisabled(false);
    if (busy) this.releaseBtn.setDisabled(true, "Waiting for the server");
    else if (out) this.releaseBtn.setDisabled(true, "Dismiss it before releasing it");
    else this.releaseBtn.setDisabled(false);
    setText(this.stageNote, out
      ? (c.Fullness < r.MinimalFullness ? "Too hungry to pick anything up: feed it." : "Out and collecting.")
      : this.cr.summoned ? `${other?.CustomName ?? "Another creature"} is out.` : "Resting at home.");

    // Fullness ring (MinimalFullness = the level it needs to work)
    const hungry = c.Fullness < r.MinimalFullness;
    this.fullRing.set(c.Fullness / MAX_FULL, `${Math.floor(c.Fullness / 100)}%`);
    setStyle(this.fullRing.el, "--ring", hungry ? "var(--bad)" : "var(--ac)");
    setText(this.fullHead, hungry ? "Hungry!" : "Fullness");
    this.fullHead.classList.toggle("bad", hungry);
    setText(this.fullText, `${fmt(c.Fullness)} / ${fmt(MAX_FULL)} · needs ${fmt(r.MinimalFullness)}`);

    // Blackstone production (only while out; the server awards the stone itself)
    if (!r.CanProduceBlackStone) {
      this.stoneRing.set(0, "–");
      setText(this.stoneHead, "Blackstones");
      setText(this.stoneText, "No production.");
    } else {
      const done = Math.min(BLACKSTONE_PRODUCE_TIME, Math.max(0, Number(c.BlackstoneTime)));
      this.stoneRing.set(done / BLACKSTONE_PRODUCE_TIME, `${Math.floor(done / BLACKSTONE_PRODUCE_TIME * 100)}%`);
      setText(this.stoneHead, "Blackstone");
      setText(this.stoneText, `Next in ${fmtClock(BLACKSTONE_PRODUCE_TIME - done)}${out ? "" : " · paused while at home"} · also makes pearls`);
    }
  }

  /** The pickup mode, ranges, filter chips and grade of the selected creature. */
  private refreshSettings(): void {
    const c = this.cur();
    if (!c) return;
    const r = c.CreatureRules, busy = this.pending >= 0;
    this.cfg.classList.toggle("locked", busy);
    this.modeSeg.set(c.petMode);
    const [autoB, semiB] = [...this.modeSeg.el.querySelectorAll("button")] as HTMLButtonElement[];
    if (autoB) { autoB.disabled = !r.AutoPickupEnabled; autoB.title = r.AutoPickupEnabled ? "Collects matching items around it on its own" : "This creature cannot pick up on its own"; }
    if (semiB) { semiB.disabled = !r.SemiAutoPickupEnabled; semiB.title = r.SemiAutoPickupEnabled ? "Collects when you press the creature pickup keys" : "This creature has no semi-automatic pickup"; }
    const mouseKey = keyLabel(KeybindOptions.CreaturePickup), areaKey = keyLabel(KeybindOptions.CreatureAutoPickup);
    setText(this.modeHint, c.petMode === PM.Automatic
      ? "Collects everything that matches the filter around it, on its own."
      : `Collects around you on ${areaKey || "the auto pickup key"}, or the item under the mouse on ${mouseKey || "the pickup key"}.`);
    const rule = (on: boolean, value: string, what: string) => h("div", on ? "" : "off", h("b", "", on ? value : "—"), what);
    const cells = (n: number) => `${n}×${n} cells`;
    this.rulesEl.replaceChildren(
      rule(r.AutoPickupEnabled, cells(r.AutoPickupRange), "automatic range"),
      rule(r.SemiAutoPickupEnabled, cells(r.SemiAutoPickupRange), "semi-auto range"),
      rule(r.MousePickupEnabled, cells(r.MousePickupRange), "mouse pickup range"),
      rule(true, fmt(r.MinimalFullness), "fullness it needs to work"));
    const flags = itemFilterFlags(c.Filter);
    this.filterChips.set(flags.flatMap((on, i) => (on ? [i] : [])));
    setText(this.filterCount, flags[0] ? "everything" : plural(flags.filter(Boolean).length, "kind"));
    const g = String(c.Filter.PickupGrade);
    if (this.gradeSel.value !== g) this.gradeSel.value = g;
    this.gradeSel.style.color = c.Filter.PickupGrade ? gradeVar(c.Filter.PickupGrade) : "";
  }

  /** Feed: only for the creature that is out, with a food in the bag. */
  private refreshFeed(): void {
    const c = this.cur();
    if (!c) return;
    const out = this.isOut(c), f = out ? this.food() : null;
    this.feedBtn.el.hidden = !out;
    if (!out) return;
    if (c.Fullness >= MAX_FULL) this.feedBtn.setDisabled(true, "It is full");
    else if (!f) this.feedBtn.setDisabled(true, "No creature food in your bag (Fairy Moss, Freshwater Clam, Mackerel, Cherry)");
    else { this.feedBtn.setDisabled(false); this.feedBtn.el.title = `Feed it ${f.info.Name} (+${fmt(f.info.Effect * 100)} fullness)`; }
  }

  // — Animation (UI ticker, 100 ms, shown only) —

  /** CreatureButton.SetCreatureFrames / DrawCreatureAnimation: the default loop, the extra loop once every 8 s. */
  private animate(now: number): void {
    const c = this.cur();
    if (c) {
      const f = creatureFrames(c.PetType);
      let idx = f.defIdx;
      if (!this.still) {
        if (!this.extra && now >= this.nextExtra && f.exCount > 1) { this.extra = true; this.exStart = now; }
        if (this.extra) {
          const step = Math.floor((now - this.exStart) / Math.max(1, f.exDelay));
          if (step >= f.exCount) { this.extra = false; this.nextExtra = now + EXTRA_EVERY; } else idx = f.exIdx + step;
        }
        if (!this.extra) idx = f.defIdx + (Math.floor(now / Math.max(1, f.defDelay)) % Math.max(1, f.defCount));
      }
      if (idx !== this.spriteIdx && this.paint(this.sprite, idx)) this.spriteIdx = idx;
    }
    for (const k of this.cards) {
      const f = creatureFrames(k.type);
      const idx = this.still ? f.defIdx : f.defIdx + (Math.floor(now / Math.max(1, f.defDelay)) % Math.max(1, f.defCount));
      if (idx !== k.lastIdx && this.paint(k.sprite, idx)) k.lastIdx = idx;
    }
  }

  /** Sets a Prguse2 frame as the element's background; false while it is still loading (asked for, painted next tick). */
  private paint(el: HTMLElement, idx: number): boolean {
    const p = libPictureNow(Lib.Prguse2, idx);
    if (p === undefined) { void libPicture(Lib.Prguse2, idx); return false; }
    el.style.backgroundImage = p ? `url(${p.url})` : "";
    return true;
  }
}

/** GameScene.NewIntelligentCreature: name the new creature (a popup input; Cancel keeps the given name). */
async function nameNewCreature(host: ModernUiHost, cr: Creatures, type: number, given: string): Promise<void> {
  const v = await host.rt.popups.input({ title: "New creature", text: "Please give your creature a name.", value: given,
    min: 3, max: 15, chars: "A-Za-z0-9", check: nameProblem });
  if (v === null || host.rt.signal.aborted || nameProblem(v)) return;
  const c = cr.list.find((x) => x.PetType === type);
  if (!c) return;
  sendUpdate(host, { ...c, CustomName: v });
  c.CustomName = v; // no answer for a plain update (header)
  stateOf(cr).changed.emit();
}

registerWindow(CREATURES_WINDOW, (host) => new CreaturesWindow(host), {
  // Wired at UI start (works while the window is closed / unbuilt): the new-creature naming prompt, the rename grant
  // and the rare-find count.
  watch: (host) => {
    const cr = host.ctx.models.creatures, st = stateOf(cr);
    const offChanged = cr.changed.on((ch) => {
      if (ch.kind === "enableRename") { st.renameEnabled = true; st.changed.emit(); }
      else if (ch.kind === "new") {
        const c = cr.list[ch.index];
        if (c) void nameNewCreature(host, cr, c.PetType, c.CustomName);
      }
    });
    const offPackets = host.packets.on((p) => { if (p.type === "IntelligentCreaturePickup") { st.rareFinds++; st.changed.emit(); } });
    return () => { offChanged(); offPackets(); };
  },
});
