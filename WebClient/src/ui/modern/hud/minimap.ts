// Ported from play/hudMinimap.ts: the new UI's minimap (top right), the desktop's MiniMapDialog
// (Client/MirScenes/Dialogs/MainDialogs.cs; classic port src/ui/miniMap.ts) in a themed round or square frame.
//   Picture   MMap.Lib[MapControl.MiniMap] around the user (CurrentMap.miniMapIndex, the map's size from the loaded
//             MapCells); maps without a minimap switch to the small mode (the banner only), like DrawMiniMap. A generated
//             dungeon (no MMap art) uses the picture drawn from its cells (game/cellPicture.ts).
//   Banner    map title + the time-of-day icon (GameScene.Lights, LightSetting; name on hover). Under the disc: the user's location (Functions.PointToString).
//   Marks     NPCs and exits from the map's ClientMapInfo (C.RequestMapInfo via GameInfo.requestMap; exits beyond the edge
//             as arrows on the rim), the NPC quest "!" / "?" markers (Quests.npcQuestIcon + questMiniMapMarker, recomputed
//             on quest changes only), the objects in view (players, monsters, NPCs and guards; items, the dead, spells and
//             sneaking objects skipped like DrawMiniMap; the user's pets and group members in the accent colour), group
//             members on this map as diamonds (GroupState.locations, S.SendMemberLocation), the user as the centre arrow
//             pointing the way the user faces (turns eased over 180 ms, after WoOOLcraft MiniMapDialog.GetSmoothedUserArrowAngle).
//   Smooth    the view centres on the user's drawn position (Movement - OffSetMove / cell, the world camera's sum), and
//             objects sit at their drawn positions, so the picture slides with the world instead of jumping a cell a step
//             (after WoOOLcraft's smoothed view offset, but locked to the camera rather than eased behind it).
//   Buttons   zoom (wheel / - / +), world map (the "bigMap" window), mail (the "mail" window; a badge while
//             MailBox.newMail, the desktop's blinking NewMail icon), round / square, fold (KeybindOptions.Minimap: toggle()).
// Hover shows names, a click walks there (GameWorld.walkTo: MapControl.AutoPath, as the big map does; the desktop
// minimap has no click-walk). Drawn each frame while the view or an object moves or the arrow turns, else at 10 Hz, on the
// UI ticker; never while folded or hidden. The accent colour is read
// from the theme once and on theme changes (no getComputedStyle per draw). Zoom / shape / fold are per-viewer
// conveniences in localStorage 'crystal.ui.minimap'.

import { LightSetting, ObjectType } from "../../../protocol/enums.generated.ts";
import type { ClientMapInfo } from "../../../protocol/server.ts";
import { CELL_H, CELL_W, type MapObj } from "../../../game/objects.ts";
import { questMiniMapMarker } from "../../../game/quests.ts";
import { cellPicture } from "../../../game/cellPicture.ts";
import { isGeneratedMap } from "../../../game/mapFiles.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, rafCoalesce, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { npcName } from "../core/format.ts";
import type { UiRuntime } from "../core/runtime.ts";

const ZOOMS = [14, 24, 40, 64]; // cells from the centre to the edge
const SIZE = 176;
const STORE = "crystal.ui.minimap";
const SLOW_MS = 100; // the banner, markers and a still picture
const TURN_MS = 180; // WoOOLcraft UserArrowAnimationDurationMs

/** The cell an object is drawn at, fractional while it walks (MapObj.updateMovement: Movement + OffSetMove). */
function drawnX(ob: MapObj): number { return ob.movement.X - ob.offsetMove.X / CELL_W; }
function drawnY(ob: MapObj): number { return ob.movement.Y - ob.offsetMove.Y / CELL_H; }

