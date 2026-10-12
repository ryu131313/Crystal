// Minimap: port of MiniMapDialog (Client/MirScenes/Dialogs/MainDialogs.cs) with the desktop artwork: Prguse 2090 (big)
// / 2091 (small) at (ScreenWidth - 126, 0), the map name, the user's location, the Mail button (Prguse 2099) with the
// blinking NewMail icon (544), the BigMap button (2096), the toggle button (2102) and the light-setting icon (GameScene.
// TimeOfDay: 2092-2095). DrawMiniMap: the map's MMap image (MapInformation.MiniMap) cropped to 120 x 108 around the
// user at (3, 22), a 2 x 2 dot per object (white players, green NPCs and guards (AI 6), red monsters, blue group
// members / "...(<user>)" pets; items, the dead, spells and sneaking objects skipped) and the bold "!" / "?" NPC quest
// labels. Maps without a minimap switch to the small mode. The map canvas redraws at 10 Hz.

import { LightSetting, ObjectType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { libAsync } from "../game/libraries.ts";
import type { MapObj } from "../game/objects.ts";
import { questMiniMapMarker } from "../game/quests.ts";
import { KeybindOptions } from "../game/keybinds.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirButton, MirImage, MirLabel, Lib } from "./mir/controls.ts";
import { pixelDetect } from "./hudCommon.ts";
import { keyHint } from "./mainDialog.ts";

const VIEW_W = 120, VIEW_H = 108;

export interface MiniMapOptions {
  /** NPCObject.QuestIcon of an NPC (Quests.npcQuestIcon): null / None when it has no available quest. */
  questIcon?: (npcId: number) => number | null;
  /** GameScene.Lights from S.TimeOfDay (LightSetting). */
  timeOfDay?: () => number;
  /** MailButton: MailListDialog.Toggle. */
  openMail?: () => void;
  /** BigMapButton: BigMapDialog.Toggle. */
  openBigMap?: () => void;
  /** GameScene.NewMail. */
  newMail?: () => boolean;
  screenWidth?: number;
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
  private readonly win: MirWindow;
  private readonly canvas = document.createElement("canvas");
  private readonly g: CanvasRenderingContext2D;
  private readonly mapName: MirLabel;
  private readonly locationLabel: MirLabel;
  private readonly mailButton: MirButton;
  private readonly newMailIcon: MirImage;
  private readonly bigMapButton: MirButton;
  private readonly lightSetting: MirImage;
  private readonly questLabels: MirLabel[] = [];
  private image: ImageBitmap | null = null;
  private imageIndex = -1;
  private nextDraw = 0;
  private fade = 1;
  private bigMode = true;
  private newMailCounter = 0;
  private readonly opts: MiniMapOptions;

  constructor(_stage: HTMLElement, opts: MiniMapOptions = {}) {
    this.opts = opts;
    this.win = new MirWindow({ library: Lib.Prguse, index: 2090, location: [(opts.screenWidth ?? 1024) - 126, 0], sort: false, visible: true });
    this.win.el.classList.add("mir-hud");
    pixelDetect(this.win);
    this.canvas.width = VIEW_W;
    this.canvas.height = VIEW_H;
    this.canvas.style.cssText = "position:absolute;left:3px;top:22px;pointer-events:none;image-rendering:pixelated";
    this.win.el.append(this.canvas);
    this.g = this.canvas.getContext("2d")!;
    this.mapName = new MirLabel({ parent: this.win, center: true, size: [120, 18], location: [2, 2], notControl: true });
    this.locationLabel = new MirLabel({ parent: this.win, center: true, size: [56, 18], location: [46, 131], notControl: true });
    this.mailButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 2099, hoverIndex: 2100, pressedIndex: 2101, location: [4, 131],
      sound: SoundList.ButtonA, hint: "Mail", onClick: () => opts.openMail?.() });
    this.newMailIcon = new MirImage({ parent: this.win, library: Lib.Prguse, index: 544, location: [5, 132], visible: false, notControl: true });
    this.bigMapButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 2096, hoverIndex: 2097, pressedIndex: 2098, location: [25, 131],
      sound: SoundList.ButtonA, hint: keyHint("BigMap ({0})", KeybindOptions.Bigmap), onClick: () => opts.openBigMap?.() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 2102, hoverIndex: 2103, pressedIndex: 2104, location: [109, 3],
      sound: SoundList.ButtonA, hint: keyHint("MiniMap ({0})", KeybindOptions.Minimap), onClick: () => this.toggle() });
    this.lightSetting = new MirImage({ parent: this.win, library: Lib.Prguse, index: 2093, location: [102, 131] });
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  /** MiniMapDialog.Size.Height (154 big, 45 small): the dura panel and the mode labels sit under it. */
  get height(): number { return this.win.index === 2091 ? 45 : 154; }
  get location(): readonly [number, number] { return this.win.location; }

  /** MiniMapDialog.Toggle. */
  toggle(): void {
    if (this.fade === 0) { this.bigMode = true; this.setMode(2090); this.fade = 1; }
    else { this.bigMode = false; this.setMode(2091); this.fade = 0; }
    this.nextDraw = 0;
  }

  /** SetSmallMode / SetBigMode: the bottom row follows the picture's height. */
  private setMode(index: number): void {
    this.win.index = index;
    const y = (index === 2091 ? 45 : 154) - 23;
    this.mailButton.location = [4, y];
    this.newMailIcon.location = [5, y + 1];
    this.bigMapButton.location = [25, y];
    this.locationLabel.location = [46, y];
    this.lightSetting.location = [102, y];
    this.canvas.hidden = index !== 2090;
    if (index !== 2090) for (const l of this.questLabels) l.visible = false;
  }

  /** The MMap image for a minimap index (decoded once per map). */
  private load(index: number): void {
    this.imageIndex = index;
    this.image = null;
    if (index <= 0) return;
    void (async () => {
      const lib = await libAsync(Lib.MiniMap);
      const img = lib && (await lib.image(index));
      if (!img || !img.width || !img.height || this.imageIndex !== index) return;
      const bmp = await createImageBitmap(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height));
      if (this.imageIndex === index) { this.image = bmp; this.nextDraw = 0; }
    })();
  }

  /** MiniMapDialog.Process + DrawMiniMap; called every frame (the map redraws at 10 Hz). */
  draw(map: MapCells, miniMap: number, title: string, user: MapObj, objects: Iterable<MapObj>, inGroup: (name: string) => boolean = () => false): void {
    const now = performance.now();
    // Process
    this.mapName.text = title;
    this.locationLabel.text = `${user.location.X}, ${user.location.Y}`; // Functions.PointToString
    this.lightSetting.index = lightIconIndex(this.opts.timeOfDay?.() ?? LightSetting.Normal);
    if (this.opts.newMail?.()) {
      const visible = Math.round(now / 100) % 10 < 5 || this.newMailCounter >= 10;
      if (!visible && this.newMailIcon.visible) this.newMailCounter++;
      this.newMailIcon.visible = visible;
    } else {
      this.newMailIcon.visible = false;
      this.newMailCounter = 0;
    }

    if (miniMap !== this.imageIndex) this.load(miniMap);
    // DrawMiniMap: small mode without a minimap, back to big when the next map has one
    if (miniMap === 0 && this.win.index !== 2091) this.setMode(2091);
    else if (miniMap > 0 && this.bigMode && this.win.index === 2091) this.setMode(2090);
    if (now < this.nextDraw) return;
    this.nextDraw = now + 100;
    const g = this.g;
    g.clearRect(0, 0, VIEW_W, VIEW_H);
    const img = this.image;
    let used = 0;
    if (img && miniMap > 0 && this.win.index === 2090 && map.width > 0 && map.height > 0) {
      const scaleX = img.width / map.width, scaleY = img.height / map.height;
      let vx = Math.trunc(scaleX * user.location.X) - VIEW_W / 2;
      let vy = Math.trunc(scaleY * user.location.Y) - VIEW_H / 2;
      if (vx + VIEW_W >= img.width) vx = img.width - VIEW_W;
      if (vy + VIEW_H >= img.height) vy = img.height - VIEW_H;
      if (vx < 0) vx = 0;
      if (vy < 0) vy = 0;
      g.globalAlpha = this.fade;
      g.drawImage(img, vx, vy, VIEW_W, VIEW_H, 0, 0, VIEW_W, VIEW_H);
      g.globalAlpha = 1;

      const startX = Math.trunc(vx / scaleX), startY = Math.trunc(vy / scaleY);
      const petSuffix = `(${user.name})`;
      for (const ob of objects) {
        if (ob.race === ObjectType.Item || ob.dead || ob.race === ObjectType.Spell || ob.sneaking) continue;
        const x = (ob.location.X - startX) * scaleX, y = (ob.location.Y - startY) * scaleY;
        g.fillStyle = (inGroup(ob.name) && ob !== user) || ob.name.endsWith(petSuffix) ? "#0000ff"
          : ob.race === ObjectType.Player ? "#ffffff" : ob.race === ObjectType.Merchant || ob.ai === 6 ? "#00ff32" : "#ff0000";
        g.fillRect(Math.round(x - 0.5), Math.round(y - 0.5), 2, 2);
        // NPC Quest Icons: a bold 9pt label per NPC with an available quest (reused between draws)
        if (ob.race === ObjectType.Merchant && this.opts.questIcon) {
          const m = questMiniMapMarker(this.opts.questIcon(ob.id));
          if (!m) continue;
          let label = this.questLabels[used];
          if (!label) {
            label = new MirLabel({ parent: this.win, autoSize: true, notControl: true, font: "bold 9pt Arial" });
            this.questLabels.push(label);
          }
          label.text = m.text;
          label.foreColour = m.colour;
          label.location = [Math.trunc(x) - 1, Math.trunc(y) + 12];
          label.visible = true;
          used++;
        }
      }
    }
    for (let i = this.questLabels.length - 1; i >= used; i--) this.questLabels.pop()!.dispose();
  }
}
