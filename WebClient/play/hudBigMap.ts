// Big map mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// BigMapDialog: the map's real big-map picture (MMap.Lib[MapInfo.BigMap]) in a zoomable, draggable viewport with
// layers for NPCs (MapLinkIcon[BigMapIcon]), exits (to other maps; exits to the same map closer than ~40 px on screen
// share one marker with a count, regrouped on zoom, and the sidebar lists each such group once), monsters (portrait tokens at the centre of their
// spawns, ringed by how hard they are for your level; spawns closer than ~46 px on screen share a token showing the most
// numerous monster, a second portrait behind it when several kinds share it, and the total count; the grouping follows
// the zoom. Hovering a token shows its spawn circles; clicking a mixed token zooms in there, clicking a single-kind token
// selects that monster: all its circles stay up and the other monsters dim until it is clicked again or cleared), safe zones and your own waypoints (right-click), the lab camera as "you" (click the map to move it there),
// and a sidebar with the map picker (every map that has a big map), search, layer chips and NPC / exit / monster lists
// (click to focus; exits can be viewed or travelled to, which loads that map into the lab).
// Data: play/lab-data/maps.json, exported from a copy of the live Server.MirDB (2026-10-15; maps with a BigMap, their
// NPCs, movements, respawns and safe zones) and play/lab-data/monster-images.json (MonsterInfo.Name -> Image, the Monster
// value = Data\Monster\NNN.Lib, from the same kind of copy, 2026-10-16). Lab-only: publish-webclient.ps1 skips play/lab-data.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { mapCells } from "../src/game/mapFiles.ts";
import { libAsync, monsterLibPath } from "../src/game/libraries.ts";
import { monsterFrames } from "../src/game/frames.ts";
import { MirAction } from "../src/protocol/enums.generated.ts";
import { HudWindow, h, injectCss, rafCoalesce } from "./hudWindow.ts";

/** maps.json rows. npc: [name, x, y, showOnBigMap, bigMapIcon, canTeleportTo]; mv: [destIndex, destTitle, x, y, showOnBigMap, icon];
 *  sp: [monster, level, x, y, spread, count]; sz: [x, y, size]. */
export interface MapRow {
  i: number; f: string; t: string; b: number; m: number; l: number;
  npc: [string, number, number, number, number, number][]; mv: [number, string, number, number, number, number][];
  sp: [string, number, number, number, number, number][]; sz: [number, number, number][];
}
/** play/lab-data/maps.json, fetched once (the minimap shares it). */
let mapData: Promise<MapRow[]> | null = null;
export function loadMapData(): Promise<MapRow[]> {
  mapData ??= fetch("lab-data/maps.json").then((r) => r.json() as Promise<MapRow[]>).catch(() => []);
  return mapData;
}

/** Friendly town guards by monster AI (MonsterObject.GetMonster: 6 / 58 Guard, 57 TownArcher, 80 ConquestArcher, 113
 *  ArcherGuard). Names don't work: "Guard" is also in hostile ZumaGuardian / WoomaGuardian, and Royal_Archer / Sentry are
 *  guards. Their spawn level is 0 or 255, neither of which means anything to a player. */
export const GUARD_AI = new Set([6, 57, 58, 80, 113]);
/** play/lab-data/monsters.json: monster name -> AI, fetched once. */
let monsterAiP: Promise<Record<string, number>> | null = null;
export const loadMonsterAi = () => monsterAiP ??= fetch("lab-data/monsters.json").then((r) => r.json() as Promise<Record<string, number>>).catch(() => ({}));

/** play/lab-data/monster-images.json: monster name -> Monster image, fetched once. */
let monsterImages: Promise<Record<string, number>> | null = null;
function loadMonsterImages(): Promise<Record<string, number>> {
  monsterImages ??= fetch("lab-data/monster-images.json").then((r) => r.json() as Promise<Record<string, number>>).catch(() => ({}));
  return monsterImages;
}
/** A monster's standing picture facing down-right (direction 3; the first standing frame when the library has fewer
 *  directions), as an object URL; null = no picture. One load per Monster image. */
const portraits = new Map<number, Promise<string | null>>();
function monsterPortrait(image: number): Promise<string | null> {
  let p = portraits.get(image);
  if (!p) {
    p = (async () => {
      const path = monsterLibPath(image);
      const lib = path && await libAsync(path);
      if (!path || !lib) return null;
      const st = monsterFrames(image, 0, 0, lib.frames).get(MirAction.Standing);
      const first = st?.start ?? 0;
      const pic = (st && await libPicture(path, first + 3 * (st.count + st.skip))) || await libPicture(path, first);
      return pic?.url ?? null;
    })().catch(() => null);
    portraits.set(image, p);
  }
  return p;
}
const setPortrait = (el: HTMLElement, image: number | undefined) => {
  if (image !== undefined) void monsterPortrait(image).then((url) => { if (url) el.style.backgroundImage = `url(${url})`; });
};

export interface MapHost {
  map(): string;
  cam(): { x: number; y: number };
  level(): number;
  walk(x: number, y: number): void;
  travel(file: string): void;
}
type Layer = "NPCs" | "Exits" | "Monsters" | "Safe zones" | "Waypoints";
type Tab = "NPCs" | "Exits" | "Monsters";

/** Exits to the same map within `near` cells of each other, as one group at their centre (a doorway is often a row of
 *  link cells). `at` = the cells, for focus and for cycling through them. */
type ExitGroup = { title: string; x: number; y: number; at: [number, number][] };
function groupExits(mv: MapRow["mv"], near: number): ExitGroup[] {
  const groups: ExitGroup[] = [];
  for (const [, title, x, y] of mv) {
    const g = groups.find((q) => q.title === title && Math.hypot(q.x - x, q.y - y) < near);
    if (g) { g.at.push([x, y]); g.x += (x - g.x) / g.at.length; g.y += (y - g.y) / g.at.length; }
    else groups.push({ title, x, y, at: [[x, y]] });
  }
  return groups;
}
const pretty = (t: string) => t.replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/([A-Za-z])\(/g, "$1 (").replace(/\s+/g, " ").trim();
/** "Teleport_Gilbert" -> ["Gilbert", "Teleport"]; a leading "_" (hidden helper NPCs) keeps the rest. */
export const npcName = (n: string): [string, string] => { const p = n.split("_").filter(Boolean); return [pretty(p.pop() ?? n), p.map(pretty).join(" · ")]; };

