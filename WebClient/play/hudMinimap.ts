// Minimap mock for the scene lab's UI tester: an always-on HUD element (top right) drawn from the map's real minimap
// picture (MMap.Lib[MapInfo.MiniMap], like the desktop's MiniMapDialog) around the lab camera, in a themed round (or
// square) frame: a banner with the map's name, your coordinates and time-of-day / weather icons (from the lab's
// settings); NPCs and exits from play/lab-data/maps.json, the lab's sample players and monsters, you as an arrow in the
// middle; arrows on the rim for exits beyond the edge; hover for names, click to move the camera, wheel or +/− to zoom,
// a shape toggle and a button for the world map.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { mapCells } from "../src/game/mapFiles.ts";
import { injectCss, h, rafCoalesce } from "./hudWindow.ts";
import { loadMapData, type MapRow, type MapHost } from "./hudBigMap.ts";

export interface WorldObj { x: number; y: number; kind: "player" | "monster" | "npc" }
/** The lab's side: the big map's host plus what the minimap also shows. */
export interface WorldHost extends MapHost {
  objects(): WorldObj[];
  /** Time of day / weather names for the banner ("Day", "Night", ["Rain"], ...). */
  light(): string;
  weather(): string[];
}

const ZOOMS = [14, 24, 40, 64]; // cells from the centre to the edge
const SIZE = 176;

const CSS = `
.hudm .mmap { position: absolute; right: 12px; top: 10px; width: ${SIZE + 24}px; pointer-events: auto; color: var(--tx); user-select: none; }
.hudm .mmap .ban { position: relative; z-index: 2; margin: 0 auto -10px; width: max-content; max-width: 100%; padding: 4px 12px 5px; text-align: center; border-radius: 12px;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 3px 8px rgba(0,0,0,.6); }
.hudm .mmap .ban b { display: block; font-size: 12px; font-weight: 700; letter-spacing: .04em; color: var(--ac); text-shadow: 0 0 6px var(--glow); white-space: nowrap; }
.hudm .mmap .ban .ln { display: flex; align-items: center; justify-content: center; gap: 7px; font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .mmap .ban .ln svg { width: 12px; height: 12px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; color: var(--acd); }
.hudm .mmap .ban .ln span { display: inline-flex; align-items: center; gap: 3px; }
.hudm .mmap .disc { position: relative; width: ${SIZE}px; height: ${SIZE}px; margin: 0 auto; border-radius: 50%; overflow: hidden; cursor: crosshair;
  background: #05070b; box-shadow: 0 0 0 2px #000, 0 0 0 4px var(--fr), 0 0 0 5px #000, 0 0 calc(14px * var(--gl)) var(--glow), inset 0 0 18px rgba(0,0,0,.9); }
.hudm .mmap.square .disc { border-radius: var(--rad); }
.hudm .mmap canvas { position: absolute; inset: 0; width: 100%; height: 100%; }
.hudm .mmap .vig { position: absolute; inset: 0; pointer-events: none; border-radius: inherit; box-shadow: inset 0 0 22px rgba(0,0,0,.85); }
.hudm .mmap .n { position: absolute; left: 50%; top: 13px; transform: translateX(-50%); z-index: 3; font-size: 10px; font-weight: 800; color: var(--ac); text-shadow: 0 0 4px #000, 0 0 6px var(--glow); pointer-events: none; }
.hudm .mmap .tip { position: absolute; z-index: 4; transform: translate(-50%, -130%); padding: 2px 7px; border-radius: 6px; white-space: nowrap; pointer-events: none;
  font-size: 10px; font-weight: 600; color: #fff; background: rgba(0,0,0,.8); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .mmap .btns { display: flex; justify-content: center; gap: 5px; margin-top: 6px; }
.hudm .mmap .btns div { width: 22px; height: 22px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: 50%; font-size: 13px; font-weight: 700;
  color: var(--acd); background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .mmap .btns div:hover { color: #fff; border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .mmap .btns svg { width: 12px; height: 12px; fill: none; stroke: currentColor; stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .mmap.shut .disc, .hudm .mmap.shut .btns div:not(.keep) { display: none; }
.hudm .mmap.shut .ban { margin-bottom: 4px; }
`;

