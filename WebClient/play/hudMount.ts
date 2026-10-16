// Mount window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// MountDialog: a stable of your mounts (the 12 Mount items; ItemInfo.Shape = the mount type), the equipped one animated
// big on a pedestal (Prguse StartIndex + MountType * 20, 16 frames at 100 ms like MountImage; StartIndex 1330 for 5-slot
// mounts, 1170 for 4-slot), its gear slots placed round it (Reins / Bells / Saddle / Ribbon / Mask; 4-slot mounts have no
// Mask), Ride / Dismount, the loyalty bar (CurrentDura / MaxDura) with Restore, a gear browser for the picked slot (your
// bag's compatible items with Items.Lib icons and prices) and the totals the gear gives. Item names, images and prices
// are from a copy of the live Server.MirDB (2026-10-15); slot counts per mount and gear effects are mock.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Slot = "Reins" | "Bells" | "Saddle" | "Ribbon" | "Mask";
const SLOTS: Slot[] = ["Reins", "Bells", "Saddle", "Ribbon", "Mask"];
/** name, Items.Lib image, mount type (Shape), price, slots */
type MountDef = [string, number, number, number, 4 | 5];
const MOUNTS: MountDef[] = [
  ["BengalTiger", 2040, 0, 100000, 4], ["BlueTiger", 2040, 1, 100000, 4], ["BlackLeopard", 2042, 2, 200000, 4], ["IceLeopard", 2042, 3, 200000, 4],
  ["PantheraTiger", 2044, 4, 350000, 4], ["RedTiger", 3756, 5, 350000, 4], ["WhiteTiger", 3757, 6, 350000, 4], ["Wolf", 3775, 7, 500000, 5],
  ["BrownWolf", 2116, 8, 650000, 5], ["BlueWolf", 2117, 9, 1000000, 5], ["BlackWolf", 2118, 10, 1250000, 5], ["WhiteWolf", 3776, 11, 2500000, 5],
];
/** Gear: name, image, slot, price, mock effect [stat, amount] */
type Gear = [string, number, Slot, number, string, number];
const GEAR: Gear[] = [
  ["LeatherBridle", 2060, "Reins", 60000, "Ride speed", 5], ["GoldBridle", 2061, "Reins", 400000, "Ride speed", 12],
  ["BronzeBell", 2066, "Bells", 75000, "Loyalty kept", 10], ["SilverBell", 2067, "Bells", 250000, "Loyalty kept", 20], ["GreenBell", 3760, "Bells", 600000, "Loyalty kept", 30], ["GoldBell", 3761, "Bells", 650000, "Loyalty kept", 35],
  ["Saddle", 2070, "Saddle", 50000, "AC while riding", 4],
  ["RedRibbon", 3759, "Ribbon", 150000, "HP regen", 2], ["BlueRibbon", 2064, "Ribbon", 250000, "HP regen", 4], ["BlackRibbon", 2065, "Ribbon", 500000, "HP regen", 7],
  ["LeatherMask", 2110, "Mask", 100000, "Mount attack", 3], ["WhiteMask", 2111, "Mask", 155000, "Mount attack", 5], ["BlackMask", 2112, "Mask", 400000, "Mount attack", 9],
  ["SilverMask", 2113, "Mask", 550000, "Mount attack", 12], ["TitaniumMask", 2114, "Mask", 4500000, "Mount attack", 25],
];
const SLOT_ICON: Record<Slot, string> = {
  Reins: "M4 12c4-6 12-6 16 0M4 12c4 6 12 6 16 0M12 6v12", Bells: "M12 3a5 5 0 015 5v5l2 3H5l2-3V8a5 5 0 015-5zM10 19a2 2 0 004 0",
  Saddle: "M3 14c3-1 5-6 9-6s6 5 9 6l-2 4H5zM12 8V5", Ribbon: "M12 10l-6-6v8zM12 10l6-6v8zM12 10l-4 10M12 10l4 10", Mask: "M4 8c3-4 13-4 16 0-1 7-5 9-8 9s-7-2-8-9zM8 11h2M14 11h2",
};
const pretty = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2");
const fmt = (n: number) => n.toLocaleString("en-GB");
interface Owned { def: MountDef; loyalty: number; max: number; gear: Partial<Record<Slot, Gear>> }