const CSS = `
.hudm .mwin { --winh: 620px; width: 980px; }
.hudm .mwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .bm-ic { width: 14px; height: 14px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .mwin .mmain { flex: 1; min-height: 0; display: flex; }
.hudm .mwin .vp { position: relative; flex: 1; min-width: 0; overflow: hidden; cursor: grab; background: radial-gradient(ellipse at center, color-mix(in srgb, var(--pb) 70%, #000), #000);
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mwin .vp.drag { cursor: grabbing; }
.hudm .mwin .vp img { position: absolute; left: 0; top: 0; transform-origin: 0 0; image-rendering: auto; user-select: none; pointer-events: none;
  box-shadow: 0 0 0 1px var(--fr), 0 10px 40px rgba(0,0,0,.8); }
.hudm .mwin .ov { position: absolute; inset: 0; pointer-events: none; }
/* Markers sit in .mkl, which a pan only translates (the markers are built once per map / layer / focus change). */
.hudm .mwin .mkl { position: absolute; left: 0; top: 0; }
.hudm .mwin .mk { position: absolute; transform: translate(-50%, -50%); pointer-events: auto; cursor: pointer; }
.hudm .mwin .mk .lbl { position: absolute; left: 50%; top: 100%; transform: translateX(-50%); margin-top: 1px; white-space: nowrap; font-size: 10px; font-weight: 600; color: #fff;
  text-shadow: 0 0 2px #000, 0 1px 2px #000, 0 0 4px #000; pointer-events: none; }
.hudm .mwin.zoomedout .mk:not(:hover):not(.focus):not(.sel) .lbl { display: none; }
.hudm .mwin .npc i { display: block; width: 20px; height: 20px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 0 2px #000); }
.hudm .mwin .npc.dot i { width: 8px; height: 8px; border-radius: 50%; background: #ffd75a; border: 1px solid #000; }
.hudm .mwin .exit { width: 18px; height: 18px; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: #fff;
  background: color-mix(in srgb, var(--ac) 70%, #000); border: 1px solid #fff; box-shadow: 0 0 8px var(--glow); }
.hudm .mwin .exit .bm-ic { width: 11px; height: 11px; }
.hudm .mwin .exit em { position: absolute; right: -8px; top: -6px; min-width: 13px; height: 12px; padding: 0 3px; box-sizing: border-box; border-radius: 6px; font-style: normal;
  font-size: 8px; font-weight: 800; line-height: 11px; text-align: center; color: #fff; background: #000; border: 1px solid var(--ac); }
.hudm .mwin .zone { position: absolute; transform: translate(-50%, -50%); border-radius: 50%; pointer-events: none;
  border: 1px solid rgba(255,255,255,.35); background: rgba(160, 170, 190, .14); }
.hudm .mwin .zone.hide { display: none; }
.hudm .mwin .zone.easy { border-color: rgba(170, 180, 195, .5); background: rgba(150, 160, 175, .12); }
.hudm .mwin .zone.even { border-color: color-mix(in srgb, var(--ac) 70%, transparent); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .mwin .zone.hard { border-color: rgba(255, 150, 70, .7); background: rgba(255, 130, 50, .1); }
.hudm .mwin .zone.deadly { border-color: rgba(255, 70, 70, .75); background: rgba(255, 50, 50, .1); }
.hudm .mwin .zone.guard { border-color: rgba(111, 224, 138, .7); background: rgba(111, 224, 138, .1); }
/* Monster tokens: the monster's picture in a ring tinted by difficulty, and how many monsters it stands for. */
.hudm .mwin .mob { width: 28px; height: 28px; border-radius: 50%; z-index: 2; border: 2px solid #aab3c2;
  background: radial-gradient(circle at 50% 40%, #2c3442, #090c12 75%); box-shadow: 0 0 0 1px #000, 0 2px 5px rgba(0,0,0,.8); transition: opacity .15s, transform .12s; }
.hudm .mwin .mob i { position: absolute; inset: 1px; border-radius: 50%; background: center 40% / contain no-repeat; }
/* A mixed token: the second most numerous monster peeks out behind the first. */
.hudm .mwin .mob i.bk { inset: auto; left: -9px; top: -6px; width: 20px; height: 20px; z-index: -1; border: 1px solid currentColor;
  background-color: #0d1118; box-shadow: 0 0 0 1px #000; }
.hudm .mwin .mob em { position: absolute; right: -7px; bottom: -5px; min-width: 15px; height: 13px; padding: 0 3px; box-sizing: border-box; border-radius: 7px; font-style: normal;
  font-size: 9px; font-weight: 800; line-height: 12px; text-align: center; color: #fff; background: #000; border: 1px solid currentColor; }
.hudm .mwin .mob .lbl { margin-top: 3px; }
.hudm .mwin .mob.easy { border-color: #aab3c2; color: #aab3c2; } .hudm .mwin .mob.even { border-color: var(--ac); color: var(--ac); }
.hudm .mwin .mob.hard { border-color: #ffa060; color: #ffa060; } .hudm .mwin .mob.deadly { border-color: #ff6060; color: #ff6060; }
.hudm .mwin .mob.guard { border-color: #6fe08a; color: #6fe08a; }
.hudm .mwin .mob:hover, .hudm .mwin .mob.sel { z-index: 7; transform: translate(-50%, -50%) scale(1.25); box-shadow: 0 0 0 1px #000, 0 0 12px currentColor; }
.hudm .mwin .mob.dim { opacity: .3; }
.hudm .mwin .mob.dim:hover { opacity: 1; }
.hudm .mwin .selpill { display: flex; align-items: center; gap: 6px; padding: 4px 6px 4px 10px; border-radius: 14px; font-size: 11px; color: #fff; background: rgba(0,0,0,.6);
  border: 1px solid var(--ac); box-shadow: 0 0 10px rgba(0,0,0,.6); }
.hudm .mwin .selpill[hidden] { display: none; }
.hudm .mwin .selpill b { color: var(--ac); }
.hudm .mwin .selpill span { width: 18px; height: 18px; display: flex; align-items: center; justify-content: center; border-radius: 50%; cursor: pointer; color: var(--dim); }
.hudm .mwin .selpill span:hover { color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .mwin .safe { position: absolute; transform: translate(-50%, -50%); border-radius: 50%; pointer-events: none; border: 2px dashed rgba(90, 230, 130, .8); background: rgba(80, 220, 120, .1); }
.hudm .mwin .wp { color: #fff; font-size: 10px; font-weight: 800; }
.hudm .mwin .wp svg { width: 22px; height: 26px; filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .mwin .wp span { position: absolute; left: 0; right: 0; top: 4px; text-align: center; }
.hudm .mwin .me { pointer-events: none; z-index: 5; }
.hudm .mwin .me svg { width: 26px; height: 26px; filter: drop-shadow(0 0 6px var(--ac)) drop-shadow(0 0 2px #000); }
.hudm .mwin .me::before { content: ""; position: absolute; left: 50%; top: 50%; width: 46px; height: 46px; margin: -23px 0 0 -23px; border-radius: 50%;
  border: 2px solid var(--ac); opacity: 0; animation: hudm-ping 2s ease-out infinite; }
@keyframes hudm-ping { 0% { transform: scale(.3); opacity: .9; } 100% { transform: scale(1.4); opacity: 0; } }
.hudm .mwin .mk.focus { z-index: 6; }
.hudm .mwin .mk.focus::after { content: ""; position: absolute; left: 50%; top: 50%; width: 40px; height: 40px; margin: -20px 0 0 -20px; border-radius: 50%;
  border: 2px solid #fff; animation: hudm-ping 1s ease-out 3; }
/* Viewport chrome */
.hudm .mwin .bar { position: absolute; left: 10px; right: 10px; top: 10px; display: flex; gap: 8px; align-items: center; pointer-events: none; }
.hudm .mwin .bar > * { pointer-events: auto; }
.hudm .mwin .crumb { padding: 5px 12px; border-radius: 14px; font-size: 13px; font-weight: 700; color: #fff; background: rgba(0,0,0,.6); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent);
  box-shadow: 0 0 10px rgba(0,0,0,.6); }
.hudm .mwin .crumb small { margin-left: 8px; font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .mwin .zoom { margin-left: auto; display: flex; border-radius: 14px; overflow: hidden; border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); background: rgba(0,0,0,.6); }
.hudm .mwin .zoom div { width: 30px; height: 26px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-size: 15px; font-weight: 700; color: var(--tx); user-select: none; }
.hudm .mwin .zoom div + div { border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mwin .zoom div:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .mwin .zoom .z { width: 54px; font-size: 11px; cursor: default; }
.hudm .mwin .coords { position: absolute; left: 10px; bottom: 10px; padding: 4px 10px; border-radius: 12px; font: 600 11px ui-monospace, Consolas, monospace; color: var(--tx);
  background: rgba(0,0,0,.6); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); pointer-events: none; }
.hudm .mwin .legend { position: absolute; right: 10px; bottom: 10px; display: flex; gap: 10px; padding: 4px 10px; border-radius: 12px; font-size: 10px; color: var(--dim);
  background: rgba(0,0,0,.6); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); pointer-events: none; }
.hudm .mwin .legend i { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 4px; vertical-align: -1px; }
.hudm .mwin .loading { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; color: var(--dim); font-size: 13px; }
/* Sidebar */
.hudm .mwin .side { width: 280px; flex: none; display: flex; flex-direction: column; min-height: 0; padding: 12px 12px 10px; gap: 9px; }
.hudm .mwin .side h3 { margin: 0; font-size: 17px; color: #fff; }
.hudm .mwin .side .sub { font-size: 11px; color: var(--dim); margin-top: 2px; }
.hudm .mwin .side .sub b { color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .mwin input.in { width: 100%; box-sizing: border-box; height: 26px; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .mwin input.in:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .mwin input.in::placeholder { color: var(--dim); }
.hudm .mwin .row2 { display: flex; gap: 6px; }
.hudm .mwin .row2 .btn { height: 26px; flex: none; padding: 0 10px; white-space: nowrap; }
.hudm .mwin .chips { display: flex; flex-wrap: wrap; gap: 4px; }
.hudm .mwin .chip { height: 20px; padding: 0 8px; display: inline-flex; align-items: center; font-size: 10px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 10px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .mwin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .mwin .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .mwin .seg div { flex: 1; height: 24px; display: flex; align-items: center; justify-content: center; gap: 4px; cursor: pointer; user-select: none;
  font-size: 11px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .mwin .seg div + div { border-left: 1px solid var(--fr); }
.hudm .mwin .seg div i { font-style: normal; font-size: 10px; opacity: .7; }
.hudm .mwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 24%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .mwin .list { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mwin .li { display: flex; align-items: center; gap: 8px; padding: 5px 6px; border-radius: var(--rad); cursor: pointer; font-size: 12px; }
.hudm .mwin .li:hover { background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .mwin .li i { width: 18px; height: 18px; flex: none; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .mwin .li .t { flex: 1; min-width: 0; }
.hudm .mwin .li .t b { display: block; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mwin .li .t span { font-size: 10px; color: var(--dim); }
.hudm .mwin .li .btn { height: 20px; padding: 0 7px; font-size: 10px; }
.hudm .mwin .li i.pt { width: 26px; height: 26px; border-radius: 50%; background: center 40% / contain no-repeat, radial-gradient(circle at 50% 40%, #2c3442, #090c12 75%);
  image-rendering: auto; border: 1px solid var(--fr); }
.hudm .mwin .li.sel { background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 2px 0 0 var(--ac); }
.hudm .mwin .lv { font-size: 10px; font-weight: 700; padding: 1px 5px; border-radius: 6px; border: 1px solid; }
.hudm .mwin .lv.guard { color: #6fe08a; } .hudm .mwin .lv.easy { color: #aab3c2; } .hudm .mwin .lv.even { color: var(--ac); } .hudm .mwin .lv.hard { color: #ffa060; } .hudm .mwin .lv.deadly { color: #ff6060; }
.hudm .mwin .empty { padding: 20px 6px; text-align: center; color: var(--dim); font-size: 11px; }
`;