const ICON = {
  sun: "M12 7a5 5 0 110 10 5 5 0 010-10zM12 1v3M12 20v3M1 12h3M20 12h3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1",
  moon: "M20 14A8 8 0 1110 4a6.5 6.5 0 0010 10z",
  dusk: "M3 18h18M6 18a6 6 0 0112 0M12 5v3M5 9l2 2M19 9l-2 2",
  rain: "M7 15a5 5 0 01.5-10 6 6 0 0111 2 4 4 0 01-.5 8zM8 18l-1 3M12 18l-1 3M16 18l-1 3",
  snow: "M12 2v20M3 7l18 10M3 17L21 7",
  fog: "M3 9h18M5 13h14M3 17h18",
  pin: "M12 21s-7-7-7-12a7 7 0 0114 0c0 5-7 12-7 12zM12 7a2 2 0 110 4 2 2 0 010-4z",
  map: "M2 5l6-2 8 3 6-2v15l-6 2-8-3-6 2zM8 3v15M16 6v15",
  shape: "M4 4h16v16H4z",
  fold: "M6 9l6 6 6-6",
};
const svg = (d: string) => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); const p = document.createElementNS("http://www.w3.org/2000/svg", "path"); p.setAttribute("d", d); s.append(p); return s; };
const pretty = (t: string) => t.replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/([A-Za-z])\(/g, "$1 (").trim();

export class MinimapMock {
  readonly el: HTMLElement;
  host: WorldHost | null = null;
  /** Opens the world map (the HUD wires it). */
  onBigMap: () => void = () => {};
  /** Group members on this map (S.SendMemberLocation in the game; the group mock here). */
  party: () => { x: number; y: number; name: string }[] = () => [];
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private tip: HTMLElement;
  private ban: HTMLElement;
  private zoom = 1;
  private rowFor = "";
  private row: MapRow | null = null;
  private img: HTMLImageElement | null = null;
  private dims: { w: number; h: number } | null = null;
  private marks: { px: number; py: number; label: string }[] = [];
  private mouse: { x: number; y: number } | null = null;
  private last = "";

  constructor() {
    injectCss("hudm-minimap-css", CSS);
    this.canvas = h("canvas");
    this.canvas.width = this.canvas.height = SIZE * 2; // crisp at 2x
    this.ctx = this.canvas.getContext("2d")!;
    this.tip = h("div", "tip");
    this.tip.hidden = true;
    this.ban = h("div", "ban");
    const disc = h("div", "disc", this.canvas, h("div", "vig"), h("span", "n", "N"), this.tip);
    const btn = (d: string | null, text: string, title: string, fn: () => void, keep = false) => {
      const b = h("div", keep ? "keep" : "", ...(d ? [svg(d)] : [text]));
      b.title = title;
      b.addEventListener("click", fn);
      return b;
    };
    const btns = h("div", "btns",
      btn(null, "−", "Zoom out (wheel)", () => this.setZoom(this.zoom + 1)), btn(null, "+", "Zoom in (wheel)", () => this.setZoom(this.zoom - 1)),
      btn(ICON.map, "", "World map (B)", () => this.onBigMap()), btn(ICON.shape, "", "Round / square", () => { this.el.classList.toggle("square"); this.last = ""; }),
      btn(ICON.fold, "", "Hide / show the minimap", () => this.el.classList.toggle("shut"), true));
    this.el = h("div", "mmap", this.ban, disc, btns);

    disc.addEventListener("wheel", (e) => { e.preventDefault(); this.setZoom(this.zoom + (e.deltaY > 0 ? 1 : -1)); }, { passive: false });
    // The disc's rect is measured on mouseenter (the minimap does not move under the pointer); the hover tip updates at
    // most once a frame.
    let r: DOMRect | null = null;
    const hover = rafCoalesce(() => { if (this.mouse) this.hover(); });
    disc.addEventListener("mouseenter", () => { r = disc.getBoundingClientRect(); });
    disc.addEventListener("mousemove", (e) => { r ??= disc.getBoundingClientRect(); this.mouse = { x: (e.clientX - r.left) / r.width * SIZE, y: (e.clientY - r.top) / r.height * SIZE }; hover(); });
    disc.addEventListener("mouseleave", () => { r = null; this.mouse = null; this.tip.hidden = true; });
    disc.addEventListener("click", (e) => {
      const r = disc.getBoundingClientRect();
      const c = this.cellAt((e.clientX - r.left) / r.width * SIZE, (e.clientY - r.top) / r.height * SIZE);
      if (c && this.host) { this.host.walk(c.x, c.y); this.last = ""; }
    });
    window.setInterval(() => this.tick(), 150);
  }

