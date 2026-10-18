// Ported from play/hudBigMap.ts (BigMapMock): the new UI's big map, the desktop's BigMapDialog (Client/MirScenes/Dialogs/
// BigMapDialog.cs; classic port src/ui/bigMap.ts) as a zoomable, draggable viewport with a sidebar. Changes from the mock:
//  - real data instead of play/lab-data: maps are keyed by MapIndex. The current map is CurrentMap (index, title,
//    MapInformation.BigMap; the loaded MapCells give its size until its record arrives, like BigMapDialog); any map is
//    GameInfo.map(index) (ClientMapInfo: Title, Width, Height, BigMap = MMap.Lib, NPCs, Movements) asked with
//    C.RequestMapInfo (GameInfo.requestMap). The picture is MMap.Lib[BigMap];
//  - the world map (S.WorldMapSetup via GameInfo.worldMap, only when Enabled): Prguse2 1360 + the additive clouds 1365,
//    each WorldMapIcon as a pin at the centre of its MapLinkIcon region (UseOffSet; the region lights up on hover like
//    OnlyDrawWhenActive), a click views that map;
//  - layers: NPCs (MapLinkIcon[Icon] when ShowOnBigMap), exits (Movements, grouped by screen distance; a click views the
//    destination), group members on your map (GroupState.locations / memberMaps, matched on the map title like
//    BigMapViewPort), monster spawns and safe zones from S.MapDetails (GameInfo.requestMapDetails / details(index), only
//    while ServerRules.MapDetailsEnabled; spawn tokens use ClientMonsterInfo Image / Level / AI), your waypoints
//    (right-click; local, per character in localStorage 'crystal.ui.waypoints', at most 9 per map);
//  - the player arrow (GameScene.User location and direction) on your own map; a click there walks
//    (GameWorld.walkTo = MapControl.AutoPath, the classic onWalk); other maps and the world are view-only;
//  - NPC search: the box filters the lists here; "Find" (or Enter with 3+ letters, 1 s apart) sends C.SearchMap and
//    S.SearchMapResult (through host.packets) views that map and selects the NPC, "Nothing found." otherwise;
//  - Teleport (C.TeleportToNPC) for NPCs with CanTeleportTo on your own map, after a popup with
//    GameScene.TeleportToNPCCost and a gold check against Inventory.gold (BigMapDialog.TeleportToNPC); the server moves
//    you (no local move);
//  - the map picker lists only maps the client knows: world-map icons, exit destinations, search results and maps
//    already viewed (Crystal sends no map list);
//  - hidden: the mock's "Travel to" (Crystal has no teleport to exits) and the full map picker;
//  - performance (Performance.md T2-1 / T2-2): markers are built once per map / layer / focus / waypoint change in a
//    layer that a pan only translates; a zoom re-places the cached nodes at once and regroups the grouped layers
//    (exits, monsters) once the wheel settles; the viewport rect is measured on pointerenter / pointerdown and pointer
//    moves are coalesced to one a frame, on the viewport only (no page-wide listeners); the arrow and group dots
//    follow on a 250 ms ticker that runs only while the window is shown; model events only mark it dirty.

import { MirAction } from "../../../protocol/enums.generated.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import type { ClientMapInfo, ClientMonsterInfo, ClientNPCInfo } from "../../../protocol/server.ts";
import { libAsync, monsterLibPath } from "../../../game/libraries.ts";
import { monsterFrames } from "../../../game/frames.ts";
import { serverRules } from "../../../game/serverRules.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange } from "../../../game/keybinds.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { debounce, h, injectCss, rafCoalesce, setStyle, setText } from "../core/dom.ts";
import { fmt, npcName, plural, pretty } from "../core/format.ts";
import { svg } from "../core/icons.ts";
import { button, chips, searchBox, segmented, type Btn, type Choice } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

/** The view key of the world map (map views are MapIndex values >= 0). */
const WORLD = -2;
/** Prguse2: the world map picture and its additive clouds (WorldMapImage). */
const WORLD_IMG = 1360, WORLD_CLOUDS = 1365;
const PREFS = "crystal.ui.bigmap";
const WAYPOINTS = "crystal.ui.waypoints";
const MAX_WAYPOINTS = 9;
/** BigMapDialog.Search: C.SearchMap at most once a second; the server ignores fewer than 3 characters. */
const SEARCH_MS = 1000, SEARCH_MIN = 3;
/** Friendly town guards by monster AI (MonsterObject.GetMonster: 6 / 58 Guard, 57 TownArcher, 80 ConquestArcher, 113
 *  ArcherGuard): ringed green, never "deadly". */
const GUARD_AI = new Set([6, 57, 58, 80, 113]);

type Layer = "npcs" | "exits" | "group" | "monsters" | "safe" | "waypoints";
type Tab = "npcs" | "exits" | "monsters";
type Difficulty = "easy" | "even" | "hard" | "deadly" | "guard";
const DETAIL_LAYERS: readonly Layer[] = ["monsters", "safe"];
const LAYERS: readonly Choice<Layer>[] = [
  { value: "npcs", label: "NPCs" }, { value: "exits", label: "Exits" }, { value: "group", label: "Group" },
  { value: "monsters", label: "Monsters", title: "Monster spawns: hover a token for its spawn areas" },
  { value: "safe", label: "Safe zones" }, { value: "waypoints", label: "Waypoints", title: "Right-click the map to drop one" },
];

/** A marker: its cell, and for areas the size in cells (centred) or a box from its top-left (world regions). */
interface Mark { el: HTMLElement; x: number; y: number; cells?: number; min?: number; box?: { w: number; h: number } }
/** Exits to the same map within `near` cells of each other, as one group at their centre (a doorway is often a row). */
interface ExitGroup { dest: number; title: string; x: number; y: number; at: [number, number][] }
/** One monster on the viewed map (S.MapDetails respawns of one MonsterIndex). */
interface MobRow { index: number; info: ClientMonsterInfo | undefined; name: string; lv: number; count: number; zones: [number, number][] }

function groupExits(mv: ClientMapInfo["Movements"], near: number): ExitGroup[] {
  const groups: ExitGroup[] = [];
  for (const m of mv) {
    const x = m.Location.X, y = m.Location.Y;
    const g = groups.find((q) => q.dest === m.Destination && Math.hypot(q.x - x, q.y - y) < near);
    if (g) { g.at.push([x, y]); g.x += (x - g.x) / g.at.length; g.y += (y - g.y) / g.at.length; }
    else groups.push({ dest: m.Destination, title: m.Title, x, y, at: [[x, y]] });
  }
  return groups;
}

/** A monster's standing picture facing down-right (direction 3; the first standing frame when the library has fewer
 *  directions), as an object URL; null = none. One load per Monster image (page-wide cache, like libPicture's). */
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
/** `under` = a background layer drawn beneath the portrait (a gradient: see DARK_FILL). */
function setPortrait(el: HTMLElement, info: ClientMonsterInfo | undefined, under = ""): void {
  if (info) void monsterPortrait(info.Image).then((url) => { if (url) el.style.backgroundImage = `url(${url})` + (under ? `, ${under}` : ""); });
}
function setLinkIcon(el: HTMLElement, icon: number): void {
  if (icon > 0) void libPicture(Lib.MapLinkIcon, icon).then((p) => { if (p) el.style.backgroundImage = `url(${p.url})`; });
}

/** The tokens' small dark fills as gradients, not solid colours: a phone browser's auto dark mode turned the solid
 *  ones (the count pills, the second-kind circle) white while it left gradients alone. */
const DARK_FILL = "linear-gradient(#0d1118, #0d1118)";

const ARROW = "M12 2l7 19-7-4-7 4z";
const PIN = "M11 25C11 25 2 15 2 9a9 9 0 0118 0c0 6-9 16-9 16z";

