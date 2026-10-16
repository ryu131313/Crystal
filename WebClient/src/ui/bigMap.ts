// Big map (Client/MirScenes/Dialogs/BigMapDialog.cs): BigMapDialog (Title 820) + BigMapViewPort + BigMapNPCRow +
// WorldMapImage + GameScene.CreateBigMapButtons / SearchMapResult, laid out like the desktop dialog.
//   Viewport:  the viewed map's MMap image (ClientMapInfo.BigMap; the current map falls back to MapInformation.BigMap until
//              its record arrives) drawn on a canvas at the desktop viewport rectangle (the image's size, at most 568 x 380,
//              centred in the 568 x 380 area at (14, 52), stretched like MiniMap.Draw(index, DisplayLocation, Size)). On the
//              user's own map: objects in view (2 x 2 radar dots, minimap colours), group members on this map (Prguse2 1350,
//              hint = name, from S.SendMemberLocation) and the user (UserRadarDot); a click walks there (onWalk).
//              Every viewed map: its movement buttons (MapLinkIcon; hint = title, hover = location, click = view the
//              destination) and the selected NPC's icon. Other maps come from C.RequestMapInfo -> S.NewMapInfo (GameInfo).
//   NPC list:  BigMapNPCRow per ClientMapInfo.NPCs, 18 rows at (590, 50 + i * 21) with the Prguse2 scroll buttons / bar and
//              the mouse wheel; click selects / deselects (Brown name); Teleport (C.TeleportToNPC) on the user's map for NPCs
//              with CanTeleportTo, after a MirMessageBox showing GameScene.TeleportToNPCCost and a gold check.
//   Search:    the Search button enables the text box; C.SearchMap (1 s apart) -> S.SearchMapResult views that map and
//              selects that NPC.
//   World map: S.WorldMapSetup (Enabled shows the World button): Prguse2 1360 + additive clouds 1365 + border 1366, the
//              icons drawn only under the mouse, the hovered icon's title on black at the top.
// Toggle with B (KeybindOptions.Bigmap), Esc closes. Only opens when the user's map has a big map (BigMapDialog.Show).

import { ObjectType } from "../protocol/enums.generated.ts";
import type { ServerPacket, ClientMapInfo, ClientNPCInfo, WorldMapIcon } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { MapCells } from "../map/mapReader.ts";
import { libAsync } from "../game/libraries.ts";
import type { MapObj } from "../game/objects.ts";
import type { GameInfo } from "../game/gameInfo.ts";
import type { CurrentMap } from "../game/currentMap.ts";
import { MirWindow, MirImage, MirButton, MirControl, MirLabel, MirTextBox, Lib, libPicture } from "./mir/controls.ts";
import { messageBox } from "./messageBox.ts";
import { dragY } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

const VIEW_W = 568, VIEW_H = 380;  // BigMapViewPort.Size
const VIEW_X = 14, VIEW_Y = 52;    // OnBeforeDraw: viewRect = (14, 52) + the centring
const MAXIMUM_ROWS = 18;           // BigMapDialog.MaximumRows
const SCROLL_UP_Y = 48, SCROLL_DOWN_Y = 417;
const MAX_GROUP = 11;              // Globals.MaxGroup
const MAX_CHAT_LENGTH = 150;       // Globals.MaxChatLength

interface Bitmap { bmp: ImageBitmap; w: number; h: number }
const bitmaps = new Map<string, Promise<Bitmap | null>>();
/** A library image as an ImageBitmap (cached), for the canvas. */
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
      return { bmp, w: img.width, h: img.height };
    })().catch(() => null);
    bitmaps.set(key, p);
  }
  return p;
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

type Group = { has(n: string): boolean; locations: ReadonlyMap<string, WirePoint>; memberMaps: ReadonlyMap<string, string> };

/** BigMapNPCRow: a 140 x 25 MirButton (no picture) with the NPC's MapLinkIcon at (1, 1) and its name at (23, 2). */
interface NPCRow { info: ClientNPCInfo; button: MirButton; name: MirLabel }