export class BigMapMock extends HudWindow {
  host: MapHost | null = null;
  private all: MapRow[] | null = null;
  private viewed: MapRow | null = null;
  private dims: { w: number; h: number } | null = null;
  private pic: { url: string; width: number; height: number } | null = null;
  private z = 1; private ox = 0; private oy = 0; private fitZ = 1;
  private layers = new Set<Layer>(["NPCs", "Exits", "Waypoints"]); // Monsters / Safe zones are opt-in (busy maps)
  private tab: Tab = "NPCs";
  private query = "";
  private waypoints = new Map<string, { x: number; y: number }[]>();
  private focus = "";
  /** The focused zone's cell (a monster focus marks the token holding that zone). */
  private focusAt = "";
  /** The selected monster: its spawn circles stay up and other monsters dim ("" = none). */
  private selMob = "";
  private mobImg: Record<string, number> = {};
  private mobAi: Record<string, number> = {};
  private selPill!: HTMLElement;
  private vp!: HTMLElement; private img!: HTMLImageElement; private ov!: HTMLElement; private coords!: HTMLElement;
  /** The marker layer (inside .ov) and its markers: cell, and for zones the size in cells, a minimum in px and the
   *  label shown once the zone is big enough. Positions are relative to the picture's origin at zoom `placedZ`. */
  private mkl!: HTMLElement;
  private marks: { el: HTMLElement; x: number; y: number; cells?: number; min?: number; lbl?: HTMLElement }[] = [];
  private placedZ = 0;
  private me!: HTMLElement;
  private meAt = "";
  /** The viewport's screen rect and the HUD scale, measured on mouseenter / mousedown (not on every move). */
  private vpBox: { left: number; top: number; k: number } | null = null;
  private over = false;
  private crumb!: HTMLElement; private zlabel!: HTMLElement; private side!: HTMLElement; private loading!: HTMLElement;
  private pending = 0;

