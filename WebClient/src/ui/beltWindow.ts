// BeltDialog (Client/MirScenes/Dialogs/InventoryDialog.cs): Prguse 1932 (horizontal) / 1944 (vertical, Flip), the
// half-transparent Index + 1 backdrop (BeltPanel_BeforeDraw), key labels 1-6, six 32 x 32 cells on inventory slots
// 0-5, Rotate and Close buttons. Visible from the start; Z (KeybindOptions.Belt) toggles it, KeybindOptions.BeltFlip
// rotates it. Built by InventoryWindow (the cells share its pick). The belt keys themselves are handled by game.ts.

import { MirWindow, MirButton, MirLabel, Lib, setPicture, libSize, SCREEN_HEIGHT, SCREEN_WIDTH } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, keyLabel, matches } from "../game/keybinds.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";

export class BeltWindow {
  private readonly win: MirWindow;
  private readonly backdrop: HTMLDivElement;
  private readonly keys: MirLabel[] = [];
  private readonly cells: HTMLElement[] = [];
  private readonly rotateButton: MirButton;
  private readonly closeButton: MirButton;
  private mainX = 0;

  constructor(inv: InventoryWindow) {
    // BeltDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 1932, movable: true, sort: true, visible: true, location: [230, SCREEN_HEIGHT - 150] });
    // BeltPanel_BeforeDraw: Prguse[Index + 1] at 50 % under the dialog picture
    this.backdrop = document.createElement("div");
    this.backdrop.className = "mir-pic";
    this.backdrop.style.opacity = "0.5";
    this.win.el.insertBefore(this.backdrop, this.win.el.firstChild);
    for (let i = 0; i < 6; i++) this.keys.push(new MirLabel({ parent: this.win, size: [26, 14], location: [8 + i * 35, 2], text: String(i + 1) }));
    this.rotateButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1926, hoverIndex: 1927, pressedIndex: 1928, location: [222, 3], sound: SoundList.ButtonA,
      hint: "Rotate", onClick: () => this.flip() });
    this.closeButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1923, hoverIndex: 1924, pressedIndex: 1925, location: [222, 19], sound: SoundList.ButtonA,
      hint: `Close (${keyLabel(KeybindOptions.Belt)})`, onClick: () => this.win.hide() });
    for (let x = 0; x < 6; x++) this.cells.push(placeCell(inv.boundCell("inv", x, [32, 32]), this.win, [x * 35 + 12, 3]));
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

  private drawBackdrop(): void {
    void setPicture(this.backdrop, Lib.Prguse, this.win.index + 1);
  }

  /** BeltDialog.Flip. */
  flip(): void {
    const vertical = this.win.index === 1932;
    if (vertical) {
      this.win.index = 1944;
      this.win.location = [0, 200];
      for (let x = 0; x < 6; x++) placeCell(this.cells[x]!, this.win, [3, x * 35 + 12]);
      this.closeButton.setIndices(1935, 1936, 1937);
      this.closeButton.location = [3, 222];
      this.rotateButton.setIndices(1938, 1939, 1940);
      this.rotateButton.location = [19, 222];
    } else {
      this.win.index = 1932;
      this.win.location = [this.mainX + 230, SCREEN_HEIGHT - 150];
      for (let x = 0; x < 6; x++) placeCell(this.cells[x]!, this.win, [x * 35 + 12, 3]);
      this.closeButton.setIndices(1923, 1924, 1925);
      this.closeButton.location = [222, 19];
      this.rotateButton.setIndices(1926, 1927, 1928);
      this.rotateButton.location = [222, 3];
    }
    for (let i = 0; i < this.keys.length; i++) this.keys[i]!.location = this.win.index !== 1932 ? [-1, 11 + i * 35] : [8 + i * 35, 2];
    this.drawBackdrop();
  }

  /** Cells are bound cells (InventoryWindow.render refreshes them). */
  render(): void {}
}