const CSS = `
.hudm .bmwin { --winh: 620px; width: 980px; }
.hudm .bmwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .bmwin .mmain { flex: 1; min-height: 0; display: flex; }
.hudm .bmwin .vp { position: relative; flex: 1; min-width: 0; overflow: hidden; cursor: grab; touch-action: none; user-select: none;
  background: radial-gradient(ellipse at center, color-mix(in srgb, var(--pb) 70%, #000), #000); border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .bmwin .vp.drag { cursor: grabbing; }
.hudm .bmwin .vp.walk { cursor: crosshair; }
.hudm .bmwin .vp.walk.drag { cursor: grabbing; }
.hudm .bmwin .vp img.pic { position: absolute; left: 0; top: 0; transform-origin: 0 0; user-select: none; pointer-events: none;
  box-shadow: 0 0 0 1px var(--fr), 0 10px 40px rgba(0,0,0,.8); }
.hudm .bmwin .vp img.clouds { box-shadow: none; mix-blend-mode: plus-lighter; }
.hudm .bmwin .ov { position: absolute; inset: 0; pointer-events: none; }
.hudm .bmwin .mkl { position: absolute; left: 0; top: 0; }
.hudm .bmwin .mk { position: absolute; transform: translate(-50%, -50%); pointer-events: auto; cursor: pointer; }
.hudm .bmwin .mk .ml { position: absolute; left: 50%; top: 100%; transform: translateX(-50%); margin-top: 1px; white-space: nowrap; font-size: 10px; font-weight: 600; color: #fff;
  text-shadow: 0 0 2px #000, 0 1px 2px #000, 0 0 4px #000; pointer-events: none; }
.hudm .bmwin.zoomedout .mk:not(:hover):not(.focus):not(.sel):not(.wpin) .ml { display: none; }
.hudm .bmwin .npc i { display: block; width: 20px; height: 20px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 0 2px #000); }
.hudm .bmwin .npc.dot i { width: 8px; height: 8px; border-radius: 50%; background: var(--gold); border: 1px solid #000; filter: none; }
.hudm .bmwin .exit { width: 18px; height: 18px; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: #fff;
  background: color-mix(in srgb, var(--ac) 70%, #000); border: 1px solid #fff; box-shadow: 0 0 6px var(--glow); }
.hudm .bmwin .exit svg { width: 11px; height: 11px; fill: none; stroke: currentColor; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
.hudm .bmwin .mk em { position: absolute; right: -8px; top: -6px; min-width: 13px; height: 12px; padding: 0 3px; border-radius: 6px; font-style: normal;
  font-size: 8px; font-weight: 800; line-height: 11px; text-align: center; color: #fff; background: linear-gradient(#000, #000); border: 1px solid currentColor; }
.hudm .bmwin .exit em { border-color: var(--ac); }
/* Spawn areas (hidden until a token is hovered or its monster selected) and safe zones: square, like the server's ranges. */
.hudm .bmwin .zone, .hudm .bmwin .safe { position: absolute; transform: translate(-50%, -50%); pointer-events: none; }
.hudm .bmwin .zone { border-radius: 22%; border: 1px solid rgba(255,255,255,.35); background: rgba(160, 170, 190, .14); }
.hudm .bmwin .zone.hide { display: none; }
.hudm .bmwin .zone.easy { border-color: rgba(170, 180, 195, .5); background: rgba(150, 160, 175, .12); }
.hudm .bmwin .zone.even { border-color: color-mix(in srgb, var(--ac) 70%, transparent); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .bmwin .zone.hard { border-color: color-mix(in srgb, var(--warn) 75%, transparent); background: color-mix(in srgb, var(--warn) 10%, transparent); }
.hudm .bmwin .zone.deadly { border-color: color-mix(in srgb, var(--bad) 75%, transparent); background: color-mix(in srgb, var(--bad) 10%, transparent); }
.hudm .bmwin .zone.guard { border-color: color-mix(in srgb, var(--ok) 70%, transparent); background: color-mix(in srgb, var(--ok) 10%, transparent); }
.hudm .bmwin .safe { border-radius: 3px; border: 2px dashed color-mix(in srgb, var(--ok) 80%, transparent); background: color-mix(in srgb, var(--ok) 10%, transparent); }
.hudm .bmwin .safe.start { border-style: solid; }
/* Monster tokens: the monster's picture in a ring tinted by difficulty, and how many monsters it stands for. */
.hudm .bmwin .mob { width: 28px; height: 28px; border-radius: 50%; z-index: 2; border: 2px solid #aab3c2; color: #aab3c2;
  background: radial-gradient(circle at 50% 40%, #2c3442, #090c12 75%); box-shadow: 0 0 0 1px #000, 0 2px 5px rgba(0,0,0,.8); transition: opacity .15s, transform .12s; }
.hudm .bmwin .mob i { position: absolute; inset: 1px; border-radius: 50%; background: center 40% / contain no-repeat; }
.hudm .bmwin .mob i.bk { inset: auto; left: -9px; top: -6px; width: 20px; height: 20px; z-index: -1; border: 1px solid currentColor; background-image: linear-gradient(#0d1118, #0d1118); box-shadow: 0 0 0 1px #000; }
.hudm .bmwin .mob em { right: -7px; top: auto; bottom: -5px; min-width: 15px; height: 13px; font-size: 9px; line-height: 12px; border-radius: 7px; }
.hudm .bmwin .mob .ml { margin-top: 3px; }
.hudm .bmwin .mob.even { border-color: var(--ac); color: var(--ac); }
.hudm .bmwin .mob.hard { border-color: var(--warn); color: var(--warn); } .hudm .bmwin .mob.deadly { border-color: var(--bad); color: var(--bad); }
.hudm .bmwin .mob.guard { border-color: var(--ok); color: var(--ok); }
.hudm .bmwin .mob:hover, .hudm .bmwin .mob.sel { z-index: 7; transform: translate(-50%, -50%) scale(1.25); }
.hudm .bmwin .mob.dim { opacity: .3; }
.hudm .bmwin .mob.dim:hover { opacity: 1; }
.hudm .bmwin .wp { color: #fff; font-size: 10px; font-weight: 800; transform: translate(-50%, -100%); }
.hudm .bmwin .wp svg { display: block; width: 22px; height: 26px; fill: color-mix(in srgb, var(--ac) 80%, #000); stroke: #fff; stroke-width: 1.5; }
.hudm .bmwin .wp span { position: absolute; left: 0; right: 0; top: 4px; text-align: center; }
.hudm .bmwin .gm { z-index: 4; cursor: default; }
.hudm .bmwin .gm i { display: block; width: 10px; height: 10px; transform: rotate(45deg); background: var(--ok); border: 1px solid #000; box-shadow: 0 0 0 1px #fff; }
.hudm .bmwin .me { pointer-events: none; z-index: 5; }
.hudm .bmwin .me svg { display: block; width: 24px; height: 24px; fill: var(--ac); stroke: #fff; stroke-width: 1.5; stroke-linejoin: round; }
.hudm .bmwin .me::before { content: ""; position: absolute; left: 50%; top: 50%; width: 46px; height: 46px; margin: -23px 0 0 -23px; border-radius: 50%;
  border: 2px solid var(--ac); opacity: 0; animation: hudm-bmping 2s ease-out infinite; }
@keyframes hudm-bmping { 0% { transform: scale(.3); opacity: .9; } 100% { transform: scale(1.4); opacity: 0; } }
.hudm .bmwin .mk.focus { z-index: 6; }
.hudm .bmwin .mk.focus::after { content: ""; position: absolute; left: 50%; top: 50%; width: 40px; height: 40px; margin: -20px 0 0 -20px; border-radius: 50%;
  border: 2px solid #fff; pointer-events: none; animation: hudm-bmping 1s ease-out 3; }
/* World map: a pin per icon; its MapLinkIcon region lights up while the pin is hovered. */
.hudm .bmwin .wreg { position: absolute; pointer-events: none; opacity: 0; transition: opacity .12s; background: 0 0 / 100% 100% no-repeat; }
.hudm .bmwin .wreg.on { opacity: 1; }
.hudm .bmwin .wpin { width: 14px; height: 14px; border-radius: 50%; background: var(--ac); border: 2px solid #fff; box-shadow: 0 0 0 1px #000, 0 0 8px var(--glow); }
.hudm .bmwin .wpin .ml { font-size: 11px; }
/* Viewport chrome */
.hudm .bmwin .bar { position: absolute; left: 10px; right: 10px; top: 10px; display: flex; gap: 8px; align-items: center; pointer-events: none; }
.hudm .bmwin .bar > * { pointer-events: auto; }
.hudm .bmwin .crumb { max-width: 55%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding: 5px 12px; border-radius: 14px; font-size: 13px; font-weight: 700; color: #fff;
  background: rgba(0,0,0,.65); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .bmwin .crumb small { margin-left: 8px; font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .bmwin .selpill { display: flex; align-items: center; gap: 6px; padding: 4px 6px 4px 10px; border-radius: 14px; font-size: 11px; color: #fff; background: rgba(0,0,0,.65);
  border: 1px solid var(--ac); white-space: nowrap; }
.hudm .bmwin .selpill b { color: var(--ac); }
.hudm .bmwin .selpill button { width: 18px; height: 18px; padding: 0; display: flex; align-items: center; justify-content: center; border-radius: 50%; border: 0; cursor: pointer;
  font: inherit; color: var(--dim); background: none; }
.hudm .bmwin .selpill button:hover { color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .bmwin .zoom { margin-left: auto; display: flex; border-radius: 14px; overflow: hidden; border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); background: rgba(0,0,0,.65); }
.hudm .bmwin .zoom button { width: 30px; height: 26px; padding: 0; display: flex; align-items: center; justify-content: center; cursor: pointer; font: inherit; font-size: 15px; font-weight: 700;
  color: var(--tx); border: 0; background: none; }
.hudm .bmwin .zoom > * + * { border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .bmwin .zoom button:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .bmwin .zoom span { width: 50px; display: flex; align-items: center; justify-content: center; font-size: 11px; font-variant-numeric: tabular-nums; }
.hudm .bmwin .coords { position: absolute; left: 10px; bottom: 10px; padding: 4px 10px; border-radius: 12px; font: 600 11px ui-monospace, Consolas, monospace; color: var(--tx);
  background: rgba(0,0,0,.65); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); pointer-events: none; }
.hudm .bmwin .legend { position: absolute; right: 10px; bottom: 10px; display: flex; gap: 10px; padding: 4px 10px; border-radius: 12px; font-size: 10px; color: var(--dim);
  background: rgba(0,0,0,.65); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); pointer-events: none; }
.hudm .bmwin .legend i { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 4px; vertical-align: -1px; }
.hudm .bmwin .loading { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 20px; text-align: center; color: var(--dim); font-size: 13px; pointer-events: none; }
/* Sidebar */
.hudm .bmwin .side { width: 280px; flex: none; display: flex; flex-direction: column; min-height: 0; padding: 12px 12px 10px; gap: 8px; }
.hudm .bmwin .side h3 { margin: 0; font-size: 16px; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .bmwin .side .sub { font-size: 11px; color: var(--dim); margin-top: 2px; }
.hudm .bmwin .side .sub b { color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .bmwin .row2 { display: flex; gap: 6px; align-items: center; }
.hudm .bmwin .row2 > select, .hudm .bmwin .row2 > .ui-search { flex: 1; min-width: 0; }
.hudm .bmwin .row2 .btn { flex: none; padding: 0 9px; }
.hudm .bmwin .list { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .bmwin .li { display: flex; align-items: center; gap: 8px; padding: 5px 6px; border-radius: var(--rad); cursor: pointer; font-size: 12px; }
.hudm .bmwin .li:hover { background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .bmwin .li.sel { background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 2px 0 0 var(--ac); }
.hudm .bmwin .li > i { width: 18px; height: 18px; flex: none; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .bmwin .li > svg { width: 16px; height: 16px; flex: none; color: var(--ac); }
.hudm .bmwin .li i.pt { width: 26px; height: 26px; border-radius: 50%; background: center 40% / contain no-repeat, radial-gradient(circle at 50% 40%, #2c3442, #090c12 75%);
  image-rendering: auto; border: 1px solid var(--fr); }
.hudm .bmwin .li .t { flex: 1; min-width: 0; }
.hudm .bmwin .li .t b { display: block; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .bmwin .li .t span { font-size: 10px; color: var(--dim); }
.hudm .bmwin .li .btn { height: 20px; padding: 0 7px; font-size: 10px; }
.hudm .bmwin .lv { font-size: 10px; font-weight: 700; padding: 1px 5px; border-radius: 6px; border: 1px solid; color: #aab3c2; }
.hudm .bmwin .lv.guard { color: var(--ok); } .hudm .bmwin .lv.even { color: var(--ac); } .hudm .bmwin .lv.hard { color: var(--warn); } .hudm .bmwin .lv.deadly { color: var(--bad); }
/* Narrow layout (a 1024 px windows layer). */
.hudm.narrow .bmwin { --winh: 560px; width: 900px; }
.hudm.narrow .bmwin .side { width: 240px; padding: 10px 10px 8px; }
`;

