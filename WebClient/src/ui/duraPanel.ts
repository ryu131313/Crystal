// Equipment durability: port of DuraStatusDialog + CharacterDuraPanel (Client/MirScenes/Dialogs/MainDialogs.cs).
// DuraStatusDialog is a 40 x 19 strip under the minimap (MiniMapDialog.Location.X + 86, MiniMapDialog.Size.Height) with
// the Character button (Prguse 2113, 2110 while the panel shows); it toggles CharacterDuraPanel and Settings.DuraView.
// CharacterDuraPanel (Prguse 2105 at (ScreenWidth - 61, 200)) shows a body outline (2161 at 40 % + 2162) with one piece
// per equipped item, green / yellow / red by durability (above half, at most half, at most a fifth; amulets by count
// against StackSize). GetCharacterDura runs every frame while the panel shows.

import type { UserItem } from "../protocol/server.ts";
import { ItemType, EquipmentSlot } from "../protocol/enums.generated.ts";
import { gameSettings, setSetting } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { MirWindow, MirButton, MirImage, Lib } from "./mir/controls.ts";

type Piece = "helmet" | "belt" | "armour" | "boots" | "weapon" | "necklace" | "braceletL" | "braceletR" | "ringL" | "ringR" | "torch" | "stone" | "amulet" | "mount";

const PIECES: [Piece, number, number][] = [
  ["helmet", 24, 3], ["belt", 23, 23], ["armour", 16, 11], ["boots", 17, 43], ["weapon", 4, 5], ["necklace", 3, 67], ["braceletL", 3, 43],
  ["braceletR", 43, 43], ["ringL", 3, 54], ["ringR", 43, 54], ["torch", 44, 5], ["stone", 30, 54], ["amulet", 16, 54], ["mount", 43, 68],
];

export interface DuraWorld {
  equipment: () => readonly (UserItem | null | undefined)[];
  info: (item: UserItem) => { Type: number; StackSize: number } | undefined;
}

export class DuraPanel {
  private readonly status: MirWindow;
  private readonly button: MirButton;
  private readonly panel: MirWindow;
  private readonly pieces = new Map<Piece, MirImage>();
  private readonly w: DuraWorld;

  constructor(screenW: number, miniMapX: number, miniMapHeight: number, w: DuraWorld) {
    this.w = w;
    this.status = new MirWindow({ location: [miniMapX + 86, miniMapHeight], size: [40, 19], sort: false, visible: true });
    this.status.el.classList.add("mir-hud");
    this.button = new MirButton({ parent: this.status, library: Lib.Prguse, index: 2113, hoverIndex: 2111, pressedIndex: 2112, size: [20, 19], location: [20, 0],
      sound: SoundList.ButtonA, hint: "Dura Panel", onClick: () => this.toggle(!this.panel.visible) });
    this.panel = new MirWindow({ library: Lib.Prguse, index: 2105, location: [screenW - 61, 200], sort: false });
    this.panel.el.classList.add("mir-hud");
    new MirImage({ parent: this.panel, library: Lib.Prguse, index: 2161, location: [3, 3], opacity: 0.4 });
    const background = new MirImage({ parent: this.panel, library: Lib.Prguse, index: 2162, location: [3, 3] });
    for (const [p, x, y] of PIECES) this.pieces.set(p, new MirImage({ parent: background, library: Lib.Prguse, index: -1, location: [x, y] }));
    this.toggle(gameSettings.DuraView);
    gameSignal().addEventListener("abort", () => { this.status.dispose(); this.panel.dispose(); });
  }

  /** CharacterDuraPanel.Show / Hide + Settings.DuraView. */
  private toggle(show: boolean): void {
    if (show) this.panel.show(); else this.panel.hide();
    this.button.index = show ? 2110 : 2113;
    setSetting("DuraView", show);
  }

  /** MiniMapDialog SetSmallMode / SetBigMode move the strip. */
  place(miniMapX: number, miniMapHeight: number): void {
    const [x, y] = this.status.location;
    if (x !== miniMapX + 86 || y !== miniMapHeight) this.status.location = [miniMapX + 86, miniMapHeight];
  }

  /** CharacterDuraPanel.GetCharacterDura + UpdateCharacterDura. */
  update(): void {
    if (!this.panel.visible) return;
    const idx: Partial<Record<Piece, number>> = {};
    const eq = this.w.equipment();
    const level = (cur: number, max: number, green: number) => {
      const warning = Math.trunc(max / 2), danger = Math.trunc(max / 5);
      let i = -1;
      if (cur > warning) i = green;
      if (cur <= warning) i = green + 1;
      if (cur <= danger) i = green + 2;
      if (cur === 0) i = -1;
      return i;
    };
    for (let slot = 0; slot < eq.length; slot++) {
      const item = eq[slot];
      if (!item) continue;
      const info = this.w.info(item);
      if (!info) continue;
      const d = (green: number) => level(item.CurrentDura, item.MaxDura, green);
      switch (info.Type) {
        case ItemType.Amulet: idx.amulet = level(item.Count, info.StackSize, 2134); break;
        case ItemType.Armour: idx.armour = d(2149); break;
        case ItemType.Belt: idx.belt = d(2158); break;
        case ItemType.Boots: idx.boots = d(2152); break;
        case ItemType.Bracelet:
          if (slot === EquipmentSlot.BraceletR) idx.braceletR = d(2143);
          else if (slot === EquipmentSlot.BraceletL) idx.braceletL = d(2143);
          break;
        case ItemType.Helmet: idx.helmet = d(2155); break;
        case ItemType.Necklace: idx.necklace = d(2122); break;
        case ItemType.Ring:
          if (slot === EquipmentSlot.RingR) idx.ringR = d(2131);
          else if (slot === EquipmentSlot.RingL) idx.ringL = d(2131);
          break;
        case ItemType.Stone: if (item.CurrentDura === 0) idx.stone = 2137; break;
        case ItemType.Mount: idx.mount = d(2140); break;
        case ItemType.Torch: idx.torch = d(2146); break;
        case ItemType.Weapon: idx.weapon = d(2125); break;
      }
    }
    for (const [p, img] of this.pieces) img.index = idx[p] ?? -1;
  }
}