const CSS = `
.hudm .mtwin { --winh: 580px; width: 960px; }
.hudm .mtwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .mtwin .mbody { flex: 1; min-height: 0; display: flex; }
.hudm .mtwin .stable { width: 200px; flex: none; padding: 10px; display: flex; flex-direction: column; gap: 6px; overflow-y: auto; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mtwin .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 4px; }
.hudm .mtwin .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .mtwin .sc { position: relative; display: flex; align-items: center; gap: 8px; padding: 6px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .mtwin .sc:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .mtwin .sc.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .mtwin .sc .eq { width: 40px; height: 40px; }
.hudm .mtwin .sc b { display: block; font-size: 12px; color: #fff; } .hudm .mtwin .sc small { font-size: 10px; color: var(--dim); }
.hudm .mtwin .sc .tag { position: absolute; right: 6px; top: 6px; font-size: 8px; font-weight: 800; letter-spacing: .06em; padding: 1px 5px; border-radius: 6px; color: #fff; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
/* Showcase */
.hudm .mtwin .show { width: 420px; flex: none; display: flex; flex-direction: column; align-items: center; padding: 12px; gap: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mtwin .arena { position: relative; width: 396px; height: 300px; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(ellipse 60% 50% at 50% 70%, color-mix(in srgb, var(--ac) 25%, transparent), transparent 70%), linear-gradient(180deg, color-mix(in srgb, var(--pb) 60%, #000), color-mix(in srgb, var(--pb) 90%, #000)); }
.hudm .mtwin .arena::before { content: ""; position: absolute; left: 50%; bottom: 40px; width: 230px; height: 46px; transform: translateX(-50%); border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%); box-shadow: 0 0 30px color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .mtwin .arena::after { content: ""; position: absolute; left: 50%; bottom: 52px; width: 200px; height: 22px; transform: translateX(-50%); border-radius: 50%; border: 1px solid color-mix(in srgb, var(--ac) 60%, transparent);
  animation: hudm-ringpulse 2.5s ease-out infinite; }
@keyframes hudm-ringpulse { 0% { transform: translateX(-50%) scale(.7); opacity: .9; } 100% { transform: translateX(-50%) scale(1.3); opacity: 0; } }
.hudm .mtwin .mount { position: absolute; left: 60px; right: 60px; top: 50px; bottom: 54px; z-index: 1; background: center bottom / contain no-repeat; image-rendering: pixelated; }
.hudm .mtwin .arena.riding .mount { animation: hudm-bob .5s ease-in-out infinite; }
@keyframes hudm-bob { 50% { translate: 0 -3px; } }
.hudm .mtwin .gs { position: absolute; z-index: 2; display: flex; flex-direction: column; align-items: center; gap: 2px; cursor: pointer; }
.hudm .mtwin .gs .eq { width: 46px; height: 46px; border-radius: 50%; }
.hudm .mtwin .gs .eq::after { border-radius: 50%; }
.hudm .mtwin .gs .eq .gi { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; color: var(--dim); }
.hudm .mtwin .gs .eq .gi svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .mtwin .gs span { font-size: 9px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); text-shadow: 0 1px 2px #000; }
.hudm .mtwin .gs.pick .eq { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .mtwin .gs.pick span { color: var(--ac); }
.hudm .mtwin .gs.lock { opacity: .4; cursor: not-allowed; }
.hudm .mtwin .gs .x { position: absolute; right: -4px; top: -4px; z-index: 3; width: 16px; height: 16px; border-radius: 50%; display: none; align-items: center; justify-content: center; font-size: 11px; color: #fff; background: #a33; }
.hudm .mtwin .gs:hover .x { display: flex; }
.hudm .mtwin .nm { text-align: center; } .hudm .mtwin .nm h3 { margin: 0; font-size: 20px; color: #fff; } .hudm .mtwin .nm small { font-size: 11px; color: var(--dim); }
.hudm .mtwin .loy { width: 100%; }
.hudm .mtwin .loy .bar2 { height: 10px; border-radius: 5px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .mtwin .loy .bar2 i { display: block; height: 100%; background: linear-gradient(90deg, #8a2a5a, #ff6aa8); box-shadow: 0 0 8px rgba(255, 106, 168, .6); transition: width .5s; }
.hudm .mtwin .row2 { display: flex; gap: 6px; width: 100%; }
.hudm .mtwin .row2 .btn { flex: 1; height: 32px; justify-content: center; font-size: 13px; }
.hudm .mtwin .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .mtwin .btn.dis { opacity: .4; pointer-events: none; }
/* Right */
.hudm .mtwin .side { flex: 1; min-width: 0; padding: 12px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mtwin .gl { display: flex; align-items: center; gap: 8px; padding: 5px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid transparent; }
.hudm .mtwin .gl:hover { border-color: var(--ac); }
.hudm .mtwin .gl.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .mtwin .gl .eq { width: 40px; height: 40px; }
.hudm .mtwin .gl .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .mtwin .gl .t b { display: block; color: #fff; } .hudm .mtwin .gl .t small { color: var(--acd); }
.hudm .mtwin .gl .pr { font-size: 11px; color: #f2c94c; font-variant-numeric: tabular-nums; }
.hudm .mtwin .tot { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .mtwin .tot div { padding: 8px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--frl) 6%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .mtwin .tot b { display: block; font-size: 16px; color: var(--ac); text-shadow: 0 0 6px var(--glow); } .hudm .mtwin .tot span { font-size: 10px; color: var(--dim); }
.hudm .mtwin .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .mtwin .empty { padding: 14px; text-align: center; color: var(--dim); font-size: 12px; }
`;

