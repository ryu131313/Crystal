// Minimap: port of MiniMapDialog.DrawMiniMap (Client/MirScenes/Dialogs/MainDialogs.cs). The map's MMap image
// (MapInformation.MiniMap) is cropped to 120 x 108 around the user (clamped to the image), scaled image-px per cell, with
// a 2 x 2 dot per object: white players, green NPCs and guards (AI 6), red monsters. Items, the dead, spells and sneaking
// objects are skipped. Blue for group members (not the user) and for objects named "...(<user>)" (pets). NPCs with an
// available quest get a "!" / "?" marker in the QuestIcon colour (opts.questIcon). Shown top right with the map name, the
// user's location and the light-setting icon (GameScene.TimeOfDay: Prguse 2092-2095, opts.timeOfDay); the button hides it
// (MiniMap Toggle). Not ported: mail / big-map buttons (the HUD has Mail and B).

import { LightSetting, ObjectType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { libAsync } from "../game/libraries.ts";
import type { MapObj } from "../game/objects.ts";
import { questMiniMapMarker } from "../game/quests.ts";
import { libIcon } from "./buffBar.ts";
import { el } from "./screens.ts";

const VIEW_W = 120, VIEW_H = 108;

export interface MiniMapOptions {
  /** NPCObject.QuestIcon of an NPC (Quests.npcQuestIcon): null / None when it has no available quest. */
  questIcon?: (npcId: number) => number | null;
  /** GameScene.Lights from S.TimeOfDay (LightSetting). */
  timeOfDay?: () => number;
}

/** GameScene.TimeOfDay: MiniMapDialog.LightSetting image per LightSetting. */
function lightIconIndex(light: number): number {
  switch (light) {
    case LightSetting.Dawn: return 2095;
    case LightSetting.Evening: return 2094;
    case LightSetting.Night: return 2092;
  }
  return 2093; // Day, Normal
}

export class MiniMap {
  private readonly root = el("div", { class: "minimap" });
  private readonly title = el("div", { class: "minimap-title" });
  private readonly canvas = el("canvas", { width: VIEW_W, height: VIEW_H });
  private readonly location = el("div", { class: "minimap-loc" });
  private readonly g: CanvasRenderingContext2D;
  private image: ImageBitmap | null = null;
  private imageIndex = -1;
  private nextDraw = 0;
  private open = true;
  private readonly opts: MiniMapOptions;
  private readonly lightIcon = el("img", { class: "minimap-light", alt: "" });
  private lightIndex = -1;

  constructor(stage: HTMLElement, opts: MiniMapOptions = {}) {
    this.opts = opts;
    this.g = this.canvas.getContext("2d")!;
    const toggle = el("button", { class: "minimap-toggle", title: "Minimap", onclick: () => this.toggle() }, "▾");
    this.root.append(el("div", { class: "minimap-head" }, this.title, toggle), this.canvas, el("div", { class: "minimap-foot" }, this.location, this.lightIcon));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
  }

  toggle(): void {
    this.open = !this.open;
    this.canvas.hidden = !this.open;
  }

  /** The MMap image for a minimap index (decoded once per map). */
  private load(index: number): void {
    this.imageIndex = index;
    this.image = null;
    if (index <= 0) return;
    void (async () => {
      const lib = await libAsync("MMap");
      const img = lib && (await lib.image(index));
      if (!img || !img.width || !img.height || this.imageIndex !== index) return;
      const bmp = await createImageBitmap(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height));
      if (this.imageIndex === index) this.image = bmp;
    })();
  }

  /** Called every frame; redraws at 10 Hz. */
  draw(map: MapCells, miniMap: number, title: string, user: MapObj, objects: Iterable<MapObj>, inGroup: (name: string) => boolean = () => false): void {
    if (miniMap !== this.imageIndex) this.load(miniMap);
    const now = performance.now();
    if (now < this.nextDraw) return;
    this.nextDraw = now + 100;
    this.title.textContent = title;
    this.location.textContent = `${user.location.X}:${user.location.Y}`;
    const light = lightIconIndex(this.opts.timeOfDay?.() ?? LightSetting.Normal);
    if (light !== this.lightIndex) {
      this.lightIndex = light;
      void libIcon("Prguse", light).then((url) => { if (url && this.lightIndex === light) this.lightIcon.src = url; });
    }
    this.root.classList.toggle("small", miniMap <= 0); // SetSmallMode when the map has no minimap
    const g = this.g;
    g.fillStyle = "#000";
    g.fillRect(0, 0, VIEW_W, VIEW_H);
    const img = this.image;
    if (!img || !this.open || map.width <= 0 || map.height <= 0) return;

    const scaleX = img.width / map.width, scaleY = img.height / map.height;
    let vx = Math.trunc(scaleX * user.location.X) - VIEW_W / 2;
    let vy = Math.trunc(scaleY * user.location.Y) - VIEW_H / 2;
    if (vx + VIEW_W >= img.width) vx = img.width - VIEW_W;
    if (vy + VIEW_H >= img.height) vy = img.height - VIEW_H;
    if (vx < 0) vx = 0;
    if (vy < 0) vy = 0;
    g.drawImage(img, vx, vy, VIEW_W, VIEW_H, 0, 0, VIEW_W, VIEW_H);

    const startX = Math.trunc(vx / scaleX), startY = Math.trunc(vy / scaleY);
    const petSuffix = `(${user.name})`;
    const markers: { x: number; y: number; text: string; colour: string }[] = [];
    for (const ob of objects) {
      if (ob.race === ObjectType.Item || ob.dead || ob.race === ObjectType.Spell || ob.sneaking) continue;
      const x = (ob.location.X - startX) * scaleX, y = (ob.location.Y - startY) * scaleY;
      g.fillStyle = (inGroup(ob.name) && ob !== user) || ob.name.endsWith(petSuffix) ? "#0000ff"
        : ob.race === ObjectType.Player ? "#ffffff" : ob.race === ObjectType.Merchant || ob.ai === 6 ? "#00ff32" : "#ff0000";
      g.fillRect(Math.round(x - 0.5), Math.round(y - 0.5), 2, 2);
      // NPC Quest Icons: a bold label at (x - 6, y - 10), drawn over every dot (the labels are child controls)
      if (ob.race === ObjectType.Merchant && this.opts.questIcon) {
        const m = questMiniMapMarker(this.opts.questIcon(ob.id));
        if (m) markers.push({ x, y, ...m });
      }
    }
    if (markers.length) {
      g.font = "bold 12px Arial, sans-serif";
      g.textAlign = "center";
      g.textBaseline = "top";
      g.lineWidth = 2;
      g.strokeStyle = "#000";
      for (const m of markers) {
        g.fillStyle = m.colour;
        g.strokeText(m.text, m.x - 2, m.y - 10);
        g.fillText(m.text, m.x - 2, m.y - 10);
      }
    }
  }
}