export class BigMap {
  private readonly info: GameInfo;
  private readonly send: (f: Uint8Array) => void;
  private readonly gold: () => number;
  private readonly stage: HTMLElement;
  /** The user's map (MapControl.Index, MapInformation.BigMap). */
  private readonly currentMap: CurrentMap;

  // BigMapDialog controls
  private readonly win: MirWindow;
  private readonly scrollUpButton: MirButton;
  private readonly scrollDownButton: MirButton;
  private readonly scrollBar: MirButton;
  private readonly worldButton: MirButton;
  private readonly myLocationButton: MirButton;
  private readonly teleportButton: MirButton;
  private readonly searchButton: MirButton;
  private readonly searchTextBox: MirTextBox;
  private readonly coordinateLabel: MirLabel;
  private readonly titleLabel: MirLabel;
  private readonly closeButton: MirButton;
  // BigMapViewPort
  private readonly viewPort: MirControl;
  private readonly canvas: HTMLCanvasElement;
  private readonly g: CanvasRenderingContext2D;
  private readonly selectedNPCIcon: MirImage;
  private readonly userRadarDot: MirImage;
  private readonly players: MirImage[] = [];
  // WorldMapImage
  private readonly worldMap: MirImage;
  private readonly worldTitle: MirLabel;
  private worldButtons: MirButton[] = [];

  /** BigMapDialog.TargetMapIndex: the map being viewed. */
  private targetMapIndex = -1;
  /** BigMapDialog.CurrentRecord (null until the viewed map's S.NewMapInfo arrives). */
  private record: ClientMapInfo | null = null;
  private recordIndex = -1;
  /** BigMapRecord.MovementButtons / NPCButtons of the current record. */
  private movementButtons: { button: MirButton; location: WirePoint }[] = [];
  private npcRows: NPCRow[] = [];
  private selectedNPC: NPCRow | null = null;
  private scrollOffset = 0;
  private gapPerRow = 0;
  private nextSearchTime = 0; // BigMapDialog.NextSearchTime

  private imageIndex = -1;
  private image: Bitmap | null = null;
  private scaleX = 1; // BigMapViewPort.ScaleX / ScaleY (viewport px per cell)
  private scaleY = 1;
  private nextDraw = 0;

  /** Set by the game: a click on the map asks to walk to a cell. */
  onWalk: (cell: WirePoint) => void = () => {};
  /** MapInformation.BigMap of the user's map (CurrentMap). */
  get bigMapIndex(): number { return this.currentMap.bigMapIndex; }
  /** MapControl.Index of the user's map (CurrentMap; S.MapInformation / S.MapChanged). */
  get mapIndex(): number { return this.currentMap.index; }

