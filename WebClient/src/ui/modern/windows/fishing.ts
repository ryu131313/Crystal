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
//  - the bite is shown without a countdown (the server sends no bite deadline).
// Hidden (no system in Crystal): the catch card (size, rarity, "new record"), the lost card, the fish journal and
// session stats, fish shadows for the Finder, the reaction timing ring, the mock's part effect texts and chance formula
// (the parts' real ItemInfo stats and the server's ChancePercent are shown).
// Hotkeys: the registry default (MENU_WINDOWS: KeybindOptions.Fishing, N) through the input router.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { EquipmentSlot, FishingSlot, StatName } from "../../../protocol/enums.generated.ts";
import { fishingRod } from "../../../game/fishing.ts";
import { canFish } from "../../../game/riding.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { UiWindow } from "../core/wm.ts";
import { asButton, h, injectCss, setStyle, setText } from "../core/dom.ts";
import { svg, svgNode, type IconName } from "../core/icons.ts";
import { fmt, pretty } from "../core/format.ts";
import { button, toggleSwitch, type Btn } from "../core/widgets.ts";
import { gradeVar } from "../core/styles.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

export const FISHING_KEY = "fishing";

/** [FishingSlot, name, what it is for (FishingDialog has no text; Crystal's fishing rules), empty-slot icon]. */
type PartDef = readonly [number, string, string, IconName | string];
const PARTS: readonly PartDef[] = [
  [FishingSlot.Hook, "Hook", "Hook slot", "M14 3v10a4 4 0 01-8 0v-2l-2 2"],
  [FishingSlot.Float, "Float", "Float slot", "M12 3v4M12 7a4 4 0 110 8 4 4 0 010-8zM12 15v6"],
  [FishingSlot.Bait, "Bait", "Bait slot · used up as you fish", "M4 12c3-5 11-5 14 0-3 5-11 5-14 0zM18 12l3-3v6z"],
  [FishingSlot.Finder, "Finder", "Finder slot", "M11 4a7 7 0 110 14 7 7 0 010-14zM16 16l5 5"],
  [FishingSlot.Reel, "Reel", "Reel slot · lets you auto cast", "M12 5a7 7 0 110 14 7 7 0 010-14zM12 9a3 3 0 110 6 3 3 0 010-6zM19 12h3"],
];
/** The fishing line from the rod tip (top left) to the float: waiting / biting (constant path data). */
const LINE_WAIT = "M2 -40 Q 30 20 50 44", LINE_BITE = "M2 -40 Q 30 55 50 50";

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
.hudm .fswin .chance { margin-top: auto; padding-top: 6px; }
.hudm .fswin .cbar { height: 8px; border-radius: 4px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .fswin .cbar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, #2f9e5a, #4fe07a); }
.hudm .fswin .hint { font-size: 10px; color: var(--dim); }
/* Water scene */
.hudm .fswin .scene { position: relative; flex: 1; min-width: 0; overflow: hidden; display: flex; flex-direction: column; border-radius: 0 0 var(--rad) 0; }
.hudm .fswin .sky { height: 70px; flex: none; background: linear-gradient(180deg, #0b1a33, #1b3a5e 70%, #2a5a7e); position: relative; }
.hudm .fswin .sky::after { content: ""; position: absolute; right: 50px; top: 14px; width: 30px; height: 30px; border-radius: 50%; background: radial-gradient(circle at 40% 40%, #fffbe0, #f2d58a); }
.hudm .fswin .water { position: relative; flex: 1; overflow: hidden; background: linear-gradient(180deg, #1d4a6e 0%, #0e2a48 40%, #06152a 100%); }
.hudm .fswin .water.can { cursor: pointer; }
.hudm .fswin .water::before { content: ""; position: absolute; left: -50%; right: -50%; top: 0; height: 40px; opacity: .5;
  background: repeating-linear-gradient(90deg, transparent 0 30px, rgba(150, 210, 255, .25) 30px 34px, transparent 34px 70px); animation: hudm-fs-waves 6s linear infinite; }
@keyframes hudm-fs-waves { to { transform: translateX(70px); } }
.hudm .fswin .glint { position: absolute; width: 18px; height: 2px; border-radius: 1px; background: rgba(255, 255, 255, .5); opacity: 0; animation: hudm-fs-glint 3s ease-in-out infinite; }
@keyframes hudm-fs-glint { 0%, 100% { opacity: 0; } 50% { opacity: .9; } }
.hudm .fswin .bobber { position: absolute; left: 50%; top: 34%; width: 22px; height: 34px; margin-left: -11px; transition: left .6s cubic-bezier(.3,.6,.4,1), top .6s cubic-bezier(.3,-.4,.6,1); }
.hudm .fswin .bobber svg { width: 100%; height: 100%; }
.hudm .fswin .scene.idle .bobber { left: 8%; top: -30%; }
.hudm .fswin .scene.wait .bobber svg { animation: hudm-fs-bob 1.6s ease-in-out infinite; }
@keyframes hudm-fs-bob { 50% { transform: translateY(3px) rotate(4deg); } }
.hudm .fswin .scene.bite .bobber svg { animation: hudm-fs-dip .35s ease-in-out infinite; }
@keyframes hudm-fs-dip { 50% { transform: translateY(12px) rotate(-10deg); } }
.hudm .fswin .ripple { position: absolute; left: 50%; top: 34%; width: 40px; height: 12px; margin: 26px 0 0 -20px; border-radius: 50%; border: 2px solid rgba(180, 225, 255, .6);
  animation: hudm-fs-ripple 1.6s ease-out infinite; }
.hudm .fswin .scene.idle .ripple, .hudm .fswin .scene.idle .line { display: none; }
.hudm .fswin .scene.bite .ripple { animation-duration: .5s; border-color: #fff; }
@keyframes hudm-fs-ripple { from { transform: scale(.5); opacity: .9; } to { transform: scale(2.6); opacity: 0; } }
.hudm .fswin .line { position: absolute; left: 0; top: 0; width: 100%; height: 100%; pointer-events: none; }
.hudm .fswin .line path { fill: none; stroke: rgba(230, 240, 255, .55); stroke-width: .35; }
.hudm .fswin .hudbar { position: absolute; left: 14px; right: 14px; bottom: 12px; display: flex; flex-direction: column; gap: 6px; }
.hudm .fswin .status { text-align: center; font-size: 13px; font-weight: 700; color: #fff; text-shadow: 0 1px 3px #000; }
.hudm .fswin .scene.bite .status { color: var(--ac); animation: hudm-fs-pulse .7s ease-in-out infinite; }
@keyframes hudm-fs-pulse { 50% { opacity: .55; } }
.hudm .fswin .pbar { height: 10px; border-radius: 5px; background: rgba(0,0,0,.55); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); overflow: hidden; }
.hudm .fswin .pbar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); transition: width .3s; }
.hudm .fswin .ctrl { display: flex; gap: 8px; justify-content: center; align-items: center; flex-wrap: wrap; }
.hudm .fswin .ctrl .btn { min-width: 110px; }
.hudm .fswin .opts { display: flex; gap: 14px; justify-content: center; }
.hudm .fswin .opt { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #dbe6f5; text-shadow: 0 1px 2px #000; }
.hudm.narrow .fswin { width: 620px; }
.hudm.narrow .fswin .rod { width: 210px; padding: 10px 8px; }
`;

const statText = (k: number): string => pretty(StatName[k] ?? `Stat ${k}`);

type Phase = "idle" | "wait" | "bite";

export class FishingWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly rodName = h("b");
  private readonly rodSub = h("small");
  private readonly partRows: HTMLElement[] = [];
  private readonly partNames: HTMLElement[] = [];
  private readonly partLines: HTMLElement[] = [];
  private readonly partCells: ItemCell[] = [];
  private readonly chanceText = h("b");
  private readonly chanceBar = h("i");
  private readonly scene: HTMLElement;
  private readonly water: HTMLElement;
  private readonly linePath: SVGPathElement;
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
    for (const [slot, name, , icon] of PARTS) {
      const cell = ui.cells.cell("fishing", slot, { size: 38, empty: svg(icon) });
      const nm = h("b"), line = h("small");
      const row = h("div", "part", cell.el, h("div", "t", nm, line));
      row.title = `${name}: right-click a part in your bag to fit it, right-click here to take it off`;
      this.partRows.push(row); this.partNames.push(nm); this.partLines.push(line); this.partCells.push(cell);
      parts.append(row);
    }
    const chance = h("div", "chance", h("div", "lbl", "Catch chance", this.chanceText), h("div", "cbar", this.chanceBar),
      h("div", "hint", "From the server while you fish."));
    const rod = h("div", "rod", rodCard, h("div", "lbl", "Parts"), parts, chance);

    // Water scene
    const line = svgNode("svg", { class: "line", viewBox: "0 0 100 100", preserveAspectRatio: "none", "aria-hidden": "true" });
    this.linePath = svgNode("path", { d: LINE_WAIT });
    line.append(this.linePath);
    const bob = svgNode("svg", { viewBox: "0 0 22 34", "aria-hidden": "true" });
    bob.append(
      svgNode("path", { d: "M11 0v9", stroke: "#ddd", "stroke-width": 1.5 }),
      svgNode("ellipse", { cx: 11, cy: 15, rx: 7, ry: 7, fill: "#e83a3a" }),
      svgNode("path", { d: "M4 15a7 7 0 0014 0z", fill: "#f4f4f4" }),
      svgNode("path", { d: "M11 22v10", stroke: "#888", "stroke-width": 1.5 }),
    );
    this.water = h("div", "water");
    for (let i = 0; i < 7; i++) {
      const g = h("span", "glint");
      Object.assign(g.style, { left: 8 + i * 13 + "%", top: 8 + ((i * 29) % 60) + "%", animationDelay: i * 0.4 + "s" });
      this.water.append(g);
    }
    this.water.append(line, h("span", "ripple"), h("div", "bobber", bob));
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
    const hudbar = h("div", "hudbar", this.statusEl, h("div", "pbar", this.progressBar),
      h("div", "ctrl", this.castBtn.el, this.reelBtn.el, help),
      h("div", "opts", h("label", "opt", this.autoCast.el, "Auto cast"), h("label", "opt", this.escExit.el, "Esc exit")));
    this.water.append(hudbar);
    this.scene = h("div", "scene idle", h("div", "sky"), this.water);

    this.el.append(h("div", "panel", this.titleBar("FISHING", "Close"), h("div", "fbody", rod, this.scene)));
    this.el.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: this.signal });

    const off = [
      // PlayerObject.FishingUpdate: the dialog hides once the rod is gone, else a refresh
      f.updated.on(() => { if (!fishingRod(this.m.inventory)) this.toggle(false); else this.invalidate(); }),
      this.m.inventory.subscribe(() => { if (this.open && !fishingRod(this.m.inventory)) this.toggle(false); else this.invalidate(); }),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
    ];
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
  protected override closed(): void {
    const f = this.m.fishing;
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
    PARTS.forEach(([slot, name, hint], i) => {
      const item = slots[slot] ?? null, ii = this.info(item);
      if (item && !ii) this.m.gameInfo.requestItem(item.ItemIndex); // GameScene.Bind
      const row = this.partRows[i]!;
      row.hidden = slot >= slots.length;
      row.classList.toggle("empty", !item);
      setText(this.partNames[i]!, item ? this.nameOf(item, ii) : `No ${name.toLowerCase()}`);
      setStyle(this.partNames[i]!, "--gc", ii ? gradeVar(ii.Grade) : "");
      const stats = ii ? Object.entries(ii.Stats).filter(([, v]) => v).map(([k, v]) => `${statText(Number(k))} ${v > 0 ? "+" : ""}${fmt(v)}`).join(", ") : "";
      setText(this.partLines[i]!, item ? (slot === FishingSlot.Bait ? `${fmt(item.Count)} left` + (stats ? ` · ${stats}` : "") : stats || hint) : hint);
    });

    // FishingStatusDialog: chance, progress, phase
    const fishing = f.fishing;
    const phase: Phase = !fishing ? "idle" : f.foundFish ? "bite" : "wait";
    if (phase !== this.phase) {
      this.phase = phase;
      this.scene.className = "scene " + phase;
      this.linePath.setAttribute("d", phase === "bite" ? LINE_BITE : LINE_WAIT);
    }
    setText(this.chanceText, fishing ? `${f.chancePercent}%` : "-");
    setStyle(this.chanceBar, "width", `${fishing ? Math.max(0, Math.min(100, f.chancePercent)) : 0}%`);
    setStyle(this.progressBar, "width", `${fishing ? Math.max(0, Math.min(100, f.progressPercent)) : 0}%`);
    if (phase === "wait") setText(this.statusEl, "Waiting for a bite…");
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