interface Prefs { layers: Layer[]; tab: Tab }

export class BigMapWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  // — View state —
  /** The viewed map (MapIndex), WORLD, or -1 before the first open. */
  private view = -1;
  private record: ClientMapInfo | null = null;
  /** The view's size in cells (world: the picture's pixels). */
  private dims: { w: number; h: number } | null = null;
  private pic: { url: string; width: number; height: number } | null = null;
  private picKey = "";
  private picJob = 0;
  private fitPending = false;
  private z = 1; private ox = 0; private oy = 0; private fitZ = 1;
  private layers: Set<Layer>;
  private tab: Tab;
  private query = "";
  /** The focused marker ("n:<ObjectID>", "e:<x>,<y>", "m:<MonsterIndex>") and the cell it was focused at. */
  private focus = ""; private focusAt = "";
  /** The selected NPC (ObjectID; 0 = none) and monster (MonsterIndex; 0 = none: all spawn areas hidden until hovered). */
  private selNpc = 0;
  private selMob = 0;
  /** S.SearchMapResult's map and NPC (ObjectID), selected once that map's record arrives. */
  private pendingNpc: { map: number; id: number } | null = null;
  private nextSearch = 0;
  /** Maps the client knows (MapIndex -> title) for the picker. */
  private readonly known = new Map<number, string>();
  /** Derived per view (renderSide), reused by the list. */
  private npcs: ClientNPCInfo[] = [];
  private exits: ExitGroup[] = [];
  private mobs: MobRow[] = [];
  private mobIndexes = new Set<number>();
  // — Markers —
  private marks: Mark[] = [];
  private placedZ = 0;
  /** The zoom the grouped layers (exits, monster tokens) were grouped at. */
  private groupedZ = 0;
  private meKey = "";
  private groupMarks: Mark[] = [];
  private groupKey = "";
  // — Pointer —
  private vpBox: { left: number; top: number; k: number; w: number; h: number } | null = null;
  /** A pan in progress: pointer, start (moved = past 3 px, set on the raw event so a quick drag is never a click) and
   *  the last applied position. */
  private pan: { id: number; sx: number; sy: number; x: number; y: number; moved: boolean } | null = null;
  /** A two-finger pinch (touch): its pointers and the last applied finger distance and midpoint (viewport px). */
  private pinch: { a: number; b: number; d: number; mx: number; my: number } | null = null;
  /** The pointers down on the viewport (client px), for the pinch. */
  private readonly pts = new Map<number, { x: number; y: number }>();
  private hoverCell = "";
  // — DOM —
  private readonly vp = h("div", "vp");
  private readonly img = h("img", "pic");
  private readonly clouds = h("img", "pic clouds");
  private readonly mkl = h("div", "mkl");
  private readonly me = h("div", "mk me", svg(ARROW, ""));
  private readonly coords = h("div", "coords", "—");
  private readonly crumb = h("div", "crumb");
  private readonly crumbText = h("span");
  private readonly crumbSub = h("small");
  private readonly zlabel = h("span", "", "100%");
  private readonly selPill = h("div", "selpill");
  private readonly legend: HTMLElement;
  private readonly loading = h("div", "loading", "Loading map…");
  private readonly picker = h("select", "fld");
  private readonly worldBtn: Btn;
  private readonly title = h("h3");
  private readonly sub1 = h("div", "sub");
  private readonly sub2 = h("div", "sub");
  private readonly layerChips: ReturnType<typeof chips<Layer>>;
  private readonly search: ReturnType<typeof searchBox>;
  /** The title bar's close cross: its hint names the live Bigmap key (render). */
  private readonly closeX: HTMLElement | null;
  private readonly findBtn: Btn;
  private readonly segBox = h("div");
  private readonly list = h("div", "list");
  private readonly wpLine = h("div", "sub");
  private readonly regroup: ReturnType<typeof debounce<[]>>;

  constructor(ui: ModernUiHost) {
    super(ui.rt, "bmwin", 22, 20);
    this.ui = ui;
    injectCss("win-bigmap", CSS);
    const prefs = loadPrefs();
    this.layers = new Set(prefs.layers);
    this.tab = prefs.tab;
    this.regroup = debounce(() => { if (this.open && this.z !== this.groupedZ) this.drawOverlay(); }, 160, this.signal);

    // — Viewport —
    this.img.draggable = false; this.img.alt = "";
    this.clouds.draggable = false; this.clouds.alt = "";
    this.img.hidden = this.clouds.hidden = true;
    const zbtn = (t: string, title: string, fn: () => void) => {
      const b = h("button", "", t);
      b.type = "button"; b.title = title;
      b.addEventListener("click", fn, { signal: this.signal });
      return b;
    };
    const zoom = h("div", "zoom", zbtn("−", "Zoom out (wheel)", () => this.zoomBy(1 / 1.4)), this.zlabel,
      zbtn("+", "Zoom in (wheel)", () => this.zoomBy(1.4)), zbtn("⤢", "Fit the map", () => { this.fit(); this.applyView(); }));
    this.selPill.hidden = true;
    this.crumb.append(this.crumbText, this.crumbSub);
    const dot = (cls: string, text: string) => { const i = h("i"); i.style.background = cls; return h("span", "", i, text); };
    this.legend = h("div", "legend", dot("#aab3c2", "Easy"), dot("var(--ac)", "Your level"), dot("var(--warn)", "Hard"), dot("var(--bad)", "Deadly"), dot("var(--ok)", "Guard"));
    this.legend.hidden = true;
    this.vp.append(this.img, this.clouds, h("div", "ov", this.mkl), h("div", "bar", this.crumb, this.selPill, zoom), this.coords, this.legend, this.loading);
    this.bindViewport();

    // — Sidebar —
    this.picker.title = "Maps you know: the world map's, exit destinations, search results and maps you viewed";
    this.picker.addEventListener("change", () => { const i = Number(this.picker.value); if (Number.isInteger(i) && i >= 0) this.setView(i, true); }, { signal: this.signal });
    const mine = button("My map", () => this.setView(this.ui.ctx.models.currentMap.index, true), { title: "Back to the map you are on" });
    this.worldBtn = button("World", () => this.setView(WORLD, true), { title: "The world map" });
    this.layerChips = chips(LAYERS, this.layers, (sel) => { this.layers = sel; this.savePrefs(); this.drawOverlay(); this.renderSide(); });
    this.search = searchBox("Search NPCs, exits, monsters", (q) => { this.query = q; this.renderList(); }, { signal: this.signal });
    this.search.input.maxLength = 150; // Globals.MaxChatLength
    this.search.input.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.repeat) this.serverSearch(); }, { signal: this.signal });
    this.findBtn = button("Find", () => this.serverSearch(), { icon: "search", title: `Search every map for an NPC (${SEARCH_MIN}+ letters)` });
    const side = h("div", "side",
      h("div", "row2", this.picker, mine.el, this.worldBtn.el),
      h("div", "", this.title, this.sub1, this.sub2),
      this.layerChips.el,
      h("div", "row2", this.search.el, this.findBtn.el),
      this.segBox, this.list, this.wpLine);
    const titleBar = this.titleBar("WORLD MAP", "Close");
    this.closeX = titleBar.querySelector<HTMLElement>(".x");
    this.el.append(h("div", "panel", titleBar, h("div", "mmain", this.vp, side)));

    // — Models (only mark dirty: a closed window costs nothing) —
    const m = ui.ctx.models, gi = m.gameInfo, cm = m.currentMap;
    let own = cm.index;
    const offs = [
      gi.onChange((kind, index) => {
        if (kind === "worldMap") { this.learnWorld(); this.invalidate(); }
        else if (kind === "map") {
          const rec = gi.map(index);
          if (rec) this.learn(index, rec.Title);
          if (index === this.view || index === cm.index) this.invalidate();
        } else if (kind === "mapDetails") { if (index === this.view) this.invalidate(); }
        else if (kind === "monster") { if (this.mobIndexes.has(index) && this.layers.has("monsters")) this.invalidate(); }
      }),
      cm.changed.on((what) => {
        if (what !== "map") return;
        if (cm.index >= 0) this.learn(cm.index, cm.title);
        // Follow a map change while looking at the map you were on (teleport, exit, death).
        if (this.open && (this.view === own || this.view < 0)) this.setView(cm.index, true);
        else this.invalidate();
        own = cm.index;
      }),
      serverRules(ui.ctx.session).changed.on(() => this.invalidate()),
      onKeyBindsChange(() => this.invalidate()),
      ui.packets.on((p) => {
        if (p.type !== "SearchMapResult") return;
        if (p.MapIndex === -1 && p.NPCIndex === 0) { // GameScene.SearchMapResult: nothing
          void this.ui.rt.popups.confirm({ title: "Search", text: "Nothing found.", buttons: "ok", kind: "info" });
          return;
        }
        this.learn(p.MapIndex, gi.map(p.MapIndex)?.Title ?? "");
        this.pendingNpc = { map: p.MapIndex, id: p.NPCIndex };
        if (!this.open) this.toggle(true);
        this.setView(p.MapIndex, true);
      }),
    ];
    this.signal.addEventListener("abort", () => { for (const off of offs) off(); }, { once: true });
    this.everyShown(250, () => this.follow());
    this.learnWorld();
  }

  protected override shown(): void {
    // BigMapDialog.Show: TargetMyLocation.
    const cm = this.ui.ctx.models.currentMap;
    this.setView(cm.index, true);
  }
  /** Closed under the cursor gives no pointerleave: forget the hover, the drag and the measured box (the window may move). */
  protected override closed(): void { this.vpBox = null; this.pan = null; this.pinch = null; this.pts.clear(); this.vp.classList.remove("drag"); this.regroup.cancel(); }

  protected override render(): void {
    if (this.closeX) {
      const close = keyLabel(KeybindOptions.Bigmap);
      this.closeX.title = `Close${close ? ` (${close})` : ""} · Esc closes the front window, Shift+Esc all`;
    }
    if (this.view !== -1) this.refresh();
  }

  // — Known maps —

  private learn(index: number, title: string): void {
    if (index < 0) return;
    if (title || !this.known.has(index)) this.known.set(index, title || this.known.get(index) || `Map ${index}`);
  }
  private learnWorld(): void {
    for (const icon of this.ui.ctx.models.gameInfo.worldMap.Icons) this.learn(icon.MapIndex, this.ui.ctx.models.gameInfo.map(icon.MapIndex)?.Title ?? icon.Title);
  }

  private detailsOn(): boolean { return serverRules(this.ui.ctx.session).rules?.MapDetailsEnabled ?? false; }
  private isOwn(): boolean { return this.view >= 0 && this.view === this.ui.ctx.models.currentMap.index; }

  // — View —

  /** BigMapDialog.SetTargetMap (WORLD = OpenWorldMap). */
  private setView(index: number, fit: boolean): void {
    if (index < 0 && index !== WORLD) return;
    const gi = this.ui.ctx.models.gameInfo;
    if (index !== this.view) { this.selMob = 0; this.selNpc = 0; }
    this.view = index;
    this.focus = "";
    this.fitPending ||= fit;
    if (index >= 0) {
      if (!gi.map(index)) gi.requestMap(index);
      if (this.detailsOn()) gi.requestMapDetails(index);
    }
    this.refresh();
  }

  /** Picks up the viewed record / picture / details and redraws the overlay and the sidebar. */
  private refresh(): void {
    const ctx = this.ui.ctx, gi = ctx.models.gameInfo, cm = ctx.models.currentMap;
    let picKey = "", dims: { w: number; h: number } | null = null, lib = "", index = 0, missing = "";
    if (this.view === WORLD) {
      this.record = null;
      if (gi.worldMap.Enabled) { picKey = "w"; lib = Lib.Prguse2; index = WORLD_IMG; }
      else missing = "There is no world map on this server.";
    } else {
      const rec = gi.map(this.view) ?? null;
      this.record = rec;
      const own = this.isOwn();
      const big = rec ? rec.BigMap : own ? cm.bigMapIndex : 0;
      dims = rec ? { w: rec.Width, h: rec.Height } : own && ctx.world.map.width > 0 ? { w: ctx.world.map.width, h: ctx.world.map.height } : null;
      if (rec) this.learn(this.view, rec.Title);
      if (big > 0) { picKey = "m" + big; lib = Lib.MiniMap; index = big; }
      else if (rec || own) missing = "This map has no big map.";
      if (dims && (dims.w <= 0 || dims.h <= 0)) dims = null;
    }
    if (picKey !== this.picKey) {
      this.picKey = picKey;
      this.pic = null;
      this.img.hidden = this.clouds.hidden = true;
      const job = ++this.picJob;
      if (picKey) {
        void libPicture(lib, index).then((p) => {
          if (job !== this.picJob || this.signal.aborted) return;
          if (!p) { this.picKey = ""; this.showLoading("This map has no picture."); return; }
          this.pic = p;
          this.img.src = p.url;
          this.img.hidden = false;
          if (this.view === WORLD) {
            this.dims = { w: p.width, h: p.height };
            void libPicture(Lib.Prguse2, WORLD_CLOUDS).then((c) => { if (c && job === this.picJob) { this.clouds.src = c.url; this.clouds.hidden = false; } });
          }
          if (this.open) this.refresh();
        });
      }
    }
    if (this.view !== WORLD) this.dims = dims;
    // Retry S.MapDetails while missing or incomplete (the model dedupes and spaces the requests).
    if (this.view >= 0 && this.detailsOn() && !gi.details(this.view)?.complete) gi.requestMapDetails(this.view);
    const ready = !!(this.pic && this.dims);
    if (!ready) this.showLoading(missing || (this.view >= 0 && !this.record && !this.isOwn() ? "Asking the server for this map…" : "Loading map…"));
    else this.loading.hidden = true;
    if (ready && this.fitPending) { this.fitPending = false; this.fit(); }
    // S.SearchMapResult: select its NPC once the record is here.
    if (this.pendingNpc && this.record && this.pendingNpc.map === this.view) {
      const id = this.pendingNpc.id;
      const npc = this.record.NPCs.find((n) => n.ObjectID === id);
      this.pendingNpc = null;
      if (npc) { this.selNpc = npc.ObjectID; this.tab = "npcs"; if (ready) this.focusOn(npc.Location.X, npc.Location.Y, "n:" + npc.ObjectID, false); }
    }
    this.vp.classList.toggle("walk", this.isOwn());
    this.drawOverlay();
    this.applyView();
    this.renderSide();
  }

  private showLoading(text: string): void {
    setText(this.loading, text);
    this.loading.hidden = false;
  }

  // — Viewport input —

  private bindViewport(): void {
    const opts = { signal: this.signal };
    const onChrome = (t: EventTarget | null) => t instanceof Element && !!t.closest(".mk, .bar");
    this.vp.addEventListener("pointerenter", () => this.measure(), opts);
    this.vp.addEventListener("pointerleave", () => { if (!this.pan) { this.vpBox = null; this.setHover(null); } }, opts);
    this.vp.addEventListener("pointerdown", (e) => {
      // A second finger while one pans: a pinch (the pan becomes a drag, so lifting it never walks)
      if (this.pan && !this.pinch && e.pointerType !== "mouse" && e.pointerId !== this.pan.id) {
        const p = this.pts.get(this.pan.id);
        if (!p) return;
        this.pan.moved = true;
        this.pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
        try { this.vp.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ }
        const g = this.pinchGeom(p, { x: e.clientX, y: e.clientY });
        this.pinch = { a: this.pan.id, b: e.pointerId, ...g };
        e.preventDefault();
        return;
      }
      if (e.button !== 0 || onChrome(e.target) || this.pan) return;
      this.measure();
      this.pan = { id: e.pointerId, sx: e.clientX, sy: e.clientY, x: e.clientX, y: e.clientY, moved: false };
      this.pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      try { this.vp.setPointerCapture(e.pointerId); } catch { /* pointer already gone */ }
      this.vp.classList.add("drag");
      e.preventDefault();
    }, opts);
    // One write a frame at most; no layout reads (the box is measured on pointerenter / pointerdown).
    const move = rafCoalesce((x: number, y: number) => {
      if (!this.open) return;
      const d = this.pan;
      if (d) {
        const k = this.vpBox?.k ?? 1;
        if (d.moved) { this.ox += (x - d.x) / k; this.oy += (y - d.y) / k; d.x = x; d.y = y; this.applyView(); }
      }
      this.setHover(this.cellAt(x, y));
    }, this.signal);
    // The pinch: zoom by the change in finger distance around their midpoint, and pan with the midpoint.
    const pinchMove = rafCoalesce(() => {
      const pi = this.pinch;
      if (!pi || !this.open) return;
      const p = this.pts.get(pi.a), q = this.pts.get(pi.b);
      if (!p || !q) return;
      const g = this.pinchGeom(p, q);
      this.ox += g.mx - pi.mx; this.oy += g.my - pi.my;
      this.zoomBy(pi.d > 0 ? g.d / pi.d : 1, g.mx, g.my);
      pi.d = g.d; pi.mx = g.mx; pi.my = g.my;
    }, this.signal);
    this.vp.addEventListener("pointermove", (e) => {
      const pt = this.pts.get(e.pointerId);
      if (pt) { pt.x = e.clientX; pt.y = e.clientY; }
      if (this.pinch) { if (pt) pinchMove(); return; }
      if (!this.open || (!this.vpBox && !this.pan)) return;
      const d = this.pan;
      if (d && !d.moved && Math.abs(e.clientX - d.sx) + Math.abs(e.clientY - d.sy) > 3) d.moved = true;
      move(e.clientX, e.clientY);
    }, opts);
    const end = (e: PointerEvent, click: boolean) => {
      this.pts.delete(e.pointerId);
      const pi = this.pinch;
      if (pi && (e.pointerId === pi.a || e.pointerId === pi.b)) {
        this.pinch = null;
        // The panning finger stays down: it pans on from where it is now (no jump)
        const d0 = this.pan, p = d0 && d0.id !== e.pointerId ? this.pts.get(d0.id) : undefined;
        if (d0 && p) { d0.x = p.x; d0.y = p.y; return; }
      }
      const d = this.pan;
      if (!d || d.id !== e.pointerId) return;
      this.pan = null;
      this.vp.classList.remove("drag");
      if (!click || d.moved || !this.isOwn()) return;
      const c = this.cellAt(e.clientX, e.clientY);
      if (c) this.ui.ctx.world.walkTo({ X: c.x, Y: c.y }); // MapControl.AutoPath (BigMapViewPort.OnMouseClick)
    };
    this.vp.addEventListener("pointerup", (e) => end(e, true), opts);
    this.vp.addEventListener("pointercancel", (e) => end(e, false), opts);
    this.vp.addEventListener("wheel", (e) => {
      e.preventDefault();
      const b = this.vpBox ?? this.measure();
      this.zoomBy(e.deltaY < 0 ? 1.2 : 1 / 1.2, (e.clientX - b.left) / b.k, (e.clientY - b.top) / b.k);
    }, { passive: false, signal: this.signal });
    this.vp.addEventListener("contextmenu", (e) => {
      e.preventDefault();
      if (onChrome(e.target) || this.view < 0) return;
      const c = this.cellAt(e.clientX, e.clientY);
      if (c) this.addWaypoint(c.x, c.y);
    }, opts);
  }

  /** Two fingers' distance and midpoint, in viewport px. */
  private pinchGeom(p: { x: number; y: number }, q: { x: number; y: number }): { d: number; mx: number; my: number } {
    const b = this.vpBox ?? this.measure();
    return { d: Math.hypot(q.x - p.x, q.y - p.y) / b.k, mx: ((p.x + q.x) / 2 - b.left) / b.k, my: ((p.y + q.y) / 2 - b.top) / b.k };
  }

  /** Measures the viewport's screen box and the total scale once (pointerenter / pointerdown / wheel). */
  private measure(): { left: number; top: number; k: number; w: number; h: number } {
    const r = this.vp.getBoundingClientRect();
    const w = this.vp.clientWidth, hgt = this.vp.clientHeight;
    return this.vpBox = { left: r.left, top: r.top, k: w ? r.width / this.vp.offsetWidth || 1 : 1, w, h: hgt };
  }

  private cellAt(clientX: number, clientY: number): { x: number; y: number } | null {
    if (!this.dims || !this.pic) return null;
    const b = this.vpBox ?? this.measure();
    const px = (clientX - b.left) / b.k, py = (clientY - b.top) / b.k;
    if (px < 0 || py < 0 || px > b.w || py > b.h) return null;
    const x = Math.floor((px - this.ox) / (this.pic.width * this.z) * this.dims.w), y = Math.floor((py - this.oy) / (this.pic.height * this.z) * this.dims.h);
    return x >= 0 && y >= 0 && x < this.dims.w && y < this.dims.h ? { x, y } : null;
  }

  private setHover(c: { x: number; y: number } | null): void {
    const key = c && this.view !== WORLD ? `${c.x}, ${c.y}` : "—";
    if (key === this.hoverCell) return;
    this.hoverCell = key;
    setText(this.coords, key);
  }

  private fit(): void {
    if (!this.pic) return;
    const w = this.vp.clientWidth || 600, hgt = this.vp.clientHeight || 500;
    this.fitZ = Math.min(w / this.pic.width, hgt / this.pic.height) * 0.94;
    this.z = this.fitZ;
    this.ox = (w - this.pic.width * this.z) / 2;
    this.oy = (hgt - this.pic.height * this.z) / 2;
  }

  private zoomBy(f: number, cx?: number, cy?: number): void {
    if (!this.pic) return;
    const b = this.vpBox;
    cx ??= (b?.w ?? this.vp.clientWidth) / 2;
    cy ??= (b?.h ?? this.vp.clientHeight) / 2;
    const nz = Math.min(Math.max(this.z * f, this.fitZ * 0.6), Math.max(4, this.fitZ * 8));
    this.ox = cx - (cx - this.ox) * (nz / this.z);
    this.oy = cy - (cy - this.oy) * (nz / this.z);
    this.z = nz;
    this.applyView();
  }

  /** A pan writes two transforms; a zoom also re-places the cached markers now and regroups exits / monsters once the
   *  wheel settles (they group by screen distance). */
  private applyView(): void {
    setStyle(this.img, "transform", `translate(${this.ox}px, ${this.oy}px) scale(${this.z})`);
    if (!this.clouds.hidden) setStyle(this.clouds, "transform", `translate(${this.ox}px, ${this.oy}px) scale(${this.z})`);
    setStyle(this.mkl, "transform", `translate(${this.ox}px, ${this.oy}px)`);
    setText(this.zlabel, Math.round(this.z / (this.fitZ || 1) * 100) + "%");
    this.el.classList.toggle("zoomedout", this.z < this.fitZ * 1.6);
    if (this.z !== this.placedZ) {
      this.layout();
      if (this.view !== WORLD && (this.layers.has("exits") || (this.layers.has("monsters") && this.detailsOn()))) this.regroup();
    }
  }

  private toPx(x: number, y: number): [number, number] {
    const [rx, ry] = this.toRel(x, y);
    return [this.ox + rx, this.oy + ry];
  }
  /** Cell -> pixel in the marker layer (the picture's origin at the current zoom). */
  private toRel(x: number, y: number): [number, number] {
    const d = this.dims!, p = this.pic!;
    return [(x + 0.5) / d.w * p.width * this.z, (y + 0.5) / d.h * p.height * this.z];
  }

  // — Markers —

  private difficulty(info: ClientMonsterInfo | undefined): Difficulty {
    if (!info) return "easy";
    if (GUARD_AI.has(info.AI)) return "guard";
    const d = info.Level - this.ui.ctx.player.level;
    return d < -8 ? "easy" : d <= 3 ? "even" : d <= 10 ? "hard" : "deadly";
  }

  private add(el: HTMLElement, x: number, y: number, more?: Partial<Mark>): HTMLElement {
    this.marks.push({ el, x, y, ...more });
    this.mkl.append(el);
    return el;
  }

  /** Rebuilds the markers: on a map, layer, focus, details or waypoint change (pans and zooms only move them). */
  private drawOverlay(): void {
    this.mkl.replaceChildren();
    this.marks = [];
    this.groupMarks = [];
    this.groupKey = "";
    this.meKey = "";
    this.legend.hidden = true;
    if (!this.dims || !this.pic) { this.placedZ = 0; return; }
    this.groupedZ = this.z;
    if (this.view === WORLD) this.drawWorld();
    else this.drawMap();
    this.layout();
  }

  private drawWorld(): void {
    for (const icon of this.ui.ctx.models.gameInfo.worldMap.Icons) {
      const reg = h("div", "wreg");
      reg.hidden = true;
      const title = this.known.get(icon.MapIndex) || icon.Title;
      const pin = h("div", "mk wpin", h("span", "ml", pretty(title)));
      pin.hidden = true;
      pin.title = `${pretty(title)} · click to view it`;
      pin.addEventListener("pointerenter", () => reg.classList.add("on"));
      pin.addEventListener("pointerleave", () => reg.classList.remove("on"));
      pin.addEventListener("click", () => this.setView(icon.MapIndex, true));
      const regMark: Mark = { el: reg, x: 0, y: 0, box: { w: 0, h: 0 } };
      const pinMark: Mark = { el: pin, x: 0, y: 0 };
      this.marks.push(regMark, pinMark);
      this.mkl.append(reg, pin);
      // UseOffSet: the icon is a region of the world picture at its offset.
      void libPicture(Lib.MapLinkIcon, icon.ImageIndex).then((p) => {
        if (!p || !reg.isConnected) return;
        reg.style.backgroundImage = `url(${p.url})`;
        regMark.x = p.x; regMark.y = p.y; regMark.box = { w: p.width, h: p.height };
        pinMark.x = p.x + p.width / 2; pinMark.y = p.y + p.height / 2;
        reg.hidden = pin.hidden = false;
        this.placeMark(regMark); this.placeMark(pinMark);
      });
    }
  }

  private drawMap(): void {
    const rec = this.record, gi = this.ui.ctx.models.gameInfo;
    const cellPx = this.pic!.width * this.z / this.dims!.w;
    const details = this.detailsOn() ? gi.details(this.view) : undefined;
    if (details && this.layers.has("safe")) for (const s of details.safeZones) {
      const el = this.add(h("div", "safe" + (s.StartPoint ? " start" : "")), s.Location.X, s.Location.Y, { cells: s.Size * 2 + 1, min: 10 });
      el.title = s.StartPoint ? "Safe zone (start point)" : "Safe zone";
    }
    if (details && this.layers.has("monsters")) { this.drawMonsters(details.respawns, cellPx); this.legend.hidden = false; }
    if (rec && this.layers.has("exits")) for (const g of groupExits(rec.Movements, 40 / cellPx)) {
      const e = this.add(h("div", "mk exit", svg("arrowRight", ""), h("span", "ml", pretty(g.title))), g.x, g.y);
      if (g.at.length > 1) e.append(h("em", "", String(g.at.length)));
      e.title = `To ${pretty(g.title)}${g.at.length > 1 ? ` · ${g.at.length} exits here` : ""} · click to view it`;
      if (g.at.some(([x, y]) => this.focus === `e:${x},${y}`)) e.classList.add("focus");
      e.addEventListener("click", () => { this.learn(g.dest, g.title); this.setView(g.dest, true); });
    }
    if (rec && this.layers.has("npcs")) for (const npc of rec.NPCs) {
      const [name, role] = npcName(npc.Name);
      const icon = npc.ShowOnBigMap && npc.Icon > 0;
      const i = h("i");
      if (icon) setLinkIcon(i, npc.Icon);
      const n = this.add(h("div", "mk npc" + (icon ? "" : " dot"), i, h("span", "ml", name)), npc.Location.X, npc.Location.Y);
      n.title = role ? `${name} (${role})` : name;
      if (this.focus === "n:" + npc.ObjectID || this.selNpc === npc.ObjectID) n.classList.add("focus");
      n.addEventListener("click", () => this.selectNpc(npc));
    }
    if (this.layers.has("waypoints")) this.waypoints().forEach(([x, y], i) => {
      const w = this.add(h("div", "mk wp", svg(PIN, "", "0 0 22 26"), h("span", "", String(i + 1))), x, y);
      w.title = `Waypoint ${i + 1} · ${x}, ${y} · click to remove`;
      w.addEventListener("click", () => this.removeWaypoint(i));
    });
  }

  /** Monster tokens and their (hidden) spawn areas. Spawns closer than ~46 px on screen share a token at their centre
   *  (weighted by count); hovering shows its areas, the selected monster shows all of its areas and dims the others. */
  private drawMonsters(respawns: readonly { MonsterIndex: number; Location: { X: number; Y: number }; Spread: number; Count: number }[], cellPx: number): void {
    const gi = this.ui.ctx.models.gameInfo;
    const near = 46 / cellPx;
    type Kind = { count: number; info: ClientMonsterInfo | undefined };
    type Group = { x: number; y: number; count: number; kinds: Map<number, Kind>; zones: HTMLElement[]; at: Set<string> };
    const groups: Group[] = [];
    for (const r of respawns) {
      const info = gi.monster(r.MonsterIndex);
      const x = r.Location.X, y = r.Location.Y;
      const zone = this.add(h("div", "zone hide " + this.difficulty(info)), x, y, { cells: r.Spread * 2 + 1, min: 12 });
      if (this.selMob === r.MonsterIndex) zone.classList.replace("hide", "keep");
      const n = Math.max(1, r.Count);
      let g = groups.find((q) => Math.hypot(q.x - x, q.y - y) < near);
      if (g) { g.x = (g.x * g.count + x * n) / (g.count + n); g.y = (g.y * g.count + y * n) / (g.count + n); g.count += n; }
      else groups.push(g = { x, y, count: n, kinds: new Map(), zones: [], at: new Set() });
      const k = g.kinds.get(r.MonsterIndex) ?? { count: 0, info };
      k.count += n;
      g.kinds.set(r.MonsterIndex, k);
      g.zones.push(zone);
      g.at.add(`${r.MonsterIndex}@${x},${y}`);
    }
    const guard = (k: Kind) => !!k.info && GUARD_AI.has(k.info.AI);
    for (const g of groups) {
      // The selected monster first, then hostile ones (most numerous first), guards last; the ring is the hardest hostile one's.
      const kinds = [...g.kinds].sort((a, b) => Number(b[0] === this.selMob) - Number(a[0] === this.selMob)
        || Number(guard(a[1])) - Number(guard(b[1])) || b[1].count - a[1].count);
      const [top, topKind] = kinds[0]!;
      let hardest: ClientMonsterInfo | undefined;
      for (const [, k] of kinds) if (!guard(k) && k.info && (!hardest || k.info.Level > hardest.Level)) hardest = k.info;
      const ring = hardest ? this.difficulty(hardest) : kinds.some(([, k]) => guard(k)) ? "guard" : "easy";
      const mixed = kinds.length > 1;
      const i = h("i");
      setPortrait(i, topKind.info);
      const t = this.add(h("div", "mk mob " + ring, i, h("span", "ml", mobName(top, topKind.info) + (mixed ? ` +${kinds.length - 1}` : ""))), g.x, g.y);
      if (mixed) { const bk = h("i", "bk"); setPortrait(bk, kinds[1]![1].info, DARK_FILL); t.prepend(bk); }
      if (g.count > 1) t.append(h("em", "", String(g.count)));
      const lines = kinds.map(([idx, k]) => `${mobName(idx, k.info)} · ${guard(k) ? "guard" : k.info ? `level ${k.info.Level}` : "level ?"} · ${k.count}`);
      t.title = mixed ? `${lines.join("\n")}\nClick to zoom in`
        : `${lines[0]}${g.zones.length > 1 ? ` in ${g.zones.length} spawns` : ""}\nClick to ${this.selMob === top ? "hide" : "show"} all its spawns`;
      if (this.selMob) t.classList.add(g.kinds.has(this.selMob) ? "sel" : "dim");
      if (this.focus.startsWith("m:") && g.at.has(`${this.focus.slice(2)}@${this.focusAt}`)) t.classList.add("focus");
      t.addEventListener("pointerenter", () => { for (const z of g.zones) z.classList.remove("hide"); });
      t.addEventListener("pointerleave", () => { for (const z of g.zones) if (!z.classList.contains("keep")) z.classList.add("hide"); });
      t.addEventListener("click", () => { if (mixed) this.zoomInAt(g.x, g.y); else this.selectMob(this.selMob === top ? 0 : top); });
    }
  }

  private zoomInAt(x: number, y: number): void {
    if (!this.pic || !this.dims) return;
    const [px, py] = this.toPx(x, y);
    const b = this.vpBox;
    this.ox += (b?.w ?? this.vp.clientWidth) / 2 - px;
    this.oy += (b?.h ?? this.vp.clientHeight) / 2 - py;
    this.zoomBy(2);
  }

  private selectMob(index: number): void {
    this.selMob = index;
    this.drawOverlay();
    this.renderSide();
  }

  private selectNpc(npc: ClientNPCInfo): void {
    this.selNpc = this.selNpc === npc.ObjectID ? 0 : npc.ObjectID;
    if (this.selNpc) { this.tab = "npcs"; this.focusOn(npc.Location.X, npc.Location.Y, "n:" + npc.ObjectID); }
    else this.drawOverlay();
    this.renderSide();
  }

  /** Places one built marker for the current zoom. */
  private placeMark(k: Mark): void {
    if (!this.dims || !this.pic) return;
    const sx = this.pic.width * this.z / this.dims.w, sy = this.pic.height * this.z / this.dims.h;
    if (k.box) {
      setStyle(k.el, "left", k.x * sx + "px"); setStyle(k.el, "top", k.y * sy + "px");
      setStyle(k.el, "width", k.box.w * sx + "px"); setStyle(k.el, "height", k.box.h * sy + "px");
      return;
    }
    const [px, py] = this.toRel(k.x, k.y);
    setStyle(k.el, "left", px + "px"); setStyle(k.el, "top", py + "px");
    if (k.cells !== undefined) {
      setStyle(k.el, "width", Math.max(k.min ?? 0, k.cells * sx) + "px");
      setStyle(k.el, "height", Math.max(k.min ?? 0, k.cells * sy) + "px");
    }
  }

  /** Places every built marker for the current zoom (no DOM rebuild). */
  private layout(): void {
    this.placedZ = this.z;
    if (!this.dims || !this.pic) return;
    for (const k of this.marks) this.placeMark(k);
    for (const k of this.groupMarks) this.placeMark(k);
    this.meKey = "";
    this.follow();
  }

  /** The arrow and the group members on your own map (250 ms ticker while shown, and after a layout): DOM writes only
   *  when the cell / direction / roster changes. */
  private follow(): void {
    const user = this.ui.ctx.player.user;
    if (!user || !this.isOwn() || !this.dims || !this.pic) {
      if (this.me.parentNode) this.me.remove();
      this.meKey = "";
      if (this.groupMarks.length) { for (const g of this.groupMarks) g.el.remove(); this.groupMarks = []; this.groupKey = ""; }
    } else {
      const x = user.location.X, y = user.location.Y, key = `${x},${y},${user.direction}`;
      if (key !== this.meKey || !this.me.parentNode) {
        this.meKey = key;
        const [px, py] = this.toRel(x, y);
        setStyle(this.me, "left", px + "px"); setStyle(this.me, "top", py + "px");
        setStyle(this.me, "transform", `translate(-50%, -50%) rotate(${(user.direction & 7) * 45}deg)`);
        if (this.me.parentNode !== this.mkl) this.mkl.append(this.me);
        setText(this.sub2, `You are at ${x}, ${y} · click the map to walk there`);
      }
      this.syncGroup(user.name);
    }
  }

  /** Group members on this map (BigMapViewPort: GroupMembersMap matched against the map title, S.SendMemberLocation). */
  private syncGroup(me: string): void {
    const ctx = this.ui.ctx, group = ctx.models.group;
    const title = this.ui.ctx.models.currentMap.title;
    const on = this.layers.has("group");
    let key = "";
    if (on) for (const [name, loc] of group.locations) if (name !== me && group.memberMaps.get(name) === title) key += `${name}@${loc.X},${loc.Y};`;
    if (key === this.groupKey) return;
    this.groupKey = key;
    for (const g of this.groupMarks) g.el.remove();
    this.groupMarks = [];
    if (!key) return;
    for (const [name, loc] of group.locations) {
      if (name === me || group.memberMaps.get(name) !== title) continue;
      const el = h("div", "mk gm", h("i"), h("span", "ml", name));
      el.title = `${name} · ${loc.X}, ${loc.Y}`;
      const mk: Mark = { el, x: loc.X, y: loc.Y };
      this.groupMarks.push(mk);
      this.mkl.append(el);
      this.placeMark(mk);
    }
  }

  /** Centre the view on a cell (zooming in when far out) and pulse that marker. */
  private focusOn(x: number, y: number, key: string, redraw = true): void {
    if (!this.pic || !this.dims) return;
    if (this.z < this.fitZ * 2.2) this.z = this.fitZ * 2.2;
    const [px, py] = this.toPx(x, y);
    const b = this.vpBox;
    this.ox += (b?.w ?? this.vp.clientWidth) / 2 - px;
    this.oy += (b?.h ?? this.vp.clientHeight) / 2 - py;
    this.focus = key;
    this.focusAt = `${x},${y}`;
    if (!redraw) return; // refresh() draws next
    this.drawOverlay();
    this.applyView();
  }

  // — Sidebar —

  private renderSide(): void {
    const gi = this.ui.ctx.models.gameInfo, cm = this.ui.ctx.models.currentMap;
    const world = this.view === WORLD, own = this.isOwn(), rec = this.record;
    const details = this.detailsOn();
    // Title, crumb
    const title = world ? "World map" : pretty(rec?.Title ?? (own ? cm.title : this.known.get(this.view) ?? "")) || "—";
    setText(this.crumbText, title);
    setText(this.crumbSub, world ? "" : own ? "you are here" : "viewing");
    setText(this.title, title);
    setText(this.sub1, world ? "Click a region to view its map"
      : this.dims ? `${this.dims.w} × ${this.dims.h} cells${rec ? ` · ${plural(rec.NPCs.length, "NPC")}` : ""}` : "…");
    const user = this.ui.ctx.player.user;
    if (own && user) setText(this.sub2, `You are at ${user.location.X}, ${user.location.Y} · click the map to walk there`);
    else setText(this.sub2, world ? "" : "Click an exit to view where it leads");
    this.renderPicker();
    this.worldBtn.el.hidden = !gi.worldMap.Enabled;
    // Layer chips: the detail layers only while the server sends S.MapDetails.
    this.layerChips.el.hidden = world;
    LAYERS.forEach((c, i) => { (this.layerChips.el.children[i] as HTMLElement).hidden = DETAIL_LAYERS.includes(c.value) && !details; });

    // Derived lists
    this.npcs = world || !rec ? [] : rec.NPCs; // only ShowOnBigMap NPCs arrive (PlayerObject.CheckMapInfo); all are listed, like BigMapNPCRow
    this.exits = world || !rec ? [] : groupExits(rec.Movements, 12);
    const mobs = new Map<number, MobRow>();
    const det = details && !world ? gi.details(this.view) : undefined;
    if (det) for (const r of det.respawns) {
      let row = mobs.get(r.MonsterIndex);
      if (!row) {
        const info = gi.monster(r.MonsterIndex);
        mobs.set(r.MonsterIndex, row = { index: r.MonsterIndex, info, name: mobName(r.MonsterIndex, info), lv: info?.Level ?? 0, count: 0, zones: [] });
      }
      row.count += r.Count;
      row.zones.push([r.Location.X, r.Location.Y]);
    }
    const guard = (r: MobRow) => !!r.info && GUARD_AI.has(r.info.AI);
    this.mobs = [...mobs.values()].sort((a, b) => Number(guard(a)) - Number(guard(b)) || a.lv - b.lv);
    this.mobIndexes = new Set(mobs.keys());
    if (!details && this.tab === "monsters") this.tab = "npcs";
    this.renderSelPill();

    if (world) this.segBox.replaceChildren();
    else {
      const choices: Choice<Tab>[] = [
        { value: "npcs", label: "NPCs", hint: String(this.npcs.length) },
        { value: "exits", label: "Exits", hint: String(this.exits.length) },
      ];
      if (details) choices.push({ value: "monsters", label: "Monsters", hint: det?.complete === false ? "…" : String(this.mobs.length) });
      this.segBox.replaceChildren(segmented(choices, this.tab, (t) => { this.tab = t; this.savePrefs(); this.renderList(); }).el);
    }
    const wps = world ? [] : this.waypoints();
    setText(this.wpLine, world ? "" : wps.length ? `${plural(wps.length, "waypoint")} · right-click to add, click a pin to remove` : "Right-click the map to drop a waypoint");
    this.renderList();
  }

  private renderPicker(): void {
    const rows = [...this.known].sort((a, b) => a[1].localeCompare(b[1]));
    const opts: HTMLOptionElement[] = [];
    const head = h("option", "", this.view === WORLD ? "World map" : "Maps you know…");
    head.value = "";
    opts.push(head);
    for (const [index, title] of rows) {
      const o = h("option", "", pretty(title));
      o.value = String(index);
      opts.push(o);
    }
    this.picker.replaceChildren(...opts);
    this.picker.value = this.view >= 0 && this.known.has(this.view) ? String(this.view) : "";
  }

  private renderSelPill(): void {
    const sel = this.selMob ? this.mobs.find((r) => r.index === this.selMob) : undefined;
    const name = this.selMob ? sel?.name ?? mobName(this.selMob, this.ui.ctx.models.gameInfo.monster(this.selMob)) : "";
    this.selPill.hidden = !name;
    if (!name) return;
    const x = h("button", "", "×");
    x.type = "button";
    x.title = "Show no spawn areas";
    x.addEventListener("click", () => this.selectMob(0));
    this.selPill.replaceChildren("Spawns of ", h("b", "", name), x);
  }

  private renderList(): void {
    const q = this.query.trim().toLowerCase();
    const own = this.isOwn();
    const rows: HTMLElement[] = [];
    const findBtnOff = q.length < SEARCH_MIN;
    this.findBtn.setDisabled(findBtnOff, `Type at least ${SEARCH_MIN} letters to search every map`);
    if (this.view === WORLD) {
      for (const icon of this.ui.ctx.models.gameInfo.worldMap.Icons) {
        const title = pretty(this.known.get(icon.MapIndex) || icon.Title);
        if (q && !title.toLowerCase().includes(q)) continue;
        const li = h("div", "li", svg("map", ""), h("div", "t", h("b", "", title)));
        li.addEventListener("click", () => this.setView(icon.MapIndex, true));
        rows.push(li);
      }
    } else if (this.tab === "npcs") {
      const cost = this.ui.ctx.models.gameInfo.teleportToNPCCost;
      for (const npc of this.npcs) {
        const [name, role] = npcName(npc.Name);
        if (q && !`${name} ${role}`.toLowerCase().includes(q)) continue;
        const i = h("i");
        if (npc.Icon > 0) setLinkIcon(i, npc.Icon);
        const li = h("div", "li" + (this.selNpc === npc.ObjectID ? " sel" : ""), i,
          h("div", "t", h("b", "", name), h("span", "", `${role ? role + " · " : ""}${npc.Location.X}, ${npc.Location.Y}`)));
        if (own && npc.CanTeleportTo) {
          const b = button("Teleport", (e) => { e.stopPropagation(); this.teleport(npc); }, { title: `Teleport to ${name} for ${fmt(cost)} gold` });
          li.append(b.el);
        }
        li.addEventListener("click", () => this.selectNpc(npc));
        rows.push(li);
      }
    } else if (this.tab === "exits") {
      for (const g of this.exits) {
        const title = pretty(g.title);
        if (q && !title.toLowerCase().includes(q) && !g.title.toLowerCase().includes(q)) continue;
        const [x0, y0] = g.at[0]!;
        const li = h("div", "li", svg("arrowRight", ""), h("div", "t", h("b", "", title),
          h("span", "", g.at.length > 1 ? `${g.at.length} exits around ${Math.round(g.x)}, ${Math.round(g.y)}` : `exit at ${x0}, ${y0}`)));
        const view = button("View", (e) => { e.stopPropagation(); this.learn(g.dest, g.title); this.setView(g.dest, true); }, { title: `View ${title}` });
        li.append(view.el);
        li.addEventListener("click", () => { this.layers.add("exits"); this.layerChips.set(this.layers); this.focusOn(x0, y0, `e:${x0},${y0}`); });
        rows.push(li);
      }
    } else {
      const det = this.ui.ctx.models.gameInfo.details(this.view);
      for (const r of this.mobs) {
        if (q && !r.name.toLowerCase().includes(q)) continue;
        const d = this.difficulty(r.info);
        const pt = h("i", "pt");
        setPortrait(pt, r.info);
        const sel = this.selMob === r.index;
        let next = 0;
        const li = h("div", "li" + (sel ? " sel" : ""), pt,
          h("div", "t", h("b", "", r.name), h("span", "", `${fmt(r.count)} in ${plural(r.zones.length, "spawn")} · ${sel ? "click for the next" : "click to show them"}`)),
          h("span", "lv " + d, d === "guard" ? "Guard" : r.info ? String(r.lv) : "?"));
        li.addEventListener("click", () => {
          const [x, y] = r.zones[next++ % r.zones.length]!;
          if (!this.layers.has("monsters")) { this.layers.add("monsters"); this.layerChips.set(this.layers); this.savePrefs(); }
          if (this.selMob !== r.index) { this.selMob = r.index; this.renderSelPill(); for (const el of this.list.children) el.classList.toggle("sel", el === li); }
          this.focusOn(x, y, "m:" + r.index);
        });
        rows.push(li);
      }
      if (!det && this.detailsOn()) rows.push(h("div", "empty-note", "Asking the server for this map's monsters…"));
    }
    if (!rows.length) rows.push(h("div", "empty-note", q ? `Nothing here matches. Find searches every map for an NPC.` : "Nothing here."));
    this.list.replaceChildren(...rows);
  }

  // — Actions —

  /** BigMapDialog.TeleportToNPC: the cost popup, then a gold check (Inventory.gold); the server moves you. */
  private teleport(npc: ClientNPCInfo): void {
    const m = this.ui.ctx.models, cost = m.gameInfo.teleportToNPCCost, mapIndex = this.view;
    const [name] = npcName(npc.Name);
    void this.ui.rt.popups.ask(`Teleport to ${name} for ${fmt(cost)} gold?`, { title: "Teleport", kind: "question", gold: cost }).then((yes) => {
      if (!yes || this.signal.aborted) return;
      if (m.currentMap.index !== mapIndex) return; // you left that map meanwhile
      if (m.inventory.gold < cost) { void this.ui.rt.popups.confirm({ title: "Teleport", text: "Not enough gold.", buttons: "ok", kind: "warning" }); return; }
      this.ui.ctx.send(ClientPackets.TeleportToNPC({ ObjectID: npc.ObjectID }));
    });
  }

  /** BigMapDialog.Search: C.SearchMap (3+ characters, 1 s apart); S.SearchMapResult answers through host.packets. */
  private serverSearch(): void {
    const text = this.search.value;
    if (text.length < SEARCH_MIN) return;
    const now = this.ui.ctx.now();
    if (now < this.nextSearch) return;
    this.nextSearch = now + SEARCH_MS;
    this.ui.ctx.send(ClientPackets.SearchMap({ Text: text }));
  }

  // — Waypoints (local, per character) —

  private waypointKey(): string { return this.ui.ctx.player.user?.name ?? ""; }

  private waypoints(): [number, number][] {
    const all = loadWaypoints()[this.waypointKey()];
    const list = all?.[String(this.view)];
    return Array.isArray(list) ? list.filter((p): p is [number, number] => Array.isArray(p) && Number.isFinite(p[0]) && Number.isFinite(p[1])) : [];
  }

  private saveWaypoints(list: [number, number][]): void {
    const all = loadWaypoints(), who = this.waypointKey();
    const mine = all[who] ?? (all[who] = {});
    if (list.length) mine[String(this.view)] = list; else delete mine[String(this.view)];
    if (!Object.keys(mine).length) delete all[who];
    try { localStorage.setItem(WAYPOINTS, JSON.stringify(all)); } catch { /* storage blocked: not kept */ }
  }

  private addWaypoint(x: number, y: number): void {
    const list = this.waypoints();
    if (list.length >= MAX_WAYPOINTS) list.shift();
    list.push([x, y]);
    this.saveWaypoints(list);
    if (!this.layers.has("waypoints")) { this.layers.add("waypoints"); this.layerChips.set(this.layers); this.savePrefs(); }
    this.drawOverlay();
    this.renderSide();
  }

  private removeWaypoint(i: number): void {
    const list = this.waypoints();
    list.splice(i, 1);
    this.saveWaypoints(list);
    this.drawOverlay();
    this.renderSide();
  }

  private savePrefs(): void {
    try { localStorage.setItem(PREFS, JSON.stringify({ layers: [...this.layers], tab: this.tab } satisfies Prefs)); } catch { /* not kept */ }
  }
}

