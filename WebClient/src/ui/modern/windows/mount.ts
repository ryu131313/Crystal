// Ported from play/hudMount.ts (MountMock): the new UI's mount window, wired to the real game (the desktop's MountDialog,
// src/ui/mountWindow.ts is the classic reference). The equipped mount (EquipmentSlot.Mount) animated big on a pedestal
// (Prguse StartIndex + MountType * 20, 16 frames at 100 ms: StartIndex 1330 for 5-slot mounts, 1170 for 4-slot), its gear
// slots placed round it (Reins / Bells / Saddle / Ribbon / Mask; 4-slot mounts have no Mask), Ride / Dismount, the
// loyalty bar (CurrentDura / MaxDura) with Restore, and a side panel: the stats the fitted gear gives, the bag items that
// fit the picked slot, the food in the bag and the other mounts in the bag.
// Changes from the mock:
//  - opens only with a mount type (User.MountType >= 0), else the desktop's "You do not own a mount." note
//    (MountDialog.Show / ClientTextKeys.NoMount); closes when S.MountUpdate says the mount is gone (MountType < 0);
//  - the gear slots are core/itemCells.ts cells on the "mount" grid (index = MountSlot): pick / drag / drop / right-click
//    through core/drag.ts + dragRoutes.ts (C.EquipSlotItem / C.RemoveSlotItem / C.MergeItem via ItemChanges, the cell
//    locked until the server answers). Fitting from the gear list and the take-off cross go through the same model calls
//    (ItemChanges.useSlotItem / removeSlotItem) on the drag controller's pacer;
//  - Ride / Dismount = MountState.ride (chat "@ride", the same call as the M key in the input router), enabled by
//    MountState.canRide; the label follows User.RidingMount;
//  - Restore = C.UseItem of a Food item in the bag (Inventory.use): the server raises the mount's loyalty by the food's
//    durability (Shape 0 food also lowers the max loyalty, PlayerObject UseItem case Food) and answers S.ItemRepaired;
//  - "Equip" on another Mount item in the bag = Inventory.use (C.EquipItem into the Mount slot); there is one mount slot,
//    so the mock's stable becomes that list;
//  - the frames' bounds come from per-axis Uint32Array histograms (percentiles from the cumulative count: no sort, no
//    big arrays, Performance.md T4-3) and the 16 frames load and decode in parallel; no blobs are made: each frame is a
//    positioned child of one scaled box, and the animation shows one child at a time (16 visibility flips, no image
//    swaps); a 100 ms ticker step while shown, frame 0 only with prefers-reduced-motion.
// Hidden (no system in Crystal): the mock's gear effect names and values (the real ItemInfo / added stats of the fitted
// items are shown instead), the "Mount attack" total, mount prices / worth, riding a mount that is not equipped.
// Hotkeys: the registry default (MENU_WINDOWS: KeybindOptions.MountWindow, J) through the input router; M (Mount) rides
// in the router's gameplay layer (modernUi.ts), not here.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { EquipmentSlot, ItemType, MirGridType, MountSlot, StatName } from "../../../protocol/enums.generated.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { Lib, libPicture, type LibPicture } from "../../mir/controls.ts";
import { UiWindow } from "../core/wm.ts";
import { asButton, h, injectCss, reducedMotion, setStyle, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { fmt, plural, pretty } from "../core/format.ts";
import { button, type Btn } from "../core/widgets.ts";
import { gradeVar } from "../core/styles.ts";
import type { CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

export const MOUNT_KEY = "mount";

/** [MountSlot, name, the bag ItemType that fits it, empty-slot icon]. */
type SlotDef = readonly [number, string, number, IconName | string];
const SLOTS: readonly SlotDef[] = [
  [MountSlot.Reins, "Reins", ItemType.Reins, "M4 12c4-6 12-6 16 0M4 12c4 6 12 6 16 0M12 6v12"],
  [MountSlot.Bells, "Bells", ItemType.Bells, "M12 3a5 5 0 015 5v5l2 3H5l2-3V8a5 5 0 015-5zM10 19a2 2 0 004 0"],
  [MountSlot.Saddle, "Saddle", ItemType.Saddle, "M3 14c3-1 5-6 9-6s6 5 9 6l-2 4H5zM12 8V5"],
  [MountSlot.Ribbon, "Ribbon", ItemType.Ribbon, "M12 10l-6-6v8zM12 10l6-6v8zM12 10l-4 10M12 10l4 10"],
  [MountSlot.Mask, "Mask", ItemType.Mask, "M4 8c3-4 13-4 16 0-1 7-5 9-8 9s-7-2-8-9zM8 11h2M14 11h2"],
];
/** Gear slot positions round the arena (px in the arena). */
const SLOT_POS: readonly (readonly [number, number])[] = [[12, 14], [322, 14], [12, 132], [322, 132], [167, 196]];
/** The arena's size (CSS .arena) and the mount box inside it (the frames are scaled to fit this). */
const ARENA_W = 380, ARENA_H = 260, BOX_W = 260, BOX_H = 190;
/** MountImage: 16 frames at 100 ms (MirAnimated AnimationCount / AnimationDelay). */
const FRAMES = 16, FRAME_MS = 100;

const CSS = `
.hudm .mtwin { --winh: 560px; width: 740px; }
.hudm .mtwin .panel { height: var(--winh); display: flex; flex-direction: column; box-sizing: border-box; }
.hudm .mtwin .mbody { flex: 1; min-height: 0; display: flex; }
.hudm .mtwin .lbl { display: flex; justify-content: space-between; margin-bottom: 4px; }
.hudm .mtwin .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
/* Showcase */
.hudm .mtwin .show { width: ${ARENA_W + 24}px; flex: none; box-sizing: border-box; display: flex; flex-direction: column; align-items: center; padding: 12px; gap: 8px;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mtwin .arena { position: relative; width: ${ARENA_W}px; height: ${ARENA_H}px; flex: none; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(ellipse 60% 50% at 50% 70%, color-mix(in srgb, var(--ac) 25%, transparent), transparent 70%), linear-gradient(180deg, color-mix(in srgb, var(--pb) 60%, #000), color-mix(in srgb, var(--pb) 90%, #000)); }
.hudm .mtwin .arena::before { content: ""; position: absolute; left: 50%; bottom: 30px; width: 230px; height: 46px; transform: translateX(-50%); border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%); }
.hudm .mtwin .arena::after { content: ""; position: absolute; left: 50%; bottom: 42px; width: 200px; height: 22px; transform: translateX(-50%); border-radius: 50%;
  border: 1px solid color-mix(in srgb, var(--ac) 60%, transparent); animation: hudm-mt-ring 2.5s ease-out infinite; }
@keyframes hudm-mt-ring { 0% { transform: translateX(-50%) scale(.7); opacity: .9; } 100% { transform: translateX(-50%) scale(1.3); opacity: 0; } }
.hudm .mtwin .mbox { position: absolute; left: ${(ARENA_W - BOX_W) / 2}px; top: ${ARENA_H - BOX_H - 40}px; width: ${BOX_W}px; height: ${BOX_H}px; z-index: 1; pointer-events: none; }
.hudm .mtwin .arena.riding .mbox { animation: hudm-mt-bob .5s ease-in-out infinite; }
@keyframes hudm-mt-bob { 50% { transform: translateY(-3px); } }
.hudm .mtwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; overflow: hidden; }
.hudm .mtwin .org > div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; visibility: hidden; }
.hudm .mtwin .org > div.on { visibility: visible; }
.hudm .mtwin .gs { position: absolute; z-index: 2; display: flex; flex-direction: column; align-items: center; gap: 2px; cursor: pointer; }
.hudm .mtwin .gs .icell { --cs: 46px; border-radius: 50%; }
.hudm .mtwin .gs .icell::after { border-radius: 50%; }
.hudm .mtwin .gs .icell .ic { width: 18px; height: 18px; }
.hudm .mtwin .gs > span { font-size: 9px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); text-shadow: 0 1px 2px #000; }
.hudm .mtwin .gs.pick .icell { border-color: var(--ac); }
.hudm .mtwin .gs.pick > span { color: var(--ac); }
.hudm .mtwin .gs .xo { position: absolute; right: -4px; top: -4px; z-index: 3; width: 16px; height: 16px; padding: 0; border: 0; border-radius: 50%; display: none; align-items: center;
  justify-content: center; font: inherit; font-size: 11px; line-height: 1; color: #fff; background: var(--bad); cursor: pointer; }
.hudm .mtwin .gs.has:hover .xo, .hudm .mtwin .gs.has .xo:focus-visible { display: flex; }
.hudm .mtwin .nm { text-align: center; min-height: 34px; } .hudm .mtwin .nm h3 { margin: 0; font-size: 18px; color: #fff; } .hudm .mtwin .nm small { font-size: 11px; color: var(--dim); }
.hudm .mtwin .loy { width: 100%; }
.hudm .mtwin .loy .bar2 { height: 10px; border-radius: 5px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .mtwin .loy .bar2 i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, #8a2a5a, #ff6aa8); transition: width .5s; }
.hudm .mtwin .row2 { display: flex; gap: 6px; width: 100%; }
.hudm .mtwin .row2 .btn { flex: 1; }
.hudm .mtwin .row2 .btn.hb { flex: none; width: 30px; padding: 0; }
/* Side */
.hudm .mtwin .side { flex: 1; min-width: 0; padding: 12px; display: flex; flex-direction: column; gap: 8px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mtwin .tot { display: flex; flex-wrap: wrap; gap: 4px; }
.hudm .mtwin .tot span { padding: 3px 7px; font-size: 11px; color: var(--dim); border-radius: calc(var(--rad) * .6); background: color-mix(in srgb, var(--frl) 7%, transparent); }
.hudm .mtwin .tot b { color: var(--ac); font-weight: 700; }
.hudm .mtwin .gl { display: flex; align-items: center; gap: 8px; padding: 4px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid transparent; }
.hudm .mtwin .gl:hover, .hudm .mtwin .gl:focus-visible { border-color: var(--ac); }
.hudm .mtwin .gl.no { cursor: default; opacity: .6; } .hudm .mtwin .gl.no:hover { border-color: transparent; }
.hudm .mtwin .gl .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .mtwin .gl .t b { display: block; color: var(--gc, #fff); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .mtwin .gl .t small { display: block; color: var(--dim); font-size: 10px; }
.hudm .mtwin .gl .t small.warn { color: var(--warn); }
.hudm .mtwin .note { font-size: 11px; color: var(--dim); }
.hudm .mtwin .empty-note { padding: 8px; }
.hudm.narrow .mtwin { width: 700px; }
.hudm.narrow .mtwin .side { padding: 10px 8px; }
`;

/** One mount's frames, laid out in the box of their opaque pixels: x0 / y0 = that box's origin in frame coordinates. */
interface MountFrames { readonly pics: readonly (LibPicture | null)[]; readonly x0: number; readonly y0: number; readonly w: number; readonly h: number }

/** Frames by start index (page-level like libPicture's own cache; a failed load is retried on the next open). */
const framesCache = new Map<number, Promise<MountFrames | null>>();
function mountFrames(start: number): Promise<MountFrames | null> {
  let p = framesCache.get(start);
  if (!p) {
    p = loadFrames(start).then((r) => { if (!r) framesCache.delete(start); return r; }, () => { framesCache.delete(start); return null; });
    framesCache.set(start, p);
  }
  return p;
}

/** Loads and decodes the 16 frames in parallel, then finds where the mount is (the 4-slot frames are mostly empty canvas
 *  with the mount in one corner): the 0.5 - 99.5 percentile of every frame's opaque pixels per axis, so the lone opaque
 *  pixels some frames carry in their corners do not stretch the box. Per-axis Uint32Array histograms, percentiles from
 *  the cumulative count (Performance.md T4-3). */
async function loadFrames(start: number): Promise<MountFrames | null> {
  const pics = await Promise.all(Array.from({ length: FRAMES }, (_, i) => libPicture(Lib.Prguse, start + i)));
  const have = pics.filter((p): p is LibPicture => !!p);
  if (!have.length) return null;
  const imgs = await Promise.all(have.map(async (p) => { const im = new Image(); im.src = p.url; await im.decode().catch(() => {}); return im; }));
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const p of have) { minX = Math.min(minX, p.x); minY = Math.min(minY, p.y); maxX = Math.max(maxX, p.x + p.width); maxY = Math.max(maxY, p.y + p.height); }
  const hx = new Uint32Array(maxX - minX), hy = new Uint32Array(maxY - minY);
  let total = 0;
  const c = document.createElement("canvas"), g = c.getContext("2d", { willReadFrequently: true });
  if (!g) return null;
  have.forEach((p, n) => {
    c.width = p.width; c.height = p.height;
    g.clearRect(0, 0, p.width, p.height);
    g.drawImage(imgs[n]!, 0, 0);
    const d = g.getImageData(0, 0, p.width, p.height).data;
    const ox = p.x - minX, oy = p.y - minY;
    for (let y = 0, i = 3; y < p.height; y++) {
      let row = 0;
      for (let x = 0; x < p.width; x++, i += 4) if (d[i]! > 8) { hx[ox + x]!++; row++; }
      hy[oy + y]! += row;
      total += row;
    }
  });
  if (!total) return null;
  /** The bin where the cumulative count first passes `f` of the total. */
  const pct = (hist: Uint32Array, f: number): number => {
    const target = Math.floor(total * f);
    let cum = 0;
    for (let i = 0; i < hist.length; i++) { cum += hist[i]!; if (cum > target) return i; }
    return hist.length - 1;
  };
  const x0 = minX + pct(hx, 0.005) - 4, x1 = minX + pct(hx, 0.995) + 4;
  const y0 = minY + pct(hy, 0.005) - 4, y1 = minY + pct(hy, 0.995) + 4;
  return { pics, x0, y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

/** The stat label (StatName, spaced). */
const statText = (k: number): string => pretty(StatName[k] ?? `Stat ${k}`);

export class MountWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly arena: HTMLElement;
  private readonly mbox: HTMLElement;
  private org: HTMLElement;
  /** The drawn mount (start index) and the frame shown. */
  private start = -1;
  private frame = 0;
  private frameEls: HTMLElement[] = [];
  private readonly slotEls: HTMLElement[] = [];
  private readonly slotCells: ItemCell[] = [];
  private readonly nameEl = h("h3");
  private readonly subEl = h("small");
  private readonly loyText = h("b");
  private readonly loyBar = h("i");
  private readonly rideBtn: Btn;
  private readonly restoreBtn: Btn;
  private readonly side = h("div", "side");
  /** The gear slot the side list shows. */
  private slot: number = MountSlot.Reins;
  /** The side panel's content key (rebuilt only when it changes) and its cells' scope. */
  private sideKey = "";
  private sideScope: AbortController | null = null;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "mtwin", 60, 60);
    injectCss("win-mount", CSS);
    this.m = ui.ctx.models;

    this.org = h("div", "org");
    this.mbox = h("div", "mbox", this.org);
    this.arena = h("div", "arena", this.mbox);
    for (let i = 0; i < SLOTS.length; i++) {
      const [slot, name, , icon] = SLOTS[i]!;
      const cell = ui.cells.cell("mount", slot, { size: 46, empty: svg(icon) });
      const x = h("button", "xo", "×");
      x.type = "button";
      x.title = "Take it off";
      x.setAttribute("aria-label", `Take off the ${name.toLowerCase()}`);
      this.listen(x, "click", (e) => { e.stopPropagation(); this.takeOff(slot); });
      const gs = h("div", "gs", cell.el, h("span", "", name), x);
      gs.style.left = SLOT_POS[i]![0] + "px";
      gs.style.top = SLOT_POS[i]![1] + "px";
      this.listen(gs, "click", () => { if (this.slot !== slot) { this.slot = slot; this.invalidate(); } });
      this.slotEls.push(gs);
      this.slotCells.push(cell);
      this.arena.append(gs);
    }

    this.rideBtn = button("Ride", () => { if (this.m.mount.canRide()) this.m.mount.ride(); }, { cls: "main big", icon: "horse" });
    this.restoreBtn = button("Restore loyalty", () => this.restore(), { cls: "big", icon: "heart" });
    const help = asButton(h("div", "btn big hb", svg("help")), "Help: Mounts");
    help.title = "Help: Mounts";
    this.listen(help, "click", () => { ui.help("Mounts"); });

    const show = h("div", "show", this.arena, h("div", "nm", this.nameEl, this.subEl),
      h("div", "loy", h("div", "lbl", "Loyalty", this.loyText), h("div", "bar2", this.loyBar)),
      h("div", "row2", this.rideBtn.el, this.restoreBtn.el, help));
    this.el.append(h("div", "panel", this.titleBar("MOUNT", "Close"), h("div", "mbody", show, this.side)));
    this.el.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: this.signal });

    const off = [
      this.m.inventory.subscribe(() => this.invalidate()),
      // GameScene.MountUpdate (the user's part): MountType < 0 closes the window, else a refresh
      this.m.mount.updated.on(({ mountType }) => { if (mountType < 0) this.toggle(false); else this.invalidate(); }),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
    ];
    this.signal.addEventListener("abort", () => { for (const f of off) f(); this.sideScope?.abort(); }, { once: true });
    // The user's action (canRide: standing) and riding flag have no events: re-read while shown (writes are diffed).
    this.everyShown(250, () => this.renderRide());
    // MountImage: the next frame every 100 ms while shown (not with reduced motion).
    this.everyShown(FRAME_MS, () => this.step());
  }

  /** MountDialog.Show: refused with NoMount without a mount type (the desktop's message box). */
  override toggle(show = !this.open): void {
    if (show && !this.open && (this.m.mount.user()?.mountType ?? -1) < 0) {
      void this.ui.rt.popups.confirm({ title: "Mount", text: "You do not own a mount.", kind: "info", buttons: "ok" });
      return;
    }
    super.toggle(show);
  }

  /** Drops the side lists' cells while closed (rebuilt on the next open). */
  protected override closed(): void { this.sideScope?.abort(); this.sideScope = null; this.sideKey = ""; this.invalidate(); }

  private mountItem(): UserItem | null { return this.m.inventory.equipment[EquipmentSlot.Mount] ?? null; }
  private info(item: UserItem | null | undefined): ItemInfo | undefined { return item ? this.m.itemInfos.get(item.ItemIndex) : undefined; }
  private nameOf(item: UserItem, info: ItemInfo | undefined): string {
    return info ? (itemFriendlyName(info) || info.Name) + (item.Count > 1 ? ` (${item.Count})` : "") : `Item #${item.ItemIndex}`;
  }

  protected override render(): void {
    const mount = this.mountItem(), info = this.info(mount);
    const u = this.m.mount.user();
    const mountType = u?.mountType ?? -1;
    const slots = mount?.Slots ?? [];
    if (mount && !info) this.m.gameInfo.requestItem(mount.ItemIndex);
    for (const s of slots) if (s && !this.info(s)) this.m.gameInfo.requestItem(s.ItemIndex); // GameScene.Bind
    const four = slots.length === 4;
    if (four && this.slot === MountSlot.Mask) this.slot = MountSlot.Reins;

    // Name, gear slots
    setText(this.nameEl, mount ? this.nameOf(mount, info) : "No mount equipped");
    setText(this.subEl, mount ? `${plural(slots.length, "gear slot")}` : "Equip a mount from your bag");
    setStyle(this.nameEl, "color", info ? gradeVar(info.Grade) : "");
    SLOTS.forEach(([slot], i) => {
      const gs = this.slotEls[i]!;
      gs.hidden = !mount || slot >= slots.length;
      gs.classList.toggle("pick", slot === this.slot);
      gs.classList.toggle("has", !!slots[slot]);
    });

    // Loyalty
    const cur = mount?.CurrentDura ?? 0, max = mount?.MaxDura ?? 0;
    setText(this.loyText, mount ? `${fmt(cur)} / ${fmt(max)}` : "");
    setStyle(this.loyBar, "width", max > 0 ? `${Math.min(100, (cur / max) * 100).toFixed(1)}%` : "0%");
    const food = this.foods();
    if (!mount) this.restoreBtn.setDisabled(true, "No mount equipped");
    else if (cur >= max) this.restoreBtn.setDisabled(true, "Its loyalty is full");
    else if (!food.length) this.restoreBtn.setDisabled(true, "You have no mount food in your bag");
    else this.restoreBtn.setDisabled(false);

    // The picture (MountDialog.DrawMountAnimation)
    this.setMount(mount && mountType >= 0 ? (four ? 1170 : 1330) + mountType * 20 : -1);
    this.renderRide();
    this.renderSide(mount, slots, food);
  }

  /** Ride / Dismount (MountDialog.CanRide) and the riding bob. */
  private renderRide(): void {
    const u = this.m.mount.user();
    const riding = !!u?.ridingMount;
    this.rideBtn.setLabel(riding ? "Dismount" : "Ride");
    if (this.m.mount.canRide()) this.rideBtn.setDisabled(false);
    else this.rideBtn.setDisabled(true, !u || u.mountType < 0 ? "No mount" : "Stand still to ride or dismount");
    this.arena.classList.toggle("riding", riding);
  }

  // — The mount picture —

  private setMount(start: number): void {
    if (start === this.start) return;
    this.start = start;
    const org = h("div", "org"); // a fresh layer: frames still loading for the old mount land in the detached one
    this.org.replaceWith(org);
    this.org = org;
    this.frameEls = [];
    this.frame = 0;
    if (start < 0) return;
    void mountFrames(start).then((f) => {
      if (!f || org !== this.org) { if (!f && org === this.org) this.start = -1; return; }
      const k = Math.min(BOX_W / f.w, BOX_H / f.h, 2.5);
      org.style.width = f.w + "px";
      org.style.height = f.h + "px";
      org.style.transform = `scale(${k})`;
      org.style.left = (BOX_W - f.w * k) / 2 + "px";
      org.style.top = BOX_H - f.h * k + "px";
      const els: HTMLElement[] = [];
      for (const p of f.pics) {
        if (!p) continue;
        const d = h("div");
        Object.assign(d.style, { left: p.x - f.x0 + "px", top: p.y - f.y0 + "px", width: p.width + "px", height: p.height + "px", backgroundImage: `url(${p.url})` });
        els.push(d);
      }
      els[0]?.classList.add("on");
      org.append(...els);
      this.frameEls = els;
    });
  }

  /** The next animation frame (the ticker step while shown). */
  private step(): void {
    const els = this.frameEls;
    if (els.length < 2 || reducedMotion()) return;
    els[this.frame]?.classList.remove("on");
    this.frame = (this.frame + 1) % els.length;
    els[this.frame]!.classList.add("on");
  }

  // — Actions (server authoritative: the model sends and locks the cell; the answer redraws) —

  /** The take-off cross: ItemChanges.removeSlotItem (MirItemCell.RemoveItem), refusals said up front. */
  private takeOff(slot: number): void {
    const item = this.mountItem()?.Slots[slot] ?? null;
    if (!item) return;
    const u = this.m.mount.user();
    if (u?.fishing) { this.ui.drag.toast("Not while fishing"); return; }
    if (u?.ridingMount) { this.ui.drag.toast("Dismount first"); return; }
    if (!this.m.inventory.inventory.some((i) => !i)) { this.ui.drag.toast("Your bag is full"); return; }
    const ref: CellRef = { grid: "mount", index: slot };
    this.ui.drag.request(() => this.m.itemChanges.removeSlotItem(MirGridType.Mount, slot), [ref]);
  }

  /** A gear row: ItemChanges.useSlotItem (MirItemCell.UseSlotItem: C.EquipSlotItem into the item type's slot). */
  private fit(index: number, uid: bigint): void {
    const inv = this.m.inventory;
    this.ui.drag.request(() => {
      const i = inv.inventory.findIndex((it) => it?.UniqueID === uid); // re-read when the pacer runs
      if (i >= 0) this.m.itemChanges.useSlotItem(i);
    }, [{ grid: "inv", index }]);
  }

  /** Restore / a food row: C.UseItem of the food (Inventory.use). */
  private feed(index: number, uid: bigint): void {
    const inv = this.m.inventory;
    this.ui.drag.request(() => {
      const i = inv.inventory.findIndex((it) => it?.UniqueID === uid);
      if (i >= 0) inv.use(i, this.ui.ctx.now());
    }, [{ grid: "inv", index }]);
  }

  /** The Restore button: the first food that keeps the max loyalty (Shape 1), else the first food. */
  private restore(): void {
    const mount = this.mountItem();
    if (!mount || mount.CurrentDura >= mount.MaxDura) return;
    const food = this.foods();
    const pick = food.find((f) => f.info.Shape !== 0) ?? food[0];
    if (pick) this.feed(pick.index, pick.item.UniqueID);
  }

  /** Food items in the bag (unlocked), bag order. */
  private foods(): { index: number; item: UserItem; info: ItemInfo }[] {
    const inv = this.m.inventory, out: { index: number; item: UserItem; info: ItemInfo }[] = [];
    inv.inventory.forEach((item, index) => {
      const info = this.info(item);
      if (item && info && info.Type === ItemType.Food && !inv.isLocked(item)) out.push({ index, item, info });
    });
    return out;
  }

  // — The side panel: gear stats, the gear list for the picked slot, food, other mounts —

  private renderSide(mount: UserItem | null, slots: readonly (UserItem | null)[], food: { index: number; item: UserItem; info: ItemInfo }[]): void {
    const inv = this.m.inventory;
    const def = SLOTS.find((d) => d[0] === this.slot)!;
    const fits: number[] = [], mounts: number[] = [];
    inv.inventory.forEach((it, i) => {
      const t = this.info(it)?.Type;
      if (t === def[2]) fits.push(i);
      else if (t === ItemType.Mount) mounts.push(i);
    });
    const ids = (list: readonly number[]) => list.map((i) => `${i}:${inv.inventory[i]!.UniqueID}:${inv.inventory[i]!.Count}`).join(",");
    const slotIds = slots.map((s) => s ? `${s.UniqueID}` : "-").join(",");
    const key = `${mount?.UniqueID ?? ""}|${mount ? mount.CurrentDura >= mount.MaxDura : ""}|${!!this.m.mount.user()?.ridingMount}|${this.slot}|${slotIds}|${ids(fits)}|${ids(mounts)}|${ids(food.map((f) => f.index))}|${slots.map((s) => s && this.info(s) ? 1 : 0).join("")}`;
    if (key === this.sideKey) return;
    this.sideKey = key;
    this.sideScope?.abort();
    const scope = this.sideScope = new AbortController();
    const sig = scope.signal;
    const kids: (Node | string)[] = [];

    // What the fitted gear gives (ItemInfo.Stats + the items' added stats)
    const sum = new Map<number, number>();
    for (const s of slots) {
      const si = this.info(s);
      if (!s || !si) continue;
      for (const [k, v] of Object.entries(si.Stats)) if (v) sum.set(Number(k), (sum.get(Number(k)) ?? 0) + v);
      for (const [k, v] of Object.entries(s.AddedStats)) if (v) sum.set(Number(k), (sum.get(Number(k)) ?? 0) + v);
    }
    kids.push(h("div", "lbl", "Gear bonuses"));
    kids.push(sum.size
      ? h("div", "tot", ...[...sum].map(([k, v]) => h("span", "", `${statText(k)} `, h("b", "", v > 0 ? `+${fmt(v)}` : fmt(v)))))
      : h("div", "note", "The fitted gear adds no stats."));

    // The picked slot's gear in the bag
    const current = slots[this.slot] ?? null;
    const row = (cell: ItemCell, item: UserItem, info: ItemInfo | undefined, line: string, warn: string, act: (() => void) | null): HTMLElement => {
      const name = h("b", "", this.nameOf(item, info));
      if (info) name.style.setProperty("--gc", gradeVar(info.Grade));
      const r = h("div", "gl" + (act ? "" : " no"), cell.el, h("div", "t", name, h("small", warn ? "warn" : "", warn || line)));
      if (act) { asButton(r); r.addEventListener("click", act, { signal: sig }); }
      return r;
    };
    kids.push(h("div", "lbl", `${def[1]} · in your bag`, h("b", "", String(fits.length))));
    if (!mount) kids.push(h("div", "note", "Equip a mount to fit gear."));
    else {
      const riding = !!this.m.mount.user()?.ridingMount;
      for (const i of fits) {
        const it = inv.inventory[i]!, ii = this.info(it);
        const statLine = ii ? Object.entries(ii.Stats).filter(([, v]) => v).map(([k, v]) => `${statText(Number(k))} ${v > 0 ? "+" : ""}${v}`).join(", ") : "";
        const why = current ? `Take off the fitted ${def[1].toLowerCase()} first` : riding ? "Dismount first" : "";
        kids.push(row(this.ui.cells.cell("inv", i, { size: 36, readOnly: true, signal: sig }), it, ii, statLine || "Click to fit it", why,
          why ? null : () => this.fit(i, it.UniqueID)));
      }
      if (!fits.length) kids.push(h("div", "empty-note", `No ${def[1].toLowerCase()} in your bag.`));
    }

    // Food (Restore)
    if (mount) {
      kids.push(h("div", "lbl", "Food · restores loyalty", h("b", "", String(food.length))));
      const full = mount.CurrentDura >= mount.MaxDura;
      for (const f of food) {
        const line = `+${fmt(f.item.CurrentDura)} loyalty` + (f.info.Shape === 0 ? " · lowers its max loyalty" : "");
        kids.push(row(this.ui.cells.cell("inv", f.index, { size: 36, readOnly: true, signal: sig }), f.item, f.info, line, full ? "Its loyalty is full" : "",
          full ? null : () => this.feed(f.index, f.item.UniqueID)));
      }
      if (!food.length) kids.push(h("div", "empty-note", "No mount food in your bag."));
    }

    // Other mounts in the bag (one Mount slot: equipping swaps)
    if (mounts.length) {
      kids.push(h("div", "lbl", "Other mounts in your bag", h("b", "", String(mounts.length))));
      for (const i of mounts) {
        const it = inv.inventory[i]!, ii = this.info(it);
        kids.push(row(this.ui.cells.cell("inv", i, { size: 36, readOnly: true, signal: sig }), it, ii,
          `${plural(it.Slots.length, "gear slot")} · loyalty ${fmt(it.CurrentDura)} / ${fmt(it.MaxDura)} · click to equip`, "",
          () => this.ui.drag.request(() => {
            const n = inv.inventory.findIndex((x) => x?.UniqueID === it.UniqueID);
            if (n >= 0) inv.use(n, this.ui.ctx.now());
          }, [{ grid: "inv", index: i }])));
      }
    }
    this.side.replaceChildren(...kids);
  }
}

registerWindow(MOUNT_KEY, (host) => new MountWindow(host));
