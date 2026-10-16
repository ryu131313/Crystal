// BeltDialog (Client/MirScenes/Dialogs/InventoryDialog.cs): Prguse 1932 (horizontal) / 1944 (vertical, Flip), the
// half-transparent Index + 1 backdrop (BeltPanel_BeforeDraw), Rotate and Close buttons. Visible from the start; Z
// (KeybindOptions.Belt) toggles it, KeybindOptions.BeltFlip rotates it. Built by InventoryWindow (the cells share its
// pick). The belt keys themselves are handled by game.ts.
// Web change (phase 2.9, the belt is 8 cells = UserObject.BeltIdx 8): the desktop art has six 35 px cells, so the strip
// is drawn stretched by two cells (BELT_EXTRA px): the picture up to cell 4, a copy of cells 2-3, then the rest (cells
// 4-5 and the buttons) shifted by BELT_EXTRA. Key labels 1-8, eight 32 x 32 cells on inventory slots 0-7.

import { MirWindow, MirButton, MirLabel, Lib, libPicture, libSize, SCREEN_HEIGHT, SCREEN_WIDTH, type LibPicture } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, keyLabel, matches } from "../game/keybinds.ts";
import { BELT_IDX } from "../game/inventory.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";

/** Cell pitch of the belt art (cells at x * 35 + 12). */
const CELL_PITCH = 35;
/** Cells the desktop art draws (BeltDialog.Grid). */
const ART_CELLS = 6;
/** Extra strip length for the cells past the art's six. */
const BELT_EXTRA = (BELT_IDX - ART_CELLS) * CELL_PITCH;
/** Where the inserted copy goes: the start of cell 4 (4 * 35 + 12); the copy is taken from BELT_EXTRA px before it. */
const BELT_SPLIT = 4 * CELL_PITCH + 12;
/** The Rotate / Close buttons' offset along the strip (222 on the desktop) after stretching. */
const BUTTON_AT = 222 + BELT_EXTRA;

/** Draws `pic` into `host` stretched along one axis: [0, SPLIT) as is, a copy of [SPLIT - EXTRA, SPLIT) at SPLIT, then
 *  [SPLIT, end) at SPLIT + EXTRA. Returns the stretched size. */
function drawStretched(host: HTMLElement, pic: LibPicture | null, vertical: boolean): [number, number] {
  host.replaceChildren();
  if (!pic) return [0, 0];
  const len = vertical ? pic.height : pic.width, across = vertical ? pic.width : pic.height;
  const pieces: [number, number, number][] = [[0, Math.min(BELT_SPLIT, len), 0]]; // [source start, length, target start]
  if (len > BELT_SPLIT) {
    pieces.push([BELT_SPLIT - BELT_EXTRA, BELT_EXTRA, BELT_SPLIT]);
    pieces.push([BELT_SPLIT, len - BELT_SPLIT, BELT_SPLIT + BELT_EXTRA]);
  }
  for (const [from, size, to] of pieces) {
    const d = document.createElement("div");
    d.style.position = "absolute";
    d.style.backgroundImage = `url("${pic.url}")`;
    d.style.backgroundRepeat = "no-repeat";
    d.style.left = (vertical ? 0 : to) + "px";
    d.style.top = (vertical ? to : 0) + "px";
    d.style.width = (vertical ? across : size) + "px";
    d.style.height = (vertical ? size : across) + "px";
    d.style.backgroundPosition = vertical ? `0 ${-from}px` : `${-from}px 0`;
    host.appendChild(d);
  }
  const total = len > BELT_SPLIT ? len + BELT_EXTRA : len;
  return vertical ? [across, total] : [total, across];
}

function layer(): HTMLDivElement {
  const d = document.createElement("div");
  d.className = "mir-pic";
  d.style.pointerEvents = "none";
  return d;
}

export class BeltWindow {
  private readonly win: MirWindow;
  private readonly backdrop: HTMLDivElement;
  private readonly picture: HTMLDivElement;
  private drawn = 0;
  private readonly keys: MirLabel[] = [];
  private readonly cells: HTMLElement[] = [];
  private readonly rotateButton: MirButton;
  private readonly closeButton: MirButton;
  private mainX = 0;