const CSS = `
.hudm .mmap { position: absolute; z-index: 3; right: 12px; top: 10px; width: ${SIZE + 24}px; pointer-events: none; color: var(--tx); user-select: none; }
.hudm .mmap .ban { position: relative; z-index: 2; margin: 0 auto -10px; width: max-content; max-width: 100%; padding: 4px 12px 5px; display: flex; align-items: center; justify-content: center; gap: 6px; border-radius: 12px; pointer-events: auto;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 3px 8px rgba(0,0,0,.6); }
.hudm .mmap .ban b { display: block; min-width: 0; font-size: 12px; font-weight: 700; letter-spacing: .04em; color: var(--ac); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mmap .loc { position: relative; z-index: 2; display: flex; align-items: center; justify-content: center; gap: 9px; width: max-content; margin: -16px auto 0; padding: 3px 11px 4px;
  border-radius: 10px 10px 4px 4px; font-size: 10px; color: var(--dim); font-variant-numeric: tabular-nums; pointer-events: auto;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 -2px 6px rgba(0,0,0,.5); }
.hudm .mmap .ban .lt { display: inline-flex; flex: none; }
.hudm .mmap .ban .lt svg.ic { width: 13px; height: 13px; color: var(--acd); }
.hudm .mmap .loc svg.ic { width: 12px; height: 12px; color: var(--acd); }
.hudm .mmap .loc span { display: inline-flex; align-items: center; gap: 3px; }
.hudm .mmap .disc { position: relative; width: ${SIZE}px; height: ${SIZE}px; margin: 0 auto; border-radius: 50%; overflow: hidden; cursor: crosshair; pointer-events: auto;
  background: #05070b; box-shadow: 0 0 0 2px #000, 0 0 0 4px var(--fr), 0 0 0 5px #000, inset 0 0 18px rgba(0,0,0,.9); }
.hudm .mmap.square .disc { border-radius: var(--rad); }
.hudm .mmap canvas { position: absolute; inset: 0; width: 100%; height: 100%; }
.hudm .mmap .vig { position: absolute; inset: 0; pointer-events: none; border-radius: inherit; box-shadow: inset 0 0 22px rgba(0,0,0,.85); }
.hudm .mmap .n { position: absolute; left: 50%; top: 13px; transform: translateX(-50%); z-index: 3; font-size: 10px; font-weight: 800; color: var(--ac); text-shadow: 0 0 4px #000; pointer-events: none; }
.hudm .mmap .tip { position: absolute; left: 0; top: 0; z-index: 4; padding: 2px 7px; border-radius: 6px; white-space: nowrap; pointer-events: none; will-change: transform;
  font-size: 10px; font-weight: 600; color: #fff; background: rgba(0,0,0,.8); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .mmap .btns { display: flex; justify-content: center; gap: 5px; margin-top: 6px; }
.hudm .mmap .btns > div { position: relative; width: 22px; height: 22px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: 50%; font-size: 13px; font-weight: 700;
  pointer-events: auto; color: var(--acd); background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .mmap .btns > div:hover { color: #fff; border-color: var(--ac); }
.hudm .mmap .btns svg.ic { width: 12px; height: 12px; }
.hudm .mmap .btns > .zoom svg.ic { width: 11px; height: 11px; stroke-width: 3; }
.hudm .mmap .btns .badge { position: absolute; right: -3px; top: -3px; width: 9px; height: 9px; border-radius: 50%; background: var(--bad); box-shadow: 0 0 0 1px #000; }
.hudm .mmap .btns .badge[hidden] { display: none; }
.hudm .mmap.shut .disc, .hudm .mmap.shut .btns > div:not(.keep), .hudm .mmap.small .disc, .hudm .mmap.small .btns > .zoom { display: none; }
.hudm .mmap.shut .ban, .hudm .mmap.small .ban { margin-bottom: 4px; }
.hudm .mmap.shut .loc, .hudm .mmap.small .loc { margin-top: 0; border-radius: 10px; }
`;

interface Mark { px: number; py: number; label: string }

function lightIcon(light: number): IconName {
  return light === LightSetting.Night ? "moon" : light === LightSetting.Dawn || light === LightSetting.Evening ? "dusk" : "sun";
}
function lightName(light: number): string {
  switch (light) {
    case LightSetting.Dawn: return "Dawn";
    case LightSetting.Evening: return "Evening";
    case LightSetting.Night: return "Night";
  }
  return "Day";
}

