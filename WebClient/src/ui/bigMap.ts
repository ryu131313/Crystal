// Big map (Client/MirScenes/Dialogs/BigMapDialog.cs): BigMapDialog + BigMapViewPort + BigMapNPCRow + WorldMapImage.
//   Viewport: the viewed map's MMap image at ClientMapInfo.BigMap (the current map falls back to MapInformation.BigMap until
//             its record arrives), scaled per cell. On the user's own map: objects in view (minimap colours), group members on
//             this map (from S.SendMemberLocation) and the user; a click walks there (PathFinder + MapControl.AutoPath).
//             Every viewed map: its movement icons (MapLinkIcon; hover = title + location, click = view the destination) and the
//             selected NPC's icon. Other maps come from C.RequestMapInfo -> S.NewMapInfo (GameInfo).
//   NPC list:  BigMapNPCRow per ClientMapInfo.NPCs; click selects / deselects; Teleport (C.TeleportToNPC) on the user's map
//             for NPCs with CanTeleportTo, after a confirm showing GameScene.TeleportToNPCCost and a gold check.
//   Search:    C.SearchMap (1 s apart) -> S.SearchMapResult views that map and selects that NPC.
//   World map: S.WorldMapSetup (Enabled shows the World button; icons on Prguse2 1360 + clouds 1365 + border 1366).
// Toggle with B (KeybindOptions.Bigmap), Esc closes. Only opens when the user's map has a big map (BigMapDialog.Show).

import { ObjectType } from "../protocol/enums.generated.ts";
import type { ServerPacket, ClientMapInfo, ClientMovementInfo, ClientNPCInfo, WorldMapIcon } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { MapCells } from "../map/mapReader.ts";
import { libAsync } from "../game/libraries.ts";
import type { MapObj } from "../game/objects.ts";
import type { GameInfo } from "../game/gameInfo.ts";
import { libIcon } from "./buffBar.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

const MAX_W = 568, MAX_H = 380;           // BigMapViewPort.Size
const MAX_WORLD_W = 760, MAX_WORLD_H = 520; // WorldMapImage is drawn at its own size; larger images are scaled down to fit
const MAP_LINK_ICON = "MapLinkIcon";        // Libraries.MapLinkIcon
const MINI_MAP = "MMap";                    // Libraries.MiniMap
const PRGUSE2 = "Prguse2";

interface Bitmap { bmp: ImageBitmap; x: number; y: number; w: number; h: number }
const bitmaps = new Map<string, Promise<Bitmap | null>>();
/** A library image as an ImageBitmap with its offsets (cached). */
function libBitmap(lib: string, index: number): Promise<Bitmap | null> {
  const key = `${lib}#${index}`;
  let p = bitmaps.get(key);
  if (!p) {
    p = (async () => {
      if (index < 0) return null;
      const l = await libAsync(lib);
      if (!l || index >= l.count) return null;
      const img = await l.image(index);
      if (!img || !img.width || !img.height) return null;
      const bmp = await createImageBitmap(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height));
      return { bmp, x: img.x, y: img.y, w: img.width, h: img.height };
    })().catch(() => null);
    bitmaps.set(key, p);
  }
  return p;
}

/** Synchronous view of libBitmap for the draw loop: undefined while loading, null when absent. */
const ready = new Map<string, Bitmap | null>();
function bitmapNow(lib: string, index: number): Bitmap | null | undefined {
  const key = `${lib}#${index}`;
  if (ready.has(key)) return ready.get(key);
  void libBitmap(lib, index).then((b) => ready.set(key, b));
  return undefined;
}

/** BigMapNPCRow ctor: "A_B_Name" -> "(A)(B)Name" (empty parts skipped). */
function npcRowName(name: string): string {
  if (!name.includes("_")) return name;
  const parts = name.split("_");
  let out = "";
  for (let s = 0; s < parts.length; s++) {
    if (parts[s] === "") continue;
    out += s === parts.length - 1 ? parts[s] : `(${parts[s]})`;
  }
  return out;
}

interface Hit<T> { x: number; y: number; w: number; h: number; item: T }
const inside = <T>(h: Hit<T>, x: number, y: number) => x >= h.x && y >= h.y && x < h.x + h.w && y < h.y + h.h;

type Group = { has(n: string): boolean; locations: ReadonlyMap<string, WirePoint>; memberMaps: ReadonlyMap<string, string> };