  constructor() {
    super("mwin", 22, 20);
    injectCss("hudm-bigmap-css", CSS);
    this.build();
    window.setInterval(() => { if (this.open && this.viewed?.f === this.host?.map()) this.placeMe(); }, 400); // follow the lab camera
  }

  protected override shown(): void { void this.view(this.host?.map() ?? "0", true); }
  /** Hidden under the cursor gives no mouseleave: forget the hover and the measured box (the window may move). */
  protected override closed(): void { this.over = false; this.vpBox = null; }

  // — Data —
  private async data(): Promise<MapRow[]> {
    this.all ??= await loadMapData();
    return this.all;
  }

  /** View a map (by file name): its picture, size (from the .map file) and data. */
  private async view(file: string, fit: boolean): Promise<void> {
    const job = ++this.pending;
    const [all, imgs, ai] = await Promise.all([this.data(), loadMonsterImages(), loadMonsterAi()]);
    this.mobImg = imgs; this.mobAi = ai;
    const row = all.find((m) => m.f.toLowerCase() === file.toLowerCase()) ?? null;
    if (row !== this.viewed) this.selMob = "";
    this.viewed = row;
    this.focus = "";
    this.loading.hidden = false;
    this.loading.textContent = row ? "Loading map…" : `No big map for "${file}".`;
    this.img.hidden = true;
    this.renderSide();
    if (!row) { this.mkl.replaceChildren(); this.marks = []; return; }
    const [pic, cells] = await Promise.all([libPicture(Lib.MiniMap, row.b), mapCells(row.f).catch(() => null)]);
    if (job !== this.pending) return;
    this.pic = pic;
    this.dims = cells ? { w: cells.width, h: cells.height } : pic ? { w: pic.width, h: pic.height } : null;
    if (!pic || !this.dims) { this.loading.textContent = "This map has no picture."; return; }
    this.loading.hidden = true;
    this.img.src = pic.url;
    this.img.hidden = false;
    if (fit) this.fit();
    this.drawOverlay();
    this.applyView();
    this.renderSide();
  }