export class MinimapHud {
  readonly el: HTMLElement;
  private readonly canvas: HTMLCanvasElement;
  private readonly g: CanvasRenderingContext2D;
  private readonly tip: HTMLElement;
  private readonly title: HTMLElement;
  private readonly coords: HTMLElement;
  private readonly lightBox: HTMLElement;
  private readonly mailBadge: HTMLElement;
  private zoom = 1;
  private shut = false;
  private square = false;
  private img: HTMLImageElement | null = null;
  private imgIndex = -1;
  private info: ClientMapInfo | null = null;
  private infoIndex = -1;
  /** NPC ObjectID -> quest marker (recomputed on quest / map info changes only). */
  private markers = new Map<number, { text: string; colour: string }>();
  private marks: Mark[] = [];
  private mouse: { x: number; y: number } | null = null;
  private lastLight = -1;
  private ticks = 0;
  private slowDue = 0;
  /** The view centre last drawn (the user's drawn cell, fractional mid-step); NaN before the first draw. */
  private cx = NaN;
  private cy = NaN;
  /** The centre arrow: the facing it eases to, from where, since when (radians, 0 = up, clockwise). */
  private arrowDir = -1;
  private arrowFrom = 0;
  private arrowTo = 0;
  private arrowStart = 0;
  private drawnAngle = NaN;
  private accent: string;
  private readonly sub: { on(): void; off(): void };

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext) {
    injectCss("hudm-minimap-css", CSS);
    this.load();
    const sig = { signal: rt.signal };
    this.accent = rt.theme.accent;
    this.canvas = h("canvas");
    this.canvas.width = this.canvas.height = SIZE * 2; // crisp at 2x
    this.g = this.canvas.getContext("2d")!;
    this.tip = h("div", "tip");
    this.tip.hidden = true;
    this.title = h("b");
    this.coords = h("span");
    this.lightBox = h("span", "lt");
    const ban = h("div", "ban", this.title, this.lightBox);
    const loc = h("div", "loc", this.coords);
    const disc = h("div", "disc", this.canvas, h("div", "vig"), h("span", "n", "N"), this.tip);
    const btn = (content: Node | string, title: string, fn: () => void, cls = "") => {
      const b = h("div", cls, content);
      b.title = title;
      b.setAttribute("role", "button");
      b.addEventListener("click", fn, sig);
      return b;
    };
    this.mailBadge = h("i", "badge");
    const mail = btn(svg("mail"), "Mail", () => rt.toggleWindow("mail"), "keep");
    mail.append(this.mailBadge);
    const bigMap = btn(svg("map"), "World map", () => rt.toggleWindow("bigMap"));
    // Window buttons show only once their window is registered (windowRegistry.ts: an unregistered key is hidden).
    const winBtns: [HTMLElement, string][] = [[bigMap, "bigMap"], [mail, "mail"]];
    const syncWinBtns = () => { for (const [b, key] of winBtns) b.hidden = !rt.hasWindow(key); };
    syncWinBtns();
    const btns = h("div", "btns",
      btn(svg("minus"), "Zoom out (wheel)", () => this.setZoom(this.zoom + 1), "zoom"), btn(svg("plus"), "Zoom in (wheel)", () => this.setZoom(this.zoom - 1), "zoom"),
      bigMap, mail,
      btn(svg("square"), "Round / square", () => { this.square = !this.square; this.el.classList.toggle("square", this.square); this.save(); }),
      btn(svg("chevronDown"), "Hide / show the minimap", () => this.toggle(), "keep"));
    this.el = h("div", "mmap snap" + (this.square ? " square" : "") + (this.shut ? " shut" : ""), ban, disc, loc, btns);

    disc.addEventListener("wheel", (e) => { e.preventDefault(); this.setZoom(this.zoom + (e.deltaY > 0 ? 1 : -1)); }, { passive: false, signal: rt.signal });
    // The disc's rect is measured on pointerenter (the minimap does not move under the pointer); the tip moves at most
    // once a frame.
    let r: DOMRect | null = null;
    const hover = rafCoalesce(() => this.hover(), rt.signal);
    disc.addEventListener("pointerenter", () => { r = disc.getBoundingClientRect(); }, sig);
    disc.addEventListener("pointermove", (e) => { r ??= disc.getBoundingClientRect(); this.mouse = { x: (e.clientX - r.left) / r.width * SIZE, y: (e.clientY - r.top) / r.height * SIZE }; hover(); }, sig);
    disc.addEventListener("pointerleave", () => { r = null; this.mouse = null; this.tip.hidden = true; }, sig);
    disc.addEventListener("click", (e) => {
      const rr = disc.getBoundingClientRect();
      const c = this.cellAt((e.clientX - rr.left) / rr.width * SIZE, (e.clientY - rr.top) / rr.height * SIZE);
      if (c) ctx.world.walkTo({ X: c.x, Y: c.y });
    }, sig);

    rt.layers.wins.append(this.el);
    this.sub = rt.ticker.frame((now) => this.tick(now));
    if (!this.shut) this.sub.on();

    const m = ctx.models;
    const offs = [
      rt.themeChanged.on((t) => { this.accent = t.accent; }),
      rt.windowsChanged.on(syncWinBtns),
      m.mail.changed.on(() => this.renderMail()),
      m.currentMap.changed.on((what) => { if (what === "map") this.mapChanged(); }),
      m.gameInfo.onChange((kind, index) => { if (kind === "map" && index === m.currentMap.index) this.mapChanged(); }),
      m.quests.onChange(() => this.computeMarkers()),
    ];
    rt.signal.addEventListener("abort", () => { for (const off of offs) off(); this.sub.off(); }, { once: true });
    this.renderMail();
    this.mapChanged();
  }

  /** KeybindOptions.Minimap / the fold button: folds the minimap to its banner (MiniMapDialog.Toggle). */
  toggle(): void {
    this.shut = !this.shut;
    this.el.classList.toggle("shut", this.shut);
    if (this.shut) { this.sub.off(); this.tip.hidden = true; } else { this.slowDue = 0; this.sub.on(); }
    this.save();
  }

  dispose(): void {
    this.sub.off();
    this.el.remove();
  }

  // — State —

  private load(): void {
    try {
      const s = JSON.parse(localStorage.getItem(STORE) ?? "null") as { zoom?: unknown; shut?: unknown; square?: unknown } | null;
      if (typeof s?.zoom === "number" && s.zoom >= 0 && s.zoom < ZOOMS.length) this.zoom = Math.floor(s.zoom);
      if (typeof s?.shut === "boolean") this.shut = s.shut;
      if (typeof s?.square === "boolean") this.square = s.square;
    } catch { /* defaults */ }
  }
  private save(): void {
    try { localStorage.setItem(STORE, JSON.stringify({ zoom: this.zoom, shut: this.shut, square: this.square })); } catch { /* not kept */ }
  }

  private setZoom(z: number): void {
    this.zoom = Math.max(0, Math.min(ZOOMS.length - 1, z));
    this.save();
  }
  private radius(): number { return ZOOMS[this.zoom]!; }

  private renderMail(): void {
    const on = this.ctx.models.mail.newMail;
    this.mailBadge.hidden = !on;
    this.mailBadge.parentElement!.title = on ? "Mail (new)" : "Mail";
  }

  /** S.MapInformation / MapChanged / the map's ClientMapInfo arrived: picture, info, markers. */
  private mapChanged(): void {
    const cm = this.ctx.models.currentMap;
    const gi = this.ctx.models.gameInfo;
    setText(this.title, cm.title || this.ctx.world.mapTitle);
    if (cm.index !== this.infoIndex || !this.info) {
      this.infoIndex = cm.index;
      this.info = gi.map(cm.index) ?? null;
      if (!this.info && cm.index >= 0) gi.requestMap(cm.index);
    }
    const mini = cm.miniMapIndex;
    // DrawMiniMap: the small mode without a minimap (a generated dungeon has its cell picture)
    this.el.classList.toggle("small", mini <= 0 && !isGeneratedMap(cm.fileName));
    if (mini !== this.imgIndex) {
      this.imgIndex = mini;
      this.img = null;
      if (mini > 0) {
        void libPicture(Lib.MiniMap, mini).then(async (pic) => {
          if (!pic || this.imgIndex !== mini) return;
          const im = new Image();
          im.src = pic.url;
          await im.decode().catch(() => {});
          if (this.imgIndex === mini) this.img = im;
        });
      }
    }
    this.computeMarkers();
  }

  private computeMarkers(): void {
    const next = new Map<number, { text: string; colour: string }>();
    const q = this.ctx.models.quests;
    for (const npc of this.info?.NPCs ?? []) {
      const m = questMiniMapMarker(q.npcQuestIcon(npc.ObjectID));
      if (m) next.set(npc.ObjectID, m);
    }
    for (const ob of this.ctx.world.objects.values()) {
      if (ob.race !== ObjectType.Merchant || next.has(ob.id)) continue;
      const m = questMiniMapMarker(q.npcQuestIcon(ob.id));
      if (m) next.set(ob.id, m);
    }
    this.markers = next;
  }

  // — Drawing (10 Hz) —

  private cellAt(px: number, py: number): { x: number; y: number } | null {
    const user = this.ctx.player.user, map = this.ctx.world.map;
    if (!user || !map.width || this.el.classList.contains("small")) return null;
    const r = this.radius();
    const cx = Number.isNaN(this.cx) ? user.location.X : this.cx, cy = Number.isNaN(this.cy) ? user.location.Y : this.cy;
    const x = Math.round(cx + (px / SIZE * 2 - 1) * r), y = Math.round(cy + (py / SIZE * 2 - 1) * r);
    return x >= 0 && y >= 0 && x < map.width && y < map.height ? { x, y } : null;
  }

  /** Every frame: the banner and markers at 10 Hz; the picture each frame while anything on it moves, else at 10 Hz. */
  private tick(now: number): void {
    if (this.rt.root.hidden) return;
    const user = this.ctx.player.user;
    if (!user) return;
    const slow = now >= this.slowDue;
    if (slow) {
      this.slowDue = now + SLOW_MS;
      setText(this.coords, `${user.location.X}, ${user.location.Y}`);
      const light = this.ctx.world.lights.timeOfDay;
      if (light !== this.lastLight) {
        this.lastLight = light;
        this.lightBox.replaceChildren(svg(lightIcon(light)));
        this.lightBox.title = lightName(light);
      }
    }
    if (this.el.classList.contains("small")) return;
    // NPCs coming into view get their markers within 2 s (quest changes recompute at once).
    if (slow && ++this.ticks % 20 === 0) this.computeMarkers();
    const cx = drawnX(user), cy = drawnY(user), angle = this.arrowAngle(user.direction, now);
    let moving = cx !== this.cx || cy !== this.cy || angle !== this.drawnAngle;
    if (!moving && !slow) {
      for (const ob of this.ctx.world.objects.values()) if (ob.offsetMove.X || ob.offsetMove.Y) { moving = true; break; }
    }
    if (!slow && !moving) return;
    this.cx = cx; this.cy = cy; this.drawnAngle = angle;
    this.draw(cx, cy, user.name, angle);
    if (this.mouse) this.hover();
  }

  /** The centre arrow's angle for MirDirection `dir` (Up = 0, clockwise by 45 degrees): a new facing turns the short way
   *  round over TURN_MS, smoothstep (WoOOLcraft MiniMapDialog.GetSmoothedUserArrowAngle). */
  private arrowAngle(dir: number, now: number): number {
    const target = dir * Math.PI / 4;
    if (this.arrowDir < 0) { this.arrowDir = dir; this.arrowFrom = this.arrowTo = target; this.arrowStart = now; return target; }
    const t = Math.min(1, Math.max(0, (now - this.arrowStart) / TURN_MS));
    let cur = this.arrowFrom + (this.arrowTo - this.arrowFrom) * t * t * (3 - 2 * t);
    if (dir !== this.arrowDir) {
      let d = (target - cur) % (Math.PI * 2);
      if (d > Math.PI) d -= Math.PI * 2; else if (d < -Math.PI) d += Math.PI * 2;
      this.arrowDir = dir; this.arrowFrom = cur; this.arrowTo = cur + d; this.arrowStart = now;
      return cur;
    }
    if (t >= 1) { // settled: keep the angle small
      cur = this.arrowFrom = this.arrowTo = target;
    }
    return cur;
  }

  private draw(cx: number, cy: number, me: string, angle: number): void {
    const g = this.g, S = SIZE * 2, r = this.radius(), accent = this.accent || "#3fb4ff";
    const toPx = (x: number, y: number): [number, number] => [((x - cx) / r + 1) * S / 2, ((y - cy) / r + 1) * S / 2];
    const inside = (px: number, py: number) => px >= -6 && py >= -6 && px <= S + 6 && py <= S + 6;
    g.clearRect(0, 0, S, S);
    this.marks = [];
    const map = this.ctx.world.map;
    const cells = this.img ? null : cellPicture(map); // a generated dungeon: drawn from its cells
    const src = cells ? cells.canvas : this.img;
    if (src && map.width > 0 && map.height > 0) {
      const sx = (cells ? cells.width : this.img!.naturalWidth) / map.width, sy = (cells ? cells.height : this.img!.naturalHeight) / map.height;
      g.imageSmoothingEnabled = !cells; // cell blocks stay sharp
      g.drawImage(src, (cx - r) * sx, (cy - r) * sy, 2 * r * sx, 2 * r * sy, 0, 0, S, S);
    } else {
      g.fillStyle = "#0b0f16";
      g.fillRect(0, 0, S, S);
    }
    const dot = (x: number, y: number, rad: number, fill: string, label: string) => {
      const [px, py] = toPx(x, y);
      if (!inside(px, py)) return;
      g.beginPath(); g.arc(px, py, rad, 0, Math.PI * 2); g.fillStyle = fill; g.fill(); g.lineWidth = 2; g.strokeStyle = "rgba(0,0,0,.85)"; g.stroke();
      if (label) this.marks.push({ px: px / 2, py: py / 2, label });
    };
    const info = this.info;
    if (info) {
      for (const npc of info.NPCs) dot(npc.Location.X, npc.Location.Y, 5, "#ffd75a", npcName(npc.Name)[0]);
      // Exits: arrows on the map, or on the rim pointing at the nearest ones beyond the edge.
      const off: [number, number, string][] = [];
      for (const mv of info.Movements) {
        const [px, py] = toPx(mv.Location.X, mv.Location.Y);
        if (px >= 0 && py >= 0 && px <= S && py <= S) { this.arrow(px, py, 0, accent, 7); this.marks.push({ px: px / 2, py: py / 2, label: `To ${mv.Title}` }); }
        else off.push([mv.Location.X, mv.Location.Y, mv.Title]);
      }
      off.sort((a, b) => Math.hypot(a[0] - cx, a[1] - cy) - Math.hypot(b[0] - cx, b[1] - cy));
      for (const [x, y, title] of off.slice(0, 3)) {
        const ang = Math.atan2(y - cy, x - cx);
        const rr = S / 2 - 12, px = S / 2 + Math.cos(ang) * rr, py = S / 2 + Math.sin(ang) * rr;
        this.arrow(px, py, ang + Math.PI / 2, accent, 9);
        this.marks.push({ px: px / 2, py: py / 2, label: `${title} · ${Math.round(Math.hypot(x - cx, y - cy))} cells` });
      }
    }
    // Objects in view (MiniMapDialog.DrawMiniMap: items, the dead, spells and sneaking objects skipped).
    const group = this.ctx.models.group;
    const petSuffix = `(${me})`;
    for (const ob of this.ctx.world.objects.values()) {
      if (ob === this.ctx.player.user || ob.race === ObjectType.Item || ob.dead || ob.race === ObjectType.Spell || ob.sneaking) continue;
      if (ob.race === ObjectType.Player && group.has(ob.name)) continue; // drawn as a diamond below
      const npc = ob.race === ObjectType.Merchant || ob.ai === 6;
      if (ob.race === ObjectType.Merchant && info) continue; // drawn from the map info
      const fill = ob.name.endsWith(petSuffix) ? accent : ob.race === ObjectType.Player ? "#9fd8ff" : npc ? "#ffd75a" : "#ff5a4a";
      dot(drawnX(ob), drawnY(ob), ob.race === ObjectType.Player ? 4.5 : 4, fill, npc ? npcName(ob.name)[0] : "");
    }
    // NPC quest markers ("!" / "?" over the NPC, MiniMapDialog's bold labels).
    if (this.markers.size) {
      g.font = "bold 18px Arial";
      g.textAlign = "center";
      g.lineWidth = 3;
      g.strokeStyle = "#000";
      const at = (id: number): [number, number] | null => {
        const npc = info?.NPCs.find((n) => n.ObjectID === id);
        if (npc) return [npc.Location.X, npc.Location.Y];
        const ob = this.ctx.world.objects.get(id);
        return ob ? [drawnX(ob), drawnY(ob)] : null;
      };
      for (const [id, m] of this.markers) {
        const p = at(id);
        if (!p) continue;
        const [px, py] = toPx(p[0], p[1]);
        if (!inside(px, py)) continue;
        g.strokeText(m.text, px, py - 8);
        g.fillStyle = m.colour;
        g.fillText(m.text, px, py - 8);
      }
    }
    // Group members on this map: accent diamonds (S.SendMemberLocation).
    for (const [name, loc] of group.locations) {
      if (name === me) continue;
      const [px, py] = toPx(loc.X, loc.Y);
      if (!inside(px, py)) continue;
      g.save(); g.translate(px, py); g.rotate(Math.PI / 4);
      g.fillStyle = accent; g.fillRect(-5, -5, 10, 10);
      g.lineWidth = 2; g.strokeStyle = "#fff"; g.strokeRect(-5, -5, 10, 10);
      g.restore();
      this.marks.push({ px: px / 2, py: py / 2, label: name });
    }
    // You: an arrow at the centre, pointing the way you face.
    this.arrow(S / 2, S / 2, angle, accent, 11, true);
  }

  /** A filled arrow at (px, py) pointing along `ang` (0 = up). */
  private arrow(px: number, py: number, ang: number, fill: string, size: number, me = false): void {
    const g = this.g;
    g.save(); g.translate(px, py); g.rotate(ang);
    g.beginPath(); g.moveTo(0, -size); g.lineTo(size * 0.7, size * 0.8); g.lineTo(0, size * 0.35); g.lineTo(-size * 0.7, size * 0.8); g.closePath();
    g.fillStyle = fill; g.fill(); g.lineWidth = 2; g.strokeStyle = me ? "#fff" : "rgba(0,0,0,.85)"; g.stroke();
    g.restore();
  }

  private hover(): void {
    if (!this.mouse) return;
    let best: Mark | null = null, bd = 9;
    for (const m of this.marks) { const d = Math.hypot(m.px - this.mouse.x, m.py - this.mouse.y); if (d < bd) { bd = d; best = m; } }
    const cell = this.cellAt(this.mouse.x, this.mouse.y);
    this.tip.hidden = !best && !cell;
    if (this.tip.hidden) return;
    setText(this.tip, best ? best.label : cell ? `${cell.x}, ${cell.y}` : "");
    const x = best ? best.px : this.mouse.x, y = best ? best.py : this.mouse.y;
    this.tip.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) translate(-50%, -130%)`;
  }
}