export class BigMap {
  private readonly root = el("div", { class: "window bigmap-window", hidden: true });
  private readonly title = el("span", {});
  private readonly coords = el("span", { class: "magic-meta" });
  private readonly canvas = el("canvas", { width: MAX_W, height: MAX_H });
  private readonly g: CanvasRenderingContext2D;
  private readonly npcList = el("div", { class: "bigmap-npcs" });
  private readonly npcPanel = el("div", { class: "bigmap-npc-panel" });
  private readonly teleportButton = el("button", { disabled: true, title: "Teleport to the selected NPC" }, "Teleport");
  private readonly searchInput = el("input", { class: "bigmap-search", placeholder: "Search for NPCs", maxLength: 150 }); // Globals.MaxChatLength
  private readonly searchRow = el("div", { class: "bigmap-search-row" });
  private readonly worldButton = el("button", { hidden: true, title: "World map" }, "World");

  private readonly info: GameInfo;
  private readonly send: (f: Uint8Array) => void;
  private readonly gold: () => number;

  /** BigMapDialog.TargetMapIndex: the map being viewed. */
  private targetMapIndex = -1;
  /** BigMapDialog.CurrentRecord (null until the viewed map's S.NewMapInfo arrives). */
  private record: ClientMapInfo | null = null;
  private recordIndex = -1;
  private selectedNPC: ClientNPCInfo | null = null;
  private npcRows = new Map<ClientNPCInfo, HTMLElement>();
  private worldOpen = false;  // WorldMap.Visible
  private nextSearchTime = 0; // BigMapDialog.NextSearchTime

  private imageIndex = -1;
  private image: Bitmap | null = null;
  private scale = 1;  // world map scale
  private scaleX = 1; // BigMapViewPort.ScaleX / ScaleY (canvas px per cell)
  private scaleY = 1;
  private nextDraw = 0;
  private movementHits: Hit<ClientMovementInfo>[] = [];
  private npcHit: Hit<ClientNPCInfo> | null = null;
  private worldHits: Hit<WorldMapIcon>[] = [];
  private hoverIcon: WorldMapIcon | null = null;

  /** Set by the game: a click on the map asks to walk to a cell. */
  onWalk: (cell: WirePoint) => void = () => {};
  /** MapInformation.BigMap of the user's map (set by the game). */
  bigMapIndex = 0;
  /** MapControl.Index of the user's map (from S.MapInformation / S.MapChanged in handle()). */
  mapIndex = -1;