export class MountMock extends HudWindow {
  private owned: Owned[] = [
    { def: MOUNTS[6]!, loyalty: 6200, max: 8000, gear: { Reins: GEAR[1], Bells: GEAR[3], Saddle: GEAR[6] } },
    { def: MOUNTS[10]!, loyalty: 2100, max: 10000, gear: { Reins: GEAR[0], Mask: GEAR[12] } },
    { def: MOUNTS[0]!, loyalty: 4000, max: 4000, gear: {} },
  ];
  private bag: Gear[] = [GEAR[0]!, GEAR[2]!, GEAR[4]!, GEAR[7]!, GEAR[8]!, GEAR[9]!, GEAR[10]!, GEAR[11]!, GEAR[14]!];
  private equipped = 0;
  private picked = 0;
  private slot: Slot = "Reins";
  private riding = false;
  private note = "";
  private frame = 0;
  private mountEl: HTMLElement | null = null;

  constructor() {
    super("mtwin", 32, 30);
    injectCss("hudm-mount-css", CSS);
    window.setInterval(() => this.animate(), 100);
  }
  protected override shown(): void { this.render(); }
  private cur(): Owned { return this.owned[this.picked]!; }

  /** MountImage: Prguse StartIndex + MountType * 20, 16 frames at 100 ms. The 4-slot frames are mostly empty canvas
   *  (the mount sits in one corner), so each mount's frames are cropped to the union of their opaque pixels, then centred. */
  private animate(): void {
    if (!this.open || !this.mountEl) return;
    const m = this.cur().def;
    const start = (m[4] === 4 ? 1170 : 1330) + m[2] * 20;
    const frames = this.frames.get(start);
    if (!frames) { if (!this.loading.has(start)) { this.loading.add(start); void this.loadFrames(start); } return; }
    this.frame = (this.frame + 1) % frames.length;
    this.mountEl.style.backgroundImage = `url(${frames[this.frame]})`;
  }
  private frames = new Map<number, string[]>();
  private loading = new Set<number>();
  private decoded: HTMLImageElement[] = [];