  constructor(inv: InventoryWindow) {
    // BeltDialog ctor (the window's own picture is drawn stretched by drawBackdrop, not by MirImage)
    this.win = new MirWindow({ library: Lib.Prguse, index: 1932, drawImage: false, size: [0, 0], movable: true, sort: true, visible: true, location: [230, SCREEN_HEIGHT - 150] });
    // BeltPanel_BeforeDraw: Prguse[Index + 1] at 50 % under the dialog picture
    this.backdrop = layer();
    this.backdrop.style.opacity = "0.5";
    this.picture = layer();
    this.win.el.insertBefore(this.picture, this.win.el.firstChild);
    this.win.el.insertBefore(this.backdrop, this.picture);
    for (let i = 0; i < BELT_IDX; i++) this.keys.push(new MirLabel({ parent: this.win, size: [26, 14], location: [8 + i * 35, 2], text: String(i + 1) }));
    this.rotateButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1926, hoverIndex: 1927, pressedIndex: 1928, location: [BUTTON_AT, 3], sound: SoundList.ButtonA,
      hint: "Rotate", onClick: () => this.flip() });
    this.closeButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1923, hoverIndex: 1924, pressedIndex: 1925, location: [BUTTON_AT, 19], sound: SoundList.ButtonA,
      hint: `Close (${keyLabel(KeybindOptions.Belt)})`, onClick: () => this.win.hide() });
    for (let x = 0; x < BELT_IDX; x++) this.cells.push(placeCell(inv.boundCell("inv", x, [32, 32]), this.win, [x * 35 + 12, 3]));
    this.drawBackdrop();
    // Location = (MainDialog.Location.X + 230, ScreenHeight - 150); MainDialog is centred at the bottom (Prguse 1 at 1024)
    void libSize(Lib.Prguse, 1).then(([w]) => {
      this.mainX = Math.trunc(SCREEN_WIDTH / 2 - w / 2);
      if (this.win.index === 1932) this.win.location = [this.mainX + 230, SCREEN_HEIGHT - 150];
    });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Belt)) this.win.toggle();
      else if (matches(e, KeybindOptions.BeltFlip)) { e.preventDefault(); this.flip(); }
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  get visible(): boolean { return this.win.visible; }
  toggle(show = !this.win.visible): void { if (show) this.win.show(); else this.win.hide(); }

  /** The dialog picture (Index) and the BeforeDraw backdrop (Index + 1), both stretched to eight cells. */
  private drawBackdrop(): void {
    const index = this.win.index, vertical = index !== 1932, token = ++this.drawn;
    void Promise.all([libPicture(Lib.Prguse, index), libPicture(Lib.Prguse, index + 1)]).then(([pic, back]) => {
      if (token !== this.drawn) return; // flipped again while loading
      this.win.size = drawStretched(this.picture, pic, vertical);
      drawStretched(this.backdrop, back, vertical);
    });
  }

  /** BeltDialog.Flip. */
  flip(): void {
    const vertical = this.win.index === 1932;
    if (vertical) {
      this.win.index = 1944;
      this.win.location = [0, 200];
      for (let x = 0; x < BELT_IDX; x++) placeCell(this.cells[x]!, this.win, [3, x * 35 + 12]);
      this.closeButton.setIndices(1935, 1936, 1937);
      this.closeButton.location = [3, BUTTON_AT];
      this.rotateButton.setIndices(1938, 1939, 1940);
      this.rotateButton.location = [19, BUTTON_AT];
    } else {
      this.win.index = 1932;
      this.win.location = [this.mainX + 230, SCREEN_HEIGHT - 150];
      for (let x = 0; x < BELT_IDX; x++) placeCell(this.cells[x]!, this.win, [x * 35 + 12, 3]);
      this.closeButton.setIndices(1923, 1924, 1925);
      this.closeButton.location = [BUTTON_AT, 19];
      this.rotateButton.setIndices(1926, 1927, 1928);
      this.rotateButton.location = [BUTTON_AT, 3];
    }
    for (let i = 0; i < this.keys.length; i++) this.keys[i]!.location = this.win.index !== 1932 ? [-1, 11 + i * CELL_PITCH] : [8 + i * CELL_PITCH, 2];
    this.drawBackdrop();
  }

  /** Cells are bound cells (InventoryWindow.render refreshes them). */
  render(): void {}
}