  constructor(stage: HTMLElement, info: GameInfo, send: (f: Uint8Array) => void, gold: () => number) {
    this.info = info;
    this.send = send;
    this.gold = gold;
    this.g = this.canvas.getContext("2d")!;
    const myLocation = el("button", { title: "Show my location", onclick: () => this.setTargetMap(this.mapIndex) }, "My location"); // MyLocationButton
    this.worldButton.onclick = () => this.openWorldMap();
    this.teleportButton.onclick = () => this.teleportToNPC();
    this.searchInput.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") { e.preventDefault(); this.search(); } });
    this.searchRow.append(this.searchInput, el("button", { title: "Search for NPCs", onclick: () => this.search() }, "Search"));
    this.npcPanel.append(this.npcList, this.teleportButton);
    this.root.append(
      el("div", { class: "window-title" }, this.title, this.coords, el("button", { class: "window-close", onclick: () => this.toggle(false), title: "Close (B)" }, "×")),
      el("div", { class: "bigmap-body" }, this.canvas, this.npcPanel),
      el("div", { class: "bigmap-footer" }, this.searchRow, el("span", { class: "bigmap-spacer" }), this.worldButton, myLocation),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    this.canvas.addEventListener("mousemove", (e) => this.mouseMove(e));
    this.canvas.addEventListener("mouseleave", () => { this.coords.textContent = ""; this.canvas.title = ""; this.hoverIcon = null; this.nextDraw = 0; });
    this.canvas.addEventListener("mousedown", (e) => { if (e.button === 0) this.mouseDown(e); });
    stage.append(this.root);
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      if (document.activeElement instanceof HTMLInputElement) return;
      if (matches(e, KeybindOptions.Bigmap)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) this.toggle(false); // GameScene Esc hides BigMapDialog
    }, { signal });
    const off = info.onChange((kind, index) => {
      if (kind === "worldMap") this.worldButton.hidden = !info.worldMap.Enabled;
      if (kind !== "map" || index !== this.targetMapIndex) return;
      this.nextDraw = 0;
      // GameScene.InvalidateMapInfo: a dropped record that is being viewed is asked for again
      if (!info.map(index) && !this.root.hidden) info.requestMap(index, false);
    });
    signal.addEventListener("abort", off);
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** BigMapDialog.Show (nothing to show on maps without a big map; opens on the user's map) / Hide. */
  toggle(show = this.root.hidden): void {
    if (show && this.bigMapIndex <= 0) return;
    this.root.hidden = !show;
    this.nextDraw = 0;
    if (show) this.setTargetMap(this.mapIndex); // TargetMyLocation
  }

  /** S.MapInformation / S.MapChanged (MapControl.Index) and S.SearchMapResult. */
  handle(p: ServerPacket): void {
    switch (p.type) {
      case "MapInformation": case "MapChanged": this.mapIndex = p.MapIndex; this.nextDraw = 0; break;
      case "SearchMapResult": // GameScene.SearchMapResult
        if (p.MapIndex === -1 && p.NPCIndex === 0) {
          alert("Nothing Found.");
          this.searchInput.focus();
          break;
        }
        this.setTargetMap(p.MapIndex);
        this.setTargetNPC(p.NPCIndex);
        break;
    }
  }

  /** BigMapDialog.SetTargetMap. */
  setTargetMap(mapIndex: number): void {
    this.targetMapIndex = mapIndex;
    this.setRecord(null);
    this.worldOpen = false;
    this.npcPanel.hidden = false;
    this.searchRow.hidden = false;
    this.selectNPC(null);
    if (!this.info.map(mapIndex)) this.info.requestMap(mapIndex, false);
    else this.setRecord(this.info.map(mapIndex)!);
    this.nextDraw = 0;
  }

  /** BigMapDialog.SetTargetNPC: select the viewed map's NPC with that ObjectID. */
  private setTargetNPC(objectID: number): void {
    if (!this.record) return;
    const npc = this.record.NPCs.find((n) => n.ObjectID === objectID);
    if (npc) this.selectNPC(npc);
  }

  /** BigMapDialog.CurrentRecord setter: title + the NPC rows (BigMapNPCRow, GameScene.CreateBigMapButtons). */
  private setRecord(record: ClientMapInfo | null): void {
    if (record === this.record) return;
    this.record = record;
    this.recordIndex = record ? this.targetMapIndex : -1;
    this.npcRows.clear();
    const rows = (record?.NPCs ?? []).map((npc) => {
      const img = el("img", { alt: "" });
      if (npc.Icon > 0) void libIcon(MAP_LINK_ICON, npc.Icon).then((url) => { if (url) img.src = url; });
      const row = el("div", { class: "bigmap-npc-row", title: npc.Name, onclick: () => this.selectNPC(this.selectedNPC === npc ? null : npc) }, img, el("span", {}, npcRowName(npc.Name)));
      this.npcRows.set(npc, row);
      return row;
    });
    this.npcList.replaceChildren(...rows);
  }

  /** BigMapDialog.SelectedNPC setter. */
  private selectNPC(npc: ClientNPCInfo | null): void {
    if (this.selectedNPC) this.npcRows.get(this.selectedNPC)?.classList.remove("selected");
    this.selectedNPC = npc;
    if (npc) this.npcRows.get(npc)?.classList.add("selected");
    this.teleportButton.disabled = !npc || this.targetMapIndex !== this.mapIndex || !npc.CanTeleportTo;
    this.nextDraw = 0;
  }

  /** BigMapDialog.OpenWorldMap. */
  private openWorldMap(): void {
    this.worldOpen = true;
    this.npcPanel.hidden = true;
    this.searchRow.hidden = true;
    this.title.textContent = "World map";
    this.nextDraw = 0;
  }

  /** BigMapDialog.TeleportToNPC. */
  private teleportToNPC(): void {
    const npc = this.selectedNPC;
    if (!npc || !npc.CanTeleportTo) return;
    const cost = this.info.teleportToNPCCost;
    if (!confirm(`Teleport to this NPC for ${cost} Gold?`)) return;
    if (this.gold() < cost) { alert("Not enough Gold."); return; }
    this.send(ClientPackets.TeleportToNPC({ ObjectID: npc.ObjectID }));
  }

  /** BigMapDialog.Search (the server ignores fewer than 3 characters: PlayerObject.SearchMap). */
  private search(): void {
    const text = this.searchInput.value;
    if (text.trim() === "" || text.length < 3) return;
    const now = performance.now();
    if (now < this.nextSearchTime) return;
    this.nextSearchTime = now + 1000;
    this.send(ClientPackets.SearchMap({ Text: text }));
  }

  // — Input —

  private canvasPoint(e: MouseEvent): { x: number; y: number } {
    const r = this.canvas.getBoundingClientRect();
    return { x: ((e.clientX - r.left) * this.canvas.width) / r.width, y: ((e.clientY - r.top) * this.canvas.height) / r.height };
  }

  private cellAt(x: number, y: number): WirePoint | null {
    if (!this.image) return null;
    return { X: Math.trunc(x / this.scaleX), Y: Math.trunc(y / this.scaleY) };
  }

  private worldIconAt(x: number, y: number): WorldMapIcon | null {
    const ix = x / this.scale, iy = y / this.scale;
    for (let i = this.worldHits.length - 1; i >= 0; i--) if (inside(this.worldHits[i]!, ix, iy)) return this.worldHits[i]!.item;
    return null;
  }

  private movementAt(x: number, y: number): ClientMovementInfo | null {
    for (let i = this.movementHits.length - 1; i >= 0; i--) if (inside(this.movementHits[i]!, x, y)) return this.movementHits[i]!.item;
    return null;
  }

  private mouseMove(e: MouseEvent): void {
    const { x, y } = this.canvasPoint(e);
    if (this.worldOpen) {
      const icon = this.worldIconAt(x, y);
      if (icon !== this.hoverIcon) { this.hoverIcon = icon; this.nextDraw = 0; }
      this.canvas.style.cursor = icon ? "pointer" : "default";
      return;
    }
    // Movement button / selected NPC icon MouseEnter: BigMapDialog.MouseLocation = their location
    const mv = this.movementAt(x, y);
    const npc = !mv && this.npcHit && inside(this.npcHit, x, y) ? this.npcHit.item : null;
    const at = mv?.Location ?? npc?.Location ?? this.cellAt(x, y);
    this.coords.textContent = at ? `[ ${at.X}, ${at.Y} ]` : "";
    this.canvas.title = mv?.Title ?? npc?.Name ?? "";
    this.canvas.style.cursor = mv ? "pointer" : "";
  }

  private mouseDown(e: MouseEvent): void {
    const { x, y } = this.canvasPoint(e);
    if (this.worldOpen) {
      const icon = this.worldIconAt(x, y);
      if (icon) this.setTargetMap(icon.MapIndex); // WorldMapImage button click
      return;
    }
    const mv = this.movementAt(x, y);
    if (mv) { this.setTargetMap(mv.Destination); return; }
    // BigMapViewPort.OnMouseClick: walk there (only on the user's own map)
    if (this.targetMapIndex !== this.mapIndex) return;
    const c = this.cellAt(x, y);
    if (c) this.onWalk(c);
  }

  // — Drawing —

  /** Loads the viewed map's MMap image and sizes the canvas to it (the desktop viewport is the image's own size, at most
   *  568 x 380, centred on the user; here larger images are scaled down to fit instead). */
  private load(index: number): void {
    this.imageIndex = index;
    this.image = null;
    if (index <= 0) return;
    void libBitmap(MINI_MAP, index).then((b) => {
      if (!b || this.imageIndex !== index) return;
      this.image = b;
      const s = Math.min(1, MAX_W / b.w, MAX_H / b.h);
      this.canvas.width = Math.round(b.w * s);
      this.canvas.height = Math.round(b.h * s);
      this.nextDraw = 0;
    });
  }

  draw(map: MapCells, title: string, user: MapObj, objects: Iterable<MapObj>, group: Group): void {
    if (this.root.hidden) return;
    const now = performance.now();
    if (now < this.nextDraw) return;
    this.nextDraw = now + 100;
    if (this.worldOpen) { this.drawWorld(); return; }

    // BigMapViewPort.OnBeforeDraw: pick up the record once it has arrived
    const record = this.info.map(this.targetMapIndex) ?? null;
    if (record !== this.record || (record && this.recordIndex !== this.targetMapIndex)) this.setRecord(record);
    const own = this.targetMapIndex === this.mapIndex;
    const bigMap = record ? record.BigMap : own ? this.bigMapIndex : 0;
    const width = record ? record.Width : own ? map.width : 0;
    const height = record ? record.Height : own ? map.height : 0;
    this.title.textContent = record?.Title ?? (own ? title : "");
    if (bigMap !== this.imageIndex) this.load(bigMap);

    const g = this.g;
    g.fillStyle = "#000";
    g.fillRect(0, 0, this.canvas.width, this.canvas.height);
    this.movementHits = [];
    this.npcHit = null;
    const img = this.image;
    if (!img || width <= 0 || height <= 0) {
      g.fillStyle = "#9aa0ae";
      g.font = "13px sans-serif";
      g.textAlign = "center";
      g.fillText(!record && !own ? "Loading map…" : bigMap <= 0 ? "No map" : "", this.canvas.width / 2, this.canvas.height / 2);
      g.textAlign = "start";
      return;
    }
    g.drawImage(img.bmp, 0, 0, this.canvas.width, this.canvas.height);
    this.scaleX = this.canvas.width / width;
    this.scaleY = this.canvas.height / height;
    const dot = (x: number, y: number, colour: string, size = 2) => { g.fillStyle = colour; g.fillRect(Math.round(x * this.scaleX - size / 2), Math.round(y * this.scaleY - size / 2), size, size); };

    if (own) {
      const pet = `(${user.name})`;
      for (const ob of objects) {
        if (ob === user || ob.race === ObjectType.Item || ob.dead || ob.race === ObjectType.Spell || ob.sneaking) continue;
        const colour = group.has(ob.name) || ob.name.endsWith(pet) ? "#0000ff" : ob.race === ObjectType.Player ? "#ffffff" : ob.race === ObjectType.Merchant || ob.ai === 6 ? "#00ff32" : "#ff0000";
        dot(ob.location.X, ob.location.Y, colour);
      }
      // Group members on this map (the desktop matches GroupMembersMap against the map title)
      for (const [name, loc] of group.locations) {
        if (name === user.name || group.memberMaps.get(name) !== title) continue;
        dot(loc.X, loc.Y, "#3c78ff", 5);
      }
      dot(user.location.X, user.location.Y, "#ffff00", 5); // UserRadarDot (Prguse2 1350)
    }

    // Movement buttons (MapLinkIcon, centred on the location) and the selected NPC's icon
    const icon = (index: number, loc: WirePoint): Hit<null> | null => {
      const b = bitmapNow(MAP_LINK_ICON, index);
      if (!b) { if (b === undefined) this.nextDraw = 0; return null; }
      const x = Math.trunc(loc.X * this.scaleX) - Math.trunc(b.w / 2), y = Math.trunc(loc.Y * this.scaleY) - Math.trunc(b.h / 2);
      g.drawImage(b.bmp, x, y);
      return { x, y, w: b.w, h: b.h, item: null };
    };
    for (const mv of record?.Movements ?? []) {
      const h = icon(mv.Icon, mv.Location);
      if (h) this.movementHits.push({ ...h, item: mv });
    }
    if (this.selectedNPC) {
      const h = icon(this.selectedNPC.Icon, this.selectedNPC.Location);
      if (h) this.npcHit = { ...h, item: this.selectedNPC };
    }
  }

  /** WorldMapImage: background, blended clouds, border, then the icon under the mouse (OnlyDrawWhenActive) + its title. */
  private drawWorld(): void {
    const bg = bitmapNow(PRGUSE2, 1360);
    if (bg === undefined) { this.nextDraw = 0; return; }
    const g = this.g;
    const w = bg?.w ?? MAX_W, h = bg?.h ?? MAX_H;
    this.scale = Math.min(1, MAX_WORLD_W / w, MAX_WORLD_H / h);
    const cw = Math.round(w * this.scale), ch = Math.round(h * this.scale);
    if (this.canvas.width !== cw || this.canvas.height !== ch) { this.canvas.width = cw; this.canvas.height = ch; this.imageIndex = -1; }
    g.setTransform(this.scale, 0, 0, this.scale, 0, 0);
    g.fillStyle = "#000";
    g.fillRect(0, 0, w, h);
    if (bg) g.drawImage(bg.bmp, 0, 0);
    const clouds = bitmapNow(PRGUSE2, 1365), border = bitmapNow(PRGUSE2, 1366);
    if (clouds) { g.globalCompositeOperation = "lighter"; g.drawImage(clouds.bmp, 0, 0); g.globalCompositeOperation = "source-over"; }
    if (border) g.drawImage(border.bmp, 0, 0);
    if (clouds === undefined || border === undefined) this.nextDraw = 0;
    this.worldHits = [];
    for (const wi of this.info.worldMap.Icons) {
      const b = bitmapNow(MAP_LINK_ICON, wi.ImageIndex);
      if (b === undefined) { this.nextDraw = 0; continue; }
      if (!b) continue;
      this.worldHits.push({ x: b.x, y: b.y, w: b.w, h: b.h, item: wi }); // UseOffSet: drawn at the image's own offset
      if (wi === this.hoverIcon) g.drawImage(b.bmp, b.x, b.y);
    }
    if (this.hoverIcon) {
      g.font = "12px sans-serif";
      const tw = g.measureText(this.hoverIcon.Title).width;
      const x = Math.trunc(w / 2 - tw / 2);
      g.fillStyle = "#000";
      g.fillRect(x - 2, 8, tw + 4, 17);
      g.fillStyle = "#fff";
      g.fillText(this.hoverIcon.Title, x, 21);
    }
    g.setTransform(1, 0, 0, 1, 0, 0);
  }
}