  private async loadFrames(start: number): Promise<void> {
    const pics = (await Promise.all(Array.from({ length: 16 }, (_, i) => libPicture(Lib.Prguse, start + i)))).filter((p) => !!p);
    if (!pics.length) return;
    const imgs = await Promise.all(pics.map(async (p) => { const im = new Image(); im.src = p!.url; await im.decode().catch(() => {}); return { im, p: p! }; }));
    // Where the mount is, in frame coordinates (frames share their offsets' origin): the 0.5-99.5 percentile of every
    // frame's opaque pixels, so the lone opaque pixels some frames carry in their corners do not stretch the crop.
    const xs: number[] = [], ys: number[] = [];
    const c = document.createElement("canvas"), g = c.getContext("2d", { willReadFrequently: true })!;
    for (const { im, p } of imgs) {
      c.width = p.width; c.height = p.height;
      g.clearRect(0, 0, c.width, c.height); g.drawImage(im, 0, 0);
      const d = g.getImageData(0, 0, c.width, c.height).data;
      for (let y = 0; y < c.height; y++) for (let x = 0; x < c.width; x++) if (d[(y * c.width + x) * 4 + 3]! > 8) { xs.push(p.x + x); ys.push(p.y + y); }
    }
    if (!xs.length) return;
    xs.sort((a, b) => a - b); ys.sort((a, b) => a - b);
    const q = (arr: number[], f: number) => arr[Math.min(arr.length - 1, Math.max(0, Math.floor(arr.length * f)))]!;
    const x0 = q(xs, 0.005) - 4, x1 = q(xs, 0.995) + 4, y0 = q(ys, 0.005) - 4, y1 = q(ys, 0.995) + 4;
    if (!Number.isFinite(x0)) return;
    const w = x1 - x0 + 1, hgt = y1 - y0 + 1;
    const urls: string[] = [];
    for (const { im, p } of imgs) {
      c.width = w; c.height = hgt;
      g.clearRect(0, 0, w, hgt); g.drawImage(im, p.x - x0, p.y - y0);
      const blob = await new Promise<Blob | null>((res) => c.toBlob(res));
      if (!blob) continue;
      const url = URL.createObjectURL(blob);
      // Decode now and keep the element alive: swapping a background to an undecoded image paints nothing until it has
      // decoded, so at 10 swaps a second the mount would never show (the same reason libPicture keeps its <img>s).
      const keep = new Image();
      keep.src = url;
      await keep.decode().catch(() => {});
      this.decoded.push(keep);
      urls.push(url);
    }
    this.frames.set(start, urls);
  }