  private setZoom(z: number): void { this.zoom = Math.max(0, Math.min(ZOOMS.length - 1, z)); this.last = ""; }
  private radius(): number { return ZOOMS[this.zoom]!; }

  /** Load the viewed map's row, picture and size when the lab's map changes. */
  private async ensure(file: string): Promise<void> {
    if (this.rowFor === file) return;
    this.rowFor = file;
    this.row = null; this.img = null; this.dims = null;
    const all = await loadMapData();
    const row = all.find((m) => m.f.toLowerCase() === file.toLowerCase()) ?? null;
    if (this.rowFor !== file) return;
    this.row = row;
    // The size first (clicks and markers work while the picture is still decoding), then the picture.
    const picP = row ? libPicture(Lib.MiniMap, row.m || row.b) : Promise.resolve(null);
    const cells = await mapCells(file).catch(() => null);
    if (this.rowFor !== file) return;
    this.dims = cells ? { w: cells.width, h: cells.height } : null;
    this.last = "";
    const pic = await picP;
    if (this.rowFor !== file) return;
    if (pic) { const im = new Image(); im.src = pic.url; await im.decode().catch(() => {}); this.img = im; }
    this.last = "";
  }

  private cellAt(px: number, py: number): { x: number; y: number } | null {
    if (!this.host || !this.dims) return null;
    const c = this.host.cam(), r = this.radius();
    const x = Math.round(c.x + (px / SIZE * 2 - 1) * r), y = Math.round(c.y + (py / SIZE * 2 - 1) * r);
    return x >= 0 && y >= 0 && x < this.dims.w && y < this.dims.h ? { x, y } : null;
  }

  private tick(): void {
    if (!this.host || this.el.closest("[hidden]")) return;
    const file = this.host.map();
    void this.ensure(file);
    const c = this.host.cam();
    const objs = this.host.objects();
    const key = `${file}|${c.x.toFixed(1)},${c.y.toFixed(1)}|${this.zoom}|${objs.map((o) => o.x + "," + o.y).join(";")}|${this.party().map((p) => p.x + "," + p.y).join(";")}|${!!this.img}|${this.host.light()}|${this.host.weather().join()}|${getComputedStyle(this.el).getPropertyValue("--ac")}`; // the accent: redraw on a theme change
    if (key === this.last) return;
    this.last = key;
    this.banner(c);
    this.draw(c, objs);
    this.hover();
  }

  private banner(c: { x: number; y: number }): void {
    const light = this.host!.light(), weather = this.host!.weather();
    const lightIcon = /night/i.test(light) ? ICON.moon : /dawn|evening/i.test(light) ? ICON.dusk : ICON.sun;
    const ln = h("div", "ln", h("span", "", svg(ICON.pin), `${Math.round(c.x)}, ${Math.round(c.y)}`), h("span", "", svg(lightIcon), light));
    for (const w of weather.slice(0, 2)) ln.append(h("span", "", svg(/snow/i.test(w) ? ICON.snow : /fog/i.test(w) ? ICON.fog : ICON.rain), w));
    this.ban.replaceChildren(h("b", "", this.row ? pretty(this.row.t) : `Map ${this.host!.map()}`), ln);
  }