/** A monster's display name (ClientMonsterInfo.GameName, else Name; "Monster N" until its info arrives). */
function mobName(index: number, info: ClientMonsterInfo | undefined): string {
  return info ? pretty(info.GameName || info.Name) : `Monster ${index}`;
}

function loadPrefs(): Prefs {
  const all: Layer[] = ["npcs", "exits", "group", "monsters", "safe", "waypoints"];
  const def: Prefs = { layers: ["npcs", "exits", "group", "waypoints"], tab: "npcs" }; // spawns / safe zones opt-in (busy maps)
  try {
    const v = JSON.parse(localStorage.getItem(PREFS) ?? "null") as { layers?: unknown; tab?: unknown } | null;
    if (!v) return def;
    const layers = Array.isArray(v.layers) ? v.layers.filter((l): l is Layer => all.includes(l as Layer)) : def.layers;
    const tab = v.tab === "npcs" || v.tab === "exits" || v.tab === "monsters" ? v.tab : def.tab;
    return { layers, tab };
  } catch { return def; }
}

/** localStorage 'crystal.ui.waypoints': character name -> MapIndex -> [x, y][]. */
function loadWaypoints(): Record<string, Record<string, [number, number][]>> {
  try {
    const v = JSON.parse(localStorage.getItem(WAYPOINTS) ?? "{}") as unknown;
    return v && typeof v === "object" && !Array.isArray(v) ? v as Record<string, Record<string, [number, number][]>> : {};
  } catch { return {}; }
}

registerWindow("bigMap", (host) => new BigMapWindow(host));