  // — Viewport —
  private build(): void {
    this.img = h("img");
    this.img.draggable = false;
    this.mkl = h("div", "mkl");
    this.ov = h("div", "ov", this.mkl);
    this.me = h("div", "mk me");
    this.me.innerHTML = `<svg viewBox="0 0 24 24"><path d="M12 2l7 19-7-4-7 4z" style="fill: var(--ac)" stroke="#fff" stroke-width="1.5" stroke-linejoin="round"/></svg>`;
    this.coords = h("div", "coords", "—");
    this.crumb = h("div", "crumb");
    this.zlabel = h("div", "z");
    const zbtn = (t: string, fn: () => void) => { const b = h("div", "", t); b.addEventListener("click", fn); return b; };
    const zoom = h("div", "zoom", zbtn("−", () => this.zoomBy(1 / 1.4)), this.zlabel, zbtn("+", () => this.zoomBy(1.4)), zbtn("⤢", () => { this.fit(); this.applyView(); }));
    const legend = h("div", "legend", ...[["easy", "#aab3c2", "Easy"], ["even", "var(--ac)", "Your level"], ["hard", "#ffa060", "Hard"], ["deadly", "#ff6060", "Deadly"], ["guard", "#6fe08a", "Guard"]]
      .map(([, c, t]) => { const i = h("i"); i.style.background = c!; return h("span", "", i, t!); }));
    this.loading = h("div", "loading", "Loading map…");
    this.selPill = h("div", "selpill");
    this.selPill.hidden = true;
    this.vp = h("div", "vp", this.img, this.ov, h("div", "bar", this.crumb, this.selPill, zoom), this.coords, legend, this.loading);
    this.side = h("div", "side");
    this.el.append(h("div", "panel", this.titleBar("WORLD MAP", "Close (B)"), h("div", "mmain", this.vp, this.side)));

    // Drag to pan, wheel to zoom at the cursor, click to move the lab camera, right-click for a waypoint.
    let drag: { x: number; y: number; moved: boolean } | null = null;
    this.vp.addEventListener("mousedown", (e) => {
      if (e.button !== 0 || (e.target as HTMLElement).closest(".mk, .zone, .bar")) return;
      drag = { x: e.clientX, y: e.clientY, moved: false };
      this.measure();
      this.vp.classList.add("drag");
      e.preventDefault();
    });
    this.vp.addEventListener("mouseenter", () => { this.over = true; this.measure(); });
    this.vp.addEventListener("mouseleave", () => { this.over = false; });
    // At most once a frame, with no layout reads: the viewport box is measured on mouseenter / mousedown.
    window.addEventListener("mousemove", rafCoalesce((e: MouseEvent) => {
      if (!this.open || (!this.over && !drag)) return; // closed or elsewhere: nothing to do on page-wide mouse moves
      const p = this.cellAt(e);
      if (p) this.coords.textContent = `${p.x}, ${p.y}`;
      if (!drag) return;
      const k = this.vpBox?.k ?? 1;
      const dx = (e.clientX - drag.x) / k, dy = (e.clientY - drag.y) / k;
      if (Math.abs(dx) + Math.abs(dy) > 2) drag.moved = true;
      this.ox += dx; this.oy += dy; drag.x = e.clientX; drag.y = e.clientY;
      this.applyView();
    }));
    window.addEventListener("mouseup", (e) => {
      if (!drag) return;
      const was = drag; drag = null;
      this.vp.classList.remove("drag");
      if (was.moved || !this.viewed || this.viewed.f !== this.host?.map()) return;
      const p = this.cellAt(e);
      if (p) { this.host.walk(p.x, p.y); this.placeMe(); }
    });
    this.vp.addEventListener("wheel", (e) => {
      e.preventDefault();
      const b = this.vpBox ?? this.measure();
      this.zoomBy(e.deltaY < 0 ? 1.2 : 1 / 1.2, (e.clientX - b.left) / b.k, (e.clientY - b.top) / b.k);
    }, { passive: false });
    this.vp.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      const p = this.cellAt(e);
      if (!p || !this.viewed) return;
      const list = this.waypoints.get(this.viewed.f) ?? [];
      if (list.length >= 9) list.shift();
      list.push(p);
      this.waypoints.set(this.viewed.f, list);
      this.layers.add("Waypoints");
      this.drawOverlay(); this.renderSide();
    });
  }

  /** The HUD layer is scaled: screen pixels per window pixel. */
  private screenScale(): number { return this.el.getBoundingClientRect().width / this.el.offsetWidth || 1; }
  /** Measures the viewport's screen position and the HUD scale (one layout read, kept until the next mouseenter /
   *  mousedown; the window cannot move while the mouse is over the viewport). */
  private measure(): { left: number; top: number; k: number } {
    const r = this.vp.getBoundingClientRect();
    return this.vpBox = { left: r.left, top: r.top, k: this.screenScale() };
  }

  private fit(): void {
    if (!this.pic) return;
    const w = this.vp.clientWidth, hgt = this.vp.clientHeight;
    this.fitZ = Math.min(w / this.pic.width, hgt / this.pic.height) * 0.94;
    this.z = this.fitZ;
    this.ox = (w - this.pic.width * this.z) / 2;
    this.oy = (hgt - this.pic.height * this.z) / 2;
  }

  private zoomBy(f: number, cx = this.vp.clientWidth / 2, cy = this.vp.clientHeight / 2): void {
    if (!this.pic) return;
    const nz = Math.min(Math.max(this.z * f, this.fitZ * 0.6), Math.max(4, this.fitZ * 8));
    this.ox = cx - (cx - this.ox) * (nz / this.z);
    this.oy = cy - (cy - this.oy) * (nz / this.z);
    this.z = nz;
    this.applyView();
  }

  /** Pans move the picture and the marker layer (transforms only); a zoom also re-places the existing markers. */
  private applyView(): void {
    this.img.style.transform = `translate(${this.ox}px, ${this.oy}px) scale(${this.z})`;
    this.mkl.style.transform = `translate(${this.ox}px, ${this.oy}px)`;
    this.zlabel.textContent = Math.round(this.z / this.fitZ * 100) + "%";
    this.el.classList.toggle("zoomedout", this.z < this.fitZ * 1.6);
    // A zoom re-places the markers; with monsters or exits shown it rebuilds them (they group by screen distance).
    if (this.z !== this.placedZ) { if (this.layers.has("Monsters") || this.layers.has("Exits")) this.drawOverlay(); else this.layout(); }
  }

  /** Cell -> viewport pixel, and back. */
  private toPx(x: number, y: number): [number, number] {
    const d = this.dims!, p = this.pic!;
    return [this.ox + (x + 0.5) / d.w * p.width * this.z, this.oy + (y + 0.5) / d.h * p.height * this.z];
  }
  /** Cell -> pixel in the marker layer (the picture's origin, at the current zoom). */
  private toRel(x: number, y: number): [number, number] {
    const d = this.dims!, p = this.pic!;
    return [(x + 0.5) / d.w * p.width * this.z, (y + 0.5) / d.h * p.height * this.z];
  }
  private cellAt(e: MouseEvent): { x: number; y: number } | null {
    if (!this.dims || !this.pic) return null;
    const b = this.vpBox ?? this.measure();
    const px = (e.clientX - b.left) / b.k, py = (e.clientY - b.top) / b.k;
    if (px < 0 || py < 0 || px > this.vp.clientWidth || py > this.vp.clientHeight) return null;
    const x = Math.floor((px - this.ox) / (this.pic.width * this.z) * this.dims.w), y = Math.floor((py - this.oy) / (this.pic.height * this.z) * this.dims.h);
    return x >= 0 && y >= 0 && x < this.dims.w && y < this.dims.h ? { x, y } : null;
  }

  private isGuard(name: string): boolean { return GUARD_AI.has(this.mobAi[name] ?? -1); }
  /** How hard a monster is for your level; friendly guards (by AI) are "guard". */
  private difficulty(level: number, name = ""): "easy" | "even" | "hard" | "deadly" | "guard" {
    if (name && this.isGuard(name)) return "guard";
    const d = level - (this.host?.level() ?? 40);
    return d < -8 ? "easy" : d <= 3 ? "even" : d <= 10 ? "hard" : "deadly";
  }

  /** Rebuilds the markers: on a map, layer, focus or waypoint change (pans and zooms only move them). */
  private drawOverlay(): void {
    const m = this.viewed;
    this.mkl.replaceChildren();
    this.marks = [];
    this.meAt = "";
    if (!m || !this.dims || !this.pic) return;
    const add = (el: HTMLElement, x: number, y: number, more?: { cells: number; min: number; lbl?: HTMLElement }) => {
      this.marks.push({ el, x, y, ...more });
      this.mkl.append(el);
      return el;
    };
    if (this.layers.has("Safe zones")) for (const [x, y, s] of m.sz) {
      add(h("div", "safe"), x, y, { cells: s * 2 + 1, min: 10 }).title = "Safe zone";
    }
    if (this.layers.has("Monsters")) this.drawMonsters(m, add);
    if (this.layers.has("Exits")) {
      const cellPx = this.pic.width * this.z / this.dims.w;
      for (const g of groupExits(m.mv, 40 / cellPx)) {
        const e = add(h("div", "mk exit"), g.x, g.y);
        e.append(svgIcon("M5 12h12M13 7l5 5-5 5"), h("span", "lbl", pretty(g.title)));
        if (g.at.length > 1) e.append(h("em", "", String(g.at.length)));
        e.title = `To ${pretty(g.title)}${g.at.length > 1 ? ` · ${g.at.length} exits here` : ""} · click to view it`;
        if (g.at.some(([x, y]) => this.focus === `e:${x},${y}`)) e.classList.add("focus");
        e.addEventListener("click", () => { const dest = this.all?.find((r) => r.t === g.title && r.b > 0); if (dest) void this.view(dest.f, true); });
      }
    }
    if (this.layers.has("NPCs")) for (const [raw, x, y, show, icon] of m.npc) {
      if (raw.startsWith("_")) continue; // hidden helper NPCs
      const [name, role] = npcName(raw);
      const n = add(h("div", "mk npc" + (show && icon ? "" : " dot")), x, y);
      const i = h("i");
      if (show && icon) void libPicture(Lib.MapLinkIcon, icon).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
      n.append(i, h("span", "lbl", name));
      n.title = role ? `${name} (${role})` : name;
      if (this.focus === "n:" + raw) n.classList.add("focus");
    }
    if (this.layers.has("Waypoints")) (this.waypoints.get(m.f) ?? []).forEach((p, i) => {
      const w = add(h("div", "mk wp"), p.x, p.y);
      w.style.transform = "translate(-50%, -100%)";
      w.innerHTML = `<svg viewBox="0 0 22 26"><path d="M11 25C11 25 2 15 2 9a9 9 0 0118 0c0 6-9 16-9 16z" style="fill: color-mix(in srgb, var(--ac) 80%, #000)" stroke="#fff" stroke-width="1.5"/></svg><span>${i + 1}</span>`;
      w.title = `Waypoint ${i + 1} · ${p.x}, ${p.y} · click to remove`;
      w.addEventListener("click", () => { this.waypoints.get(m.f)!.splice(i, 1); this.drawOverlay(); this.renderSide(); });
    });
    this.layout();
  }

  /** Monster tokens and their (hidden) spawn circles. Spawns closer than ~46 px on screen share a token at their
   *  centre (weighted by count) whatever their kind; hovering a token shows its circles, the selected monster shows all
   *  of its circles and the tokens holding it. */
  private drawMonsters(m: MapRow, add: (el: HTMLElement, x: number, y: number, more?: { cells: number; min: number; lbl?: HTMLElement }) => HTMLElement): void {
    const cellPx = this.pic!.width * this.z / this.dims!.w;
    const near = 46 / cellPx; // cells
    type Kind = { count: number; lv: number };
    type Group = { x: number; y: number; count: number; kinds: Map<string, Kind>; zones: HTMLElement[]; at: Set<string> };
    const groups: Group[] = [];
    for (const [name, lv, x, y, spread, count] of m.sp) {
      if (name === "?") continue;
      const zone = add(h("div", "zone hide " + this.difficulty(lv, name)), x, y, { cells: spread * 2 + 1, min: 12 });
      if (this.selMob === name) { zone.classList.remove("hide"); zone.classList.add("keep"); }
      const n = Math.max(1, count);
      let g = groups.find((q) => Math.hypot(q.x - x, q.y - y) < near);
      if (g) { g.x = (g.x * g.count + x * n) / (g.count + n); g.y = (g.y * g.count + y * n) / (g.count + n); g.count += n; }
      else groups.push(g = { x, y, count: n, kinds: new Map(), zones: [], at: new Set() });
      const k = g.kinds.get(name) ?? { count: 0, lv };
      k.count += n; k.lv = Math.max(k.lv, lv);
      g.kinds.set(name, k);
      g.zones.push(zone);
      g.at.add(`${name}@${x},${y}`);
    }
    for (const g of groups) {
      // The selected monster first, then hostile monsters (most numerous first), guards last; the ring is the hardest
      // hostile one's.
      const kinds = [...g.kinds].sort((a, b) => Number(b[0] === this.selMob) - Number(a[0] === this.selMob)
        || Number(this.isGuard(a[0])) - Number(this.isGuard(b[0])) || b[1].count - a[1].count);
      const top = kinds[0]![0];
      const hardest = Math.max(...kinds.map(([n, k]) => this.isGuard(n) ? -1 : k.lv));
      const mixed = kinds.length > 1;
      const i = h("i");
      setPortrait(i, this.mobImg[top]);
      const t = add(h("div", "mk mob " + (hardest < 0 ? "guard" : this.difficulty(hardest)), i, h("span", "lbl", pretty(top) + (mixed ? ` +${kinds.length - 1}` : ""))), g.x, g.y);
      if (mixed) { const bk = h("i", "bk"); setPortrait(bk, this.mobImg[kinds[1]![0]]); t.prepend(bk); }
      if (g.count > 1) t.append(h("em", "", String(g.count)));
      const lines = kinds.map(([n, k]) => `${pretty(n)} · ${this.isGuard(n) ? "guard" : `level ${k.lv}`} · ${k.count}`);
      t.title = mixed ? `${lines.join("\n")}\nClick to zoom in` : `${lines[0]}${g.zones.length > 1 ? ` in ${g.zones.length} spawns` : ""}\nClick to ${this.selMob === top ? "hide" : "show"} all its spawns`;
      if (this.selMob) t.classList.add(g.kinds.has(this.selMob) ? "sel" : "dim");
      if (this.focus.startsWith("m:") && g.at.has(`${this.focus.slice(2)}@${this.focusAt}`)) t.classList.add("focus");
      t.addEventListener("mouseenter", () => { for (const z of g.zones) z.classList.remove("hide"); });
      t.addEventListener("mouseleave", () => this.hideZones(g.zones));
      t.addEventListener("click", () => {
        if (mixed) this.zoomInAt(g.x, g.y);
        else this.selectMob(this.selMob === top ? "" : top);
      });
    }
  }

  /** Hides spawn circles again after a hover, except the selected monster's. */
  private hideZones(zones: HTMLElement[]): void {
    for (const z of zones) if (!z.classList.contains("keep")) z.classList.add("hide");
  }

  /** Zooms in twice as far, centred on a cell (a mixed monster token). */
  private zoomInAt(x: number, y: number): void {
    if (!this.pic || !this.dims) return;
    const [px, py] = this.toPx(x, y);
    this.ox += this.vp.clientWidth / 2 - px;
    this.oy += this.vp.clientHeight / 2 - py;
    this.zoomBy(2);
  }

  /** Selects a monster (its spawn circles stay up, the others dim; "" = none) and updates the pill and the list. */
  private selectMob(name: string): void {
    this.selMob = name;
    this.drawOverlay();
    this.renderSide();
  }

  private renderSelPill(): void {
    const name = this.selMob;
    this.selPill.hidden = !name;
    if (!name) return;
    const x = h("span", "", "✕");
    x.title = "Show no spawn circles";
    x.addEventListener("click", () => this.selectMob(""));
    this.selPill.replaceChildren("Spawns of ", h("b", "", pretty(name)), x);
  }

  /** Places the built markers for the current zoom (positions and zone sizes; no DOM rebuild). */
  private layout(): void {
    this.placedZ = this.z;
    if (!this.dims || !this.pic) return;
    const cellPx = this.pic.width * this.z / this.dims.w;
    for (const k of this.marks) {
      const [px, py] = this.toRel(k.x, k.y);
      k.el.style.left = px + "px"; k.el.style.top = py + "px";
      if (k.cells !== undefined) {
        const size = Math.max(k.min!, k.cells * cellPx);
        k.el.style.width = k.el.style.height = size + "px";
        if (k.lbl) k.lbl.hidden = size <= 46;
      }
    }
    this.meAt = "";
    this.placeMe();
  }

  /** The "you" marker: one element, moved only when the lab camera's cell changes (its ping keeps running). */
  private placeMe(): void {
    const m = this.viewed;
    if (!this.host || !m || !this.dims || !this.pic || m.f !== this.host.map()) { this.me.remove(); this.meAt = ""; return; }
    const c = this.host.cam(), x = Math.round(c.x), y = Math.round(c.y), key = `${x},${y}`;
    if (key === this.meAt && this.me.parentNode) return;
    this.meAt = key;
    const [px, py] = this.toRel(x, y);
    this.me.style.left = px + "px"; this.me.style.top = py + "px";
    if (this.me.parentNode !== this.mkl) this.mkl.append(this.me);
  }

  /** Centre the view on a cell (zooming in when far out) and pulse that marker. */
  private focusOn(x: number, y: number, key: string): void {
    if (!this.pic || !this.dims) return;
    if (this.z < this.fitZ * 2.2) this.z = this.fitZ * 2.2;
    const [px, py] = this.toPx(x, y);
    this.ox += this.vp.clientWidth / 2 - px;
    this.oy += this.vp.clientHeight / 2 - py;
    this.focus = key;
    this.focusAt = `${x},${y}`;
    this.drawOverlay();
    this.applyView();
  }

  // — Sidebar —
  private renderSide(): void {
    const m = this.viewed;
    const here = this.host && m && m.f === this.host.map();
    this.crumb.replaceChildren(m ? pretty(m.t) : "—", h("small", "", m ? (here ? "you are here" : "viewing") : ""));
    this.renderSelPill();
    this.side.replaceChildren();
    // Map picker: every map with a big map.
    const pick = h("input", "in");
    pick.placeholder = "Find a map…";
    pick.setAttribute("list", "hudm-maps");
    const dl = h("datalist");
    dl.id = "hudm-maps";
    for (const r of this.all ?? []) dl.append(Object.assign(h("option"), { value: `${pretty(r.t)} [${r.f}]` }));
    pick.addEventListener("keydown", (e) => e.stopPropagation());
    pick.addEventListener("change", () => { const f = /\[([^\]]+)\]$/.exec(pick.value)?.[1]; if (f) void this.view(f, true); });
    const mine = h("div", "btn", "My map");
    mine.addEventListener("click", () => { if (this.host) void this.view(this.host.map(), true); });
    this.side.append(h("div", "row2", pick, mine), dl);
    if (!m) { this.side.append(h("div", "empty", "Pick a map.")); return; }
    const c = this.host?.cam();
    const head = h("div", "", h("h3", "", pretty(m.t)), h("div", "sub", `${m.f}.map · ${this.dims ? `${this.dims.w} × ${this.dims.h} cells` : "…"}`),
      here && c ? h("div", "sub", "You are at ", h("b", "", `${Math.round(c.x)}, ${Math.round(c.y)}`), " · click the map to move") : h("div", "sub", "Click an exit to follow it"));
    this.side.append(head);
    if (!here) {
      const travel = h("div", "btn main", `Travel to ${pretty(m.t)}`);
      travel.style.cssText = "height: 26px; justify-content: center; color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb));";
      travel.addEventListener("click", () => { this.host?.travel(m.f); window.setTimeout(() => this.renderSide(), 1500); });
      this.side.append(travel);
    }
    const chips = h("div", "chips");
    for (const l of ["NPCs", "Exits", "Monsters", "Safe zones", "Waypoints"] as Layer[]) {
      const ch = h("div", "chip" + (this.layers.has(l) ? " on" : ""), l);
      ch.addEventListener("click", () => { if (this.layers.has(l)) this.layers.delete(l); else this.layers.add(l); this.drawOverlay(); this.renderSide(); });
      chips.append(ch);
    }
    const search = h("input", "in");
    search.placeholder = "Search NPCs, exits, monsters…";
    search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value; fillList(); });
    const npcs = m.npc.filter((n) => !n[0].startsWith("_"));
    const mobs = new Map<string, { lv: number; count: number; zones: [number, number][] }>();
    for (const [name, lv, x, y, , count] of m.sp) {
      if (name === "?") continue;
      const e = mobs.get(name) ?? { lv, count: 0, zones: [] };
      e.count += count; e.zones.push([x, y]); mobs.set(name, e);
    }
    const seg = h("div", "seg");
    for (const [t, n] of [["NPCs", npcs.length], ["Exits", groupExits(m.mv, 12).length], ["Monsters", mobs.size]] as [Tab, number][]) {
      const b = h("div", t === this.tab ? "on" : "", t, h("i", "", String(n)));
      b.addEventListener("click", () => { this.tab = t; this.renderSide(); });
      seg.append(b);
    }
    const list = h("div", "list");
    const fillList = () => {
      const q = this.query.trim().toLowerCase();
      list.replaceChildren();
      const add = (el: HTMLElement) => list.append(el);
      if (this.tab === "NPCs") for (const [raw, x, y, show, icon, tp] of npcs) {
        const [name, role] = npcName(raw);
        if (q && !(name + " " + role).toLowerCase().includes(q)) continue;
        const i = h("i");
        if (show && icon) void libPicture(Lib.MapLinkIcon, icon).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
        const li = h("div", "li", i, h("div", "t", h("b", "", name), h("span", "", `${role ? role + " · " : ""}${x}, ${y}`)));
        if (tp && here) { const b = h("div", "btn", "Teleport"); b.title = "Teleport to this NPC (costs gold on the real server)"; b.addEventListener("click", (e) => { e.stopPropagation(); this.host?.walk(x, y + 1); this.focusOn(x, y, "n:" + raw); }); li.append(b); }
        li.addEventListener("click", () => this.focusOn(x, y, "n:" + raw));
        add(li);
      }
      // One row per doorway: exits to the same map within 12 cells of each other.
      if (this.tab === "Exits") for (const g of groupExits(m.mv, 12)) {
        if (q && !g.title.toLowerCase().includes(q) && !pretty(g.title).toLowerCase().includes(q)) continue;
        const dest = this.all?.find((r) => r.t === g.title && r.b > 0);
        const [x0, y0] = g.at[0]!;
        const li = h("div", "li", svgIcon("M5 12h12M13 7l5 5-5 5"), h("div", "t", h("b", "", pretty(g.title)),
          h("span", "", g.at.length > 1 ? `${g.at.length} exits around ${Math.round(g.x)}, ${Math.round(g.y)}` : `exit at ${x0}, ${y0}`)));
        if (dest) { const b = h("div", "btn", "View"); b.addEventListener("click", (e) => { e.stopPropagation(); void this.view(dest.f, true); }); li.append(b); }
        li.addEventListener("click", () => this.focusOn(x0, y0, `e:${x0},${y0}`));
        add(li);
      }
      if (this.tab === "Monsters") for (const [name, e] of [...mobs].sort((a, b) => Number(this.isGuard(a[0])) - Number(this.isGuard(b[0])) || a[1].lv - b[1].lv)) {
        if (q && !name.toLowerCase().includes(q) && !pretty(name).toLowerCase().includes(q)) continue;
        const d = this.difficulty(e.lv, name);
        let next = 0;
        const pt = h("i", "pt");
        setPortrait(pt, this.mobImg[name]);
        const sel = this.selMob === name;
        const li = h("div", "li" + (sel ? " sel" : ""), pt, h("div", "t", h("b", "", pretty(name)), h("span", "", `${e.count} in ${e.zones.length} spawn${e.zones.length === 1 ? "" : "s"} · ${sel ? "click for the next" : "click to show them"}`)), h("span", "lv " + d, d === "guard" ? "Guard" : String(e.lv)));
        li.addEventListener("click", () => {
          const [x, y] = e.zones[next++ % e.zones.length]!;
          this.layers.add("Monsters");
          if (this.selMob !== name) { this.selMob = name; this.renderSide(); }
          this.focusOn(x, y, "m:" + name);
        });
        add(li);
      }
      if (!list.childElementCount) add(h("div", "empty", q ? "Nothing matches." : "Nothing here."));
    };
    fillList();
    const wps = this.waypoints.get(m.f) ?? [];
    const wpLine = h("div", "sub", wps.length ? `${wps.length} waypoint${wps.length === 1 ? "" : "s"} · right-click the map to add, click a pin to remove` : "Right-click the map to drop a waypoint");
    this.side.append(chips, search, seg, list, wpLine);
  }
}

function svgIcon(d: string): SVGElement {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", "bm-ic");
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
}