  private draw(c: { x: number; y: number }, objs: WorldObj[]): void {
    const g = this.ctx, S = SIZE * 2, r = this.radius();
    const toPx = (x: number, y: number): [number, number] => [((x - c.x) / r + 1) * S / 2, ((y - c.y) / r + 1) * S / 2];
    g.clearRect(0, 0, S, S);
    this.marks = [];
    if (this.img && this.dims) {
      const sx = this.img.naturalWidth / this.dims.w, sy = this.img.naturalHeight / this.dims.h;
      g.imageSmoothingEnabled = true;
      g.drawImage(this.img, (c.x - r) * sx, (c.y - r) * sy, 2 * r * sx, 2 * r * sy, 0, 0, S, S);
    } else {
      g.fillStyle = "#0b0f16"; g.fillRect(0, 0, S, S);
    }
    const css = getComputedStyle(this.el);
    const accent = css.getPropertyValue("--ac").trim() || "#3fb4ff";
    const dot = (x: number, y: number, rad: number, fill: string, label: string) => {
      const [px, py] = toPx(x, y);
      if (px < -6 || py < -6 || px > S + 6 || py > S + 6) return;
      g.beginPath(); g.arc(px, py, rad, 0, Math.PI * 2); g.fillStyle = fill; g.fill(); g.lineWidth = 2; g.strokeStyle = "rgba(0,0,0,.85)"; g.stroke();
      if (label) this.marks.push({ px: px / 2, py: py / 2, label });
    };
    const row = this.row;
    if (row) {
      for (const [raw, x, y] of row.npc) if (!raw.startsWith("_")) dot(x, y, 5, "#ffd75a", pretty(raw.split("_").pop() ?? raw) + (raw.includes("_") ? ` (${pretty(raw.split("_").slice(0, -1).join(" "))})` : ""));
      // Exits: arrows on the map, or on the rim pointing at the nearest ones beyond the edge.
      const off: [number, number, string][] = [];
      for (const [, title, x, y] of row.mv) {
        const [px, py] = toPx(x, y);
        if (px >= 0 && py >= 0 && px <= S && py <= S) { this.arrow(px, py, 0, accent, 7); this.marks.push({ px: px / 2, py: py / 2, label: `To ${pretty(title)}` }); }
        else off.push([x, y, title]);
      }
      off.sort((a, b) => Math.hypot(a[0] - c.x, a[1] - c.y) - Math.hypot(b[0] - c.x, b[1] - c.y));
      for (const [x, y, title] of off.slice(0, 3)) {
        const ang = Math.atan2(y - c.y, x - c.x);
        const rr = S / 2 - 12, px = S / 2 + Math.cos(ang) * rr, py = S / 2 + Math.sin(ang) * rr;
        this.arrow(px, py, ang + Math.PI / 2, accent, 9);
        this.marks.push({ px: px / 2, py: py / 2, label: `${pretty(title)} · ${Math.round(Math.hypot(x - c.x, y - c.y))} cells` });
      }
    }
    for (const o of objs) dot(o.x, o.y, o.kind === "monster" ? 4 : 4.5, o.kind === "monster" ? "#ff5a4a" : o.kind === "npc" ? "#ffd75a" : "#9fd8ff", "");
    // Group members: accent diamonds with their names on hover.
    for (const p of this.party()) {
      const [px, py] = toPx(p.x, p.y);
      if (px < -6 || py < -6 || px > S + 6 || py > S + 6) continue;
      g.save(); g.translate(px, py); g.rotate(Math.PI / 4);
      g.fillStyle = accent; g.shadowColor = accent; g.shadowBlur = 8; g.fillRect(-5, -5, 10, 10);
      g.shadowBlur = 0; g.lineWidth = 2; g.strokeStyle = "#fff"; g.strokeRect(-5, -5, 10, 10);
      g.restore();
      this.marks.push({ px: px / 2, py: py / 2, label: p.name });
    }
    // You: an arrow at the centre.
    this.arrow(S / 2, S / 2, 0, accent, 11, true);
  }

  /** A filled arrow at (px, py) pointing along `ang` (0 = up). */
  private arrow(px: number, py: number, ang: number, fill: string, size: number, me = false): void {
    const g = this.ctx;
    g.save(); g.translate(px, py); g.rotate(ang);
    if (me) { g.shadowColor = fill; g.shadowBlur = 12; }
    g.beginPath(); g.moveTo(0, -size); g.lineTo(size * 0.7, size * 0.8); g.lineTo(0, size * 0.35); g.lineTo(-size * 0.7, size * 0.8); g.closePath();
    g.fillStyle = fill; g.fill(); g.shadowBlur = 0; g.lineWidth = 2; g.strokeStyle = me ? "#fff" : "rgba(0,0,0,.85)"; g.stroke();
    g.restore();
  }

  private hover(): void {
    if (!this.mouse) return;
    let best: { px: number; py: number; label: string } | null = null, bd = 9;
    for (const m of this.marks) { const d = Math.hypot(m.px - this.mouse.x, m.py - this.mouse.y); if (d < bd) { bd = d; best = m; } }
    const cell = this.cellAt(this.mouse.x, this.mouse.y);
    this.tip.hidden = !best && !cell;
    this.tip.textContent = best ? best.label : cell ? `${cell.x}, ${cell.y}` : "";
    this.tip.style.left = (best ? best.px : this.mouse.x) + "px";
    this.tip.style.top = (best ? best.py : this.mouse.y) + "px";
  }
}