  constructor(stage: HTMLElement, info: GameInfo, send: (f: Uint8Array) => void, gold: () => number, currentMap: CurrentMap) {
    this.stage = stage;
    this.currentMap = currentMap;
    this.info = info;
    this.send = send;
    this.gold = gold;

    // BigMapDialog ctor (controls in the desktop's creation order = draw order; Size-relative locations in layout())
    this.win = new MirWindow({ library: Lib.Title, index: 820, sort: true, location: "center" });
    this.scrollUpButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, sound: SoundList.ButtonA, onClick: () => this.scrollUp() });
    this.scrollDownButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, sound: SoundList.ButtonA, onClick: () => this.scrollDown() });
    this.scrollBar = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, sound: SoundList.ButtonA });
    // ScrollBar.OnMoving (the row clamp to the last page is added: the desktop's formula can run past it)
    dragY(this.scrollBar, (y) => {
      if (!this.record || this.gapPerRow <= 0) return;
      y = Math.min(398, Math.max(61, y));
      const row = (y - SCROLL_UP_Y + 13) / this.gapPerRow;
      this.setScrollOffset(Math.min(Math.trunc(row), Math.max(0, this.npcRows.length - MAXIMUM_ROWS)));
      this.setNPCButtonVisibility(true);
    });
    this.worldButton = new MirButton({ parent: this.win, library: Lib.Title, index: 827, hoverIndex: 828, pressedIndex: 829, sound: SoundList.ButtonA, onClick: () => this.openWorldMap() });
    this.worldButton.visible = info.worldMap.Enabled;
    this.myLocationButton = new MirButton({ parent: this.win, library: Lib.Title, index: 824, hoverIndex: 825, pressedIndex: 826, sound: SoundList.ButtonA, onClick: () => this.setTargetMap(this.mapIndex) });
    this.teleportButton = new MirButton({ parent: this.win, library: Lib.Title, index: 821, hoverIndex: 822, pressedIndex: 823, disabledIndex: 823, enabled: false, sound: SoundList.ButtonA, onClick: () => this.teleportToNPC() });
    this.searchButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 1340, hoverIndex: 1341, pressedIndex: 1342, sound: SoundList.ButtonA, hint: "Search for NPCs", onClick: () => this.search() });
    // SearchTextBox: Size(130, 10) on the desktop, where the WinForms single-line box grows to the font's height
    this.searchTextBox = new MirTextBox({ parent: this.win, font: "8pt Arial", size: [130, 14], maxLength: MAX_CHAT_LENGTH,
      onEnter: () => { if (this.searchButton.enabled) this.searchButton.el.click(); } }); // SearchTextBox_KeyPress

    this.viewPort = new MirControl({ parent: this.win, location: [VIEW_X, VIEW_Y], size: [VIEW_W, VIEW_H] });
    this.canvas = document.createElement("canvas");
    this.canvas.width = VIEW_W;
    this.canvas.height = VIEW_H;
    Object.assign(this.canvas.style, { position: "absolute", left: "0", top: "0", width: "100%", height: "100%", display: "block" });
    this.viewPort.el.append(this.canvas);
    this.g = this.canvas.getContext("2d")!;
    this.selectedNPCIcon = new MirImage({ parent: this.viewPort, library: Lib.MapLinkIcon, visible: false });
    this.selectedNPCIcon.el.addEventListener("pointerenter", () => { if (this.selectedNPC) this.setMouseLocation(this.selectedNPC.info.Location); });
    this.userRadarDot = new MirImage({ parent: this.viewPort, library: Lib.Prguse2, index: 1350, visible: false, notControl: true });
    for (let i = 0; i < MAX_GROUP; i++) this.players.push(new MirImage({ parent: this.viewPort, library: Lib.Prguse2, index: 1350, visible: false }));
    // BigMapViewPort MouseMove (UpdateBigMapCoordinates) / MouseDown (OnMouseClick): only on the viewport itself, not its icons
    const onViewPort = (e: Event) => e.target === this.canvas || e.target === this.viewPort.el;
    this.viewPort.el.addEventListener("mousemove", (e) => { if (onViewPort(e)) this.viewPortMouseMove(e); });
    this.viewPort.el.addEventListener("mousedown", (e) => { if (e.button === 0 && onViewPort(e)) this.viewPortMouseDown(e); });

    this.coordinateLabel = new MirLabel({ parent: this.win, autoSize: true, foreColour: "#ffffff", location: [519, 435] });
    this.titleLabel = new MirLabel({ parent: this.win, foreColour: "#ffffff", size: [699, 20], location: [19, 6], font: "bold 9pt Arial", align: "center" });

    // WorldMapImage
    this.worldMap = new MirImage({ parent: this.win, library: Lib.Prguse2, index: 1360, location: [10, 0], visible: false });
    const clouds = new MirImage({ parent: this.worldMap, library: Lib.Prguse2, index: 1365, notControl: true });
    clouds.el.style.mixBlendMode = "plus-lighter"; // Blending
    new MirImage({ parent: this.worldMap, library: Lib.Prguse2, index: 1366, notControl: true });
    this.worldTitle = new MirLabel({ parent: this.worldMap, autoSize: true, backColour: "#000000", foreColour: "#ffffff" });

    this.closeButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 360, hoverIndex: 361, pressedIndex: 362, sound: SoundList.ButtonA, onClick: () => this.toggle(false) });
    this.win.ready.then((pic) => { if (pic) this.layout(pic.width, pic.height); });
    this.searchTextBox.input.disabled = true;
    this.makeWorldButtons(info.worldMap.Icons);

    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      if (document.activeElement instanceof HTMLInputElement) return;
      if (matches(e, KeybindOptions.Bigmap)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false); // GameScene Esc hides BigMapDialog
    }, { signal });
    const off = info.onChange((kind, index) => {
      if (kind === "worldMap") { // BigMapDialog.WorldMapSetup
        this.worldButton.visible = info.worldMap.Enabled;
        this.makeWorldButtons(info.worldMap.Icons);
      }
      if (kind !== "map" || index !== this.targetMapIndex) return;
      this.nextDraw = 0;
      // GameScene.InvalidateMapInfo: a dropped record that is being viewed is asked for again
      if (!info.map(index) && this.win.visible) info.requestMap(index, false);
    });
    signal.addEventListener("abort", () => { off(); this.win.dispose(); });
  }

  /** The BigMapDialog ctor locations relative to its Size. */
  private layout(w: number, h: number): void {
    this.scrollUpButton.location = [w - 21, SCROLL_UP_Y];
    this.scrollDownButton.location = [w - 21, SCROLL_DOWN_Y];
    this.scrollBar.location = [w - 21, SCROLL_UP_Y + 13];
    this.worldButton.location = [250, h - 33];
    this.myLocationButton.location = [400, h - 33];
    this.teleportButton.location = [w - 122, 432];
    this.searchButton.location = [23, h - 36];
    this.searchTextBox.location = [59, h - 27];
    this.closeButton.location = [w - 25, 3];
    this.setScrollOffset(this.scrollOffset);
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** BigMapDialog.Toggle: Show (nothing to show on maps without a big map; opens on the user's map) / Hide. */
  toggle(show = !this.win.visible): void {
    if (!show) { this.win.hide(); return; }
    if (this.bigMapIndex <= 0) return;
    if (this.win.visible) { this.win.bringToFront(); return; }
    this.searchTextBox.input.disabled = true;
    this.win.show();
    this.nextDraw = 0;
    this.setTargetMap(this.mapIndex); // TargetMyLocation
  }

  /** S.MapInformation / S.MapChanged (a redraw; MapControl.Index is in CurrentMap) and S.SearchMapResult. */
  handle(p: ServerPacket): void {
    switch (p.type) {
      case "MapInformation": case "MapChanged": this.nextDraw = 0; break;
      case "SearchMapResult": // GameScene.SearchMapResult
        if (p.MapIndex === -1 && p.NPCIndex === 0) {
          messageBox(this.stage, "Nothing Found.", [{ label: "OK", onClick: () => this.searchTextBox.focus() }]);
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
    this.setScrollOffset(0);
    this.scrollBar.visible = false;
    this.worldMap.visible = false;
    this.searchTextBox.visible = true;
    this.selectNPC(null);
    if (!this.info.map(mapIndex)) this.info.requestMap(mapIndex, false);
    else this.setRecord(this.info.map(mapIndex)!);
    this.nextDraw = 0;
  }

  /** BigMapDialog.SetTargetNPC: select the viewed map's NPC with that ObjectID. */
  private setTargetNPC(objectID: number): void {
    const row = this.npcRows.find((r) => r.info.ObjectID === objectID);
    if (row) this.selectNPC(row);
  }

  /** BigMapDialog.CurrentRecord setter (+ GameScene.CreateBigMapButtons for the record's buttons). */
  private setRecord(record: ClientMapInfo | null): void {
    if (record === this.record && (!record || this.recordIndex === this.targetMapIndex)) return;
    this.setButtonsVisibility(false);
    for (const m of this.movementButtons) m.button.dispose();
    for (const r of this.npcRows) r.button.dispose();
    this.movementButtons = [];
    this.npcRows = [];
    this.selectNPC(null);
    this.record = record;
    this.recordIndex = record ? this.targetMapIndex : -1;
    this.titleLabel.text = record ? record.Title : "";
    if (record) {
      for (const mv of record.Movements) {
        const button = new MirButton({ parent: this.viewPort, library: Lib.MapLinkIcon, index: mv.Icon, pressedIndex: mv.Icon, sound: SoundList.ButtonA,
          location: [20, 38], hint: mv.Title, visible: false, onClick: () => this.setTargetMap(mv.Destination) });
        button.el.addEventListener("pointerenter", () => this.setMouseLocation(mv.Location));
        this.movementButtons.push({ button, location: mv.Location });
      }
      for (const npc of record.NPCs) {
        const button = new MirButton({ parent: this.win, size: [140, 25], visible: false, sound: SoundList.ButtonA });
        new MirImage({ parent: button, library: Lib.MapLinkIcon, index: npc.Icon, location: [1, 1], notControl: true });
        const name = new MirLabel({ parent: button, autoSize: true, foreColour: "#ffffff", location: [23, 2], notControl: true, text: npcRowName(npc.Name) });
        const row: NPCRow = { info: npc, button, name };
        button.onClick(() => this.selectNPC(this.selectedNPC === row ? null : row)); // BigMapNPCRow.Select
        button.el.addEventListener("wheel", (e) => { e.preventDefault(); if (e.deltaY < 0) this.scrollUp(); else if (e.deltaY > 0) this.scrollDown(); }, { passive: false }); // BigMap_MouseWheel
        this.npcRows.push(row);
      }
    }
    this.setButtonsVisibility(true);
  }

  private setButtonsVisibility(visible: boolean): void {
    if (!this.record) return;
    for (const m of this.movementButtons) m.button.visible = visible;
    this.setNPCButtonVisibility(visible);
  }

  /** BigMapDialog.SetNPCButtonVisibility. */
  private setNPCButtonVisibility(visible: boolean): void {
    for (const r of this.npcRows) r.button.visible = false;
    if (!visible) return;
    for (let i = this.scrollOffset; i < this.scrollOffset + MAXIMUM_ROWS && i < this.npcRows.length; i++) {
      const row = this.npcRows[i]!;
      row.button.location = [590, 50 + (i - this.scrollOffset) * 21];
      row.button.visible = true;
    }
    if (this.npcRows.length <= MAXIMUM_ROWS) return;
    this.scrollBar.visible = true;
    const scrollHeight = SCROLL_DOWN_Y - SCROLL_UP_Y - 32;
    this.gapPerRow = scrollHeight / (this.npcRows.length - MAXIMUM_ROWS);
  }

  /** BigMapDialog.ScrollOffset setter: the scroll bar follows. */
  private setScrollOffset(offset: number): void {
    this.scrollOffset = offset;
    this.scrollBar.location = [this.scrollUpButton.location[0], Math.trunc(SCROLL_UP_Y + 13 + offset * this.gapPerRow)];
  }

  private scrollUp(): void {
    if (this.scrollOffset === 0) return;
    this.setScrollOffset(this.scrollOffset - 1);
    this.setNPCButtonVisibility(true);
  }

  private scrollDown(): void {
    if (this.scrollOffset >= this.npcRows.length - MAXIMUM_ROWS) return;
    this.setScrollOffset(this.scrollOffset + 1);
    this.setNPCButtonVisibility(true);
  }

  /** BigMapDialog.SelectedNPC setter. */
  private selectNPC(row: NPCRow | null): void {
    if (row === this.selectedNPC) return;
    if (this.selectedNPC) this.selectedNPC.name.foreColour = "#ffffff";
    this.selectedNPC = row;
    if (row) {
      row.name.foreColour = "#a52a2a"; // Color.Brown
      this.selectedNPCIcon.index = row.info.Icon;
      this.selectedNPCIcon.visible = true;
      this.teleportButton.enabled = this.targetMapIndex === this.mapIndex && row.info.CanTeleportTo;
    } else {
      this.selectedNPCIcon.visible = false;
      this.teleportButton.enabled = false;
    }
    this.nextDraw = 0;
  }

  /** BigMapDialog.OpenWorldMap. */
  private openWorldMap(): void {
    this.worldMap.visible = true;
    this.searchTextBox.visible = false;
    this.setButtonsVisibility(false);
  }

  /** WorldMapImage.MakeButtons (rebuilt on each S.WorldMapSetup; the desktop adds to its list). */
  private makeWorldButtons(icons: readonly WorldMapIcon[]): void {
    for (const b of this.worldButtons) b.dispose();
    this.worldButtons = [];
    for (const icon of icons) {
      const button = new MirButton({ parent: this.worldMap, library: Lib.MapLinkIcon, index: icon.ImageIndex, sound: SoundList.ButtonA, onClick: () => this.setTargetMap(icon.MapIndex) });
      // UseOffSet (the control sits at the image's offset, so the mouse area matches) + OnlyDrawWhenActive
      void libPicture(Lib.MapLinkIcon, icon.ImageIndex).then((pic) => { if (pic) button.location = [pic.x, pic.y]; });
      button.picOpacity = 0;
      button.el.addEventListener("pointerenter", () => {
        button.picOpacity = 1;
        this.worldTitle.text = icon.Title;
        this.worldTitle.location = [Math.trunc(this.worldMap.size[0] / 2 - this.worldTitle.el.offsetWidth / 2), 10];
      });
      button.el.addEventListener("pointerleave", () => { button.picOpacity = 0; this.worldTitle.text = ""; });
      this.worldButtons.push(button);
    }
  }

  /** BigMapDialog.TeleportToNPC. */
  private teleportToNPC(): void {
    const npc = this.selectedNPC?.info;
    if (!npc || !npc.CanTeleportTo) return;
    const cost = this.info.teleportToNPCCost;
    messageBox(this.stage, `Teleport to this NPC for ${cost} Gold?`, [
      { label: "Yes", onClick: () => {
        if (this.gold() < cost) { messageBox(this.stage, "Not enough Gold.", [{ label: "OK" }]); return; }
        this.send(ClientPackets.TeleportToNPC({ ObjectID: npc.ObjectID }));
      } },
      { label: "No" },
    ]);
  }

  /** BigMapDialog.Search: the first click enables the text box. The server ignores fewer than 3 characters
   *  (PlayerObject.SearchMap); the desktop's length check is inverted and never sends a usable search. */
  private search(): void {
    if (this.searchTextBox.input.disabled) {
      this.searchTextBox.input.disabled = false;
      this.searchTextBox.focus();
    }
    const text = this.searchTextBox.text;
    if (text.trim() === "" || text.length < 3) return;
    const now = performance.now();
    if (now < this.nextSearchTime) return;
    this.nextSearchTime = now + 1000;
    this.send(ClientPackets.SearchMap({ Text: text }));
  }

  // — Input —

  /** BigMapDialog.MouseLocation setter (MakeCoordinateLabel). */
  private setMouseLocation(p: WirePoint): void {
    this.coordinateLabel.text = `[ ${p.X}, ${p.Y} ]`;
  }

  private cellAt(e: MouseEvent): WirePoint | null {
    if (!this.image) return null;
    const r = this.canvas.getBoundingClientRect();
    const x = ((e.clientX - r.left) * this.canvas.width) / r.width, y = ((e.clientY - r.top) * this.canvas.height) / r.height;
    return { X: Math.trunc(x / this.scaleX), Y: Math.trunc(y / this.scaleY) };
  }

  /** BigMapViewPort.UpdateBigMapCoordinates. */
  private viewPortMouseMove(e: MouseEvent): void {
    const c = this.cellAt(e);
    if (c) this.setMouseLocation(c);
  }

  /** BigMapViewPort.OnMouseClick: walk there (deliberately only on the user's own map; the desktop paths on the user's
   *  map whatever map is viewed). */
  private viewPortMouseDown(e: MouseEvent): void {
    if (this.worldMap.visible || this.targetMapIndex !== this.mapIndex) return;
    const c = this.cellAt(e);
    if (c) this.onWalk(c);
  }

  // — Drawing —

  /** Loads the viewed map's MMap image. */
  private load(index: number): void {
    this.imageIndex = index;
    this.image = null;
    if (index <= 0) return;
    void libBitmap(Lib.MiniMap, index).then((b) => {
      if (!b || this.imageIndex !== index) return;
      this.image = b;
      this.nextDraw = 0;
    });
  }

  /** BigMapViewPort.OnBeforeDraw (every frame from the game loop, redrawn at most every 100 ms). */
  draw(map: MapCells, title: string, user: MapObj, objects: Iterable<MapObj>, group: Group): void {
    if (!this.win.visible) return;
    const now = performance.now();
    if (now < this.nextDraw) return;
    this.nextDraw = now + 100;

    // pick up the record once it has arrived
    const record = this.info.map(this.targetMapIndex) ?? null;
    if (record !== this.record || (record && this.recordIndex !== this.targetMapIndex)) this.setRecord(record);
    const own = this.targetMapIndex === this.mapIndex;
    const bigMap = record ? record.BigMap : own ? this.bigMapIndex : 0;
    const width = record ? record.Width : own ? map.width : 0;
    const height = record ? record.Height : own ? map.height : 0;
    if (!record && own) this.titleLabel.text = title; // the user's map before its record arrives
    if (bigMap !== this.imageIndex) this.load(bigMap);

    const img = this.image;
    if (bigMap <= 0 || !img || width <= 0 || height <= 0) { this.viewPort.visible = false; return; }
    this.viewPort.visible = true;

    // viewRect: the image's size (at most 568 x 380) centred in the viewport area
    const vw = Math.min(VIEW_W, img.w), vh = Math.min(VIEW_H, img.h);
    this.viewPort.location = [VIEW_X + Math.trunc((VIEW_W - vw) / 2), VIEW_Y + Math.trunc((VIEW_H - vh) / 2)];
    this.viewPort.size = [vw, vh];
    if (this.canvas.width !== vw || this.canvas.height !== vh) { this.canvas.width = vw; this.canvas.height = vh; }
    this.scaleX = vw / width;
    this.scaleY = vh / height;

    const g = this.g;
    g.clearRect(0, 0, vw, vh);
    g.drawImage(img.bmp, 0, 0, vw, vh); // MiniMap.Draw(index, DisplayLocation, Size, White)

    this.userRadarDot.visible = own;
    if (own) {
      const pet = `(${user.name})`;
      for (const ob of objects) {
        if (ob === user || ob.race === ObjectType.Item || ob.dead || ob.race === ObjectType.Spell || ob.sneaking) continue;
        const colour = group.has(ob.name) || ob.name.endsWith(pet) ? "rgb(0,0,255)" : ob.race === ObjectType.Player ? "rgb(255,255,255)" : ob.race === ObjectType.Merchant || ob.ai === 6 ? "rgb(0,255,50)" : "rgb(255,0,0)";
        g.fillStyle = colour;
        g.fillRect(Math.round(ob.location.X * this.scaleX - 0.5), Math.round(ob.location.Y * this.scaleY - 0.5), 2, 2); // RadarTexture 2 x 2
      }
      const [dw, dh] = this.userRadarDot.size;
      this.userRadarDot.location = [Math.trunc(user.location.X * this.scaleX) - Math.trunc(dw / 2), Math.trunc(user.location.Y * this.scaleY) - Math.trunc(dh / 2)];
      // Players: group members on this map (the desktop matches GroupMembersMap against the map title)
      let i = 0;
      for (const [name, loc] of group.locations) {
        if (i >= this.players.length) break;
        if (name === user.name || group.memberMaps.get(name) !== title) continue;
        const p = this.players[i++]!;
        p.hint = name;
        p.location = [Math.trunc(loc.X * this.scaleX - 0.5) - 3, Math.trunc(loc.Y * this.scaleY - 0.5) - 3];
        p.visible = true;
      }
      for (; i < this.players.length; i++) this.players[i]!.visible = false;
    } else {
      for (const p of this.players) p.visible = false;
    }

    // movement buttons and the selected NPC's icon, centred on their locations
    for (const { button, location } of this.movementButtons) {
      const [bw, bh] = button.size;
      button.location = [Math.trunc(location.X * this.scaleX) - Math.trunc(bw / 2), Math.trunc(location.Y * this.scaleY) - Math.trunc(bh / 2)];
    }
    if (this.selectedNPC && this.selectedNPCIcon.visible) {
      const [sw, sh] = this.selectedNPCIcon.size;
      const loc = this.selectedNPC.info.Location;
      this.selectedNPCIcon.location = [Math.trunc(loc.X * this.scaleX) - Math.trunc(sw / 2), Math.trunc(loc.Y * this.scaleY) - Math.trunc(sh / 2)];
    }
  }
}