  private render(): void {
    const o = this.cur();
    // Stable
    const stable = h("div", "stable", h("div", "lbl", "Stable", h("b", "", `${this.owned.length} mounts`)));
    this.owned.forEach((x, i) => {
      const card = h("div", "sc" + (i === this.picked ? " sel" : ""), itemCell(this.el, pretty(x.def[0]), x.def[1]),
        h("div", "", h("b", "", pretty(x.def[0])), h("small", "", `${x.def[4]} gear slots · ${Math.round(x.loyalty / x.max * 100)}% loyal`)));
      if (i === this.equipped) card.append(h("span", "tag", this.riding ? "RIDING" : "EQUIPPED"));
      card.addEventListener("click", () => { this.picked = i; this.slot = "Reins"; this.note = ""; this.render(); });
      stable.append(card);
    });
    // Showcase
    this.mountEl = h("div", "mount");
    const arena = h("div", "arena" + (this.riding && this.picked === this.equipped ? " riding" : ""), this.mountEl);
    const pos: Record<Slot, [number, number]> = { Reins: [14, 20], Bells: [336, 20], Saddle: [14, 150], Ribbon: [336, 150], Mask: [175, 236] };
    for (const s of SLOTS) {
      const locked = s === "Mask" && o.def[4] === 4;
      const g = o.gear[s];
      const cell = g ? itemCell(this.el, `${pretty(g[0])} · ${g[4]} +${g[5]}`, g[1]) : itemCell(this.el, "", -1);
      if (!g) { const gi = h("div", "gi"); gi.innerHTML = `<svg viewBox="0 0 24 24"><path d="${SLOT_ICON[s]}"/></svg>`; cell.append(gi); }
      const gs = h("div", "gs" + (this.slot === s ? " pick" : "") + (locked ? " lock" : ""), cell, h("span", "", locked ? "No mask" : s));
      Object.assign(gs.style, { left: pos[s][0] + "px", top: pos[s][1] + "px" });
      gs.title = locked ? "4-slot mounts have no mask slot" : g ? `${pretty(g[0])} (${g[4]} +${g[5]})` : `Empty ${s.toLowerCase()} slot`;
      if (g) { const x = h("div", "x", "×"); x.title = "Take it off"; x.addEventListener("click", (e) => { e.stopPropagation(); this.bag.push(g); delete o.gear[s]; this.note = `${pretty(g[0])} went back to your bag.`; this.render(); }); gs.append(x); }
      if (!locked) gs.addEventListener("click", () => { this.slot = s; this.render(); });
      arena.append(gs);
    }
    const isEq = this.picked === this.equipped;
    const ride = h("div", "btn main", isEq ? (this.riding ? "Dismount" : "Ride") : "Equip this mount");
    ride.addEventListener("click", () => {
      if (!isEq) { this.equipped = this.picked; this.riding = false; this.note = `${pretty(o.def[0])} is now your mount.`; }
      else if (o.loyalty <= 0) { this.note = "Your mount refuses: restore its loyalty first."; }
      else { this.riding = !this.riding; this.note = this.riding ? "@ride: you climb on." : "You dismount."; }
      this.render();
    });
    const restore = h("div", "btn" + (o.loyalty >= o.max ? " dis" : ""), "Restore loyalty");
    restore.title = "Feed or groom it (mock: uses a treat)";
    restore.addEventListener("click", () => { o.loyalty = Math.min(o.max, o.loyalty + Math.round(o.max * 0.25)); this.note = `${pretty(o.def[0])} looks happier.`; this.render(); });
    const bar = h("div", "bar2", h("i"));
    (bar.firstChild as HTMLElement).style.width = (o.loyalty / o.max * 100) + "%";
    const show = h("div", "show", arena, h("div", "nm", h("h3", "", pretty(o.def[0])), h("small", "", `Mount type ${o.def[2]} · worth ${fmt(o.def[3])} gold`)),
      h("div", "loy", h("div", "lbl", "Loyalty", h("b", "", `${fmt(o.loyalty)} / ${fmt(o.max)}`)), bar), h("div", "row2", ride, restore), h("div", "note", this.note));
    // Side: totals + gear browser for the picked slot
    const totals = new Map<string, number>();
    for (const g of Object.values(o.gear)) if (g) totals.set(g[4], (totals.get(g[4]) ?? 0) + g[5]);
    const tot = h("div", "tot");
    for (const [k, unit] of [["Ride speed", "%"], ["Loyalty kept", "%"], ["AC while riding", ""], ["HP regen", "/s"], ["Mount attack", ""]] as [string, string][])
      if (k !== "Mount attack" || o.def[4] === 5) tot.append(h("div", "", h("b", "", `+${totals.get(k) ?? 0}${unit}`), h("span", "", k)));
    const fits = this.bag.filter((g) => g[2] === this.slot);
    const list = h("div", "");
    const current = o.gear[this.slot];
    if (current) list.append(h("div", "gl on", itemCell(this.el, current[0], current[1]), h("div", "t", h("b", "", pretty(current[0])), h("small", "", `${current[4]} +${current[5]} · fitted`))));
    for (const g of fits) {
      const better = !current || g[5] > current[5];
      const row = h("div", "gl", itemCell(this.el, g[0], g[1]), h("div", "t", h("b", "", pretty(g[0])), h("small", "", `${g[4]} +${g[5]}${current ? (better ? `  ▲${g[5] - current[5]}` : `  ▼${current[5] - g[5]}`) : ""}`)), h("span", "pr", fmt(g[3])));
      row.title = "Click to fit it";
      row.addEventListener("click", () => {
        if (current) this.bag.push(current);
        this.bag = this.bag.filter((x) => x !== g);
        o.gear[this.slot] = g;
        this.note = `${pretty(g[0])} fitted.`;
        this.render();
      });
      list.append(row);
    }
    if (!fits.length && !current) list.append(h("div", "empty", `No ${this.slot.toLowerCase()} in your bag.`));
    const side = h("div", "side", h("div", "lbl", "Gear bonuses"), tot, h("div", "lbl", `${this.slot} · from your bag`, h("b", "", String(fits.length))), list);
    this.el.replaceChildren(h("div", "panel", this.titleBar("MOUNT", "Close (J)"), h("div", "mbody", stable, show, side)));
  }
}
