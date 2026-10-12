// Mount window (MountDialog): the equipped mount (EquipmentSlot.Mount) with its name, loyalty (CurrentDura / MaxDura)
// and the mount-type animation (Prguse StartIndex + MountType * 20, 16 frames at 100 ms), and its slot items
// (MountSlot Reins / Bells / Saddle / Ribbon / Mask; 4-slot mounts have no Mask). The Ride button and M ride / dismount
// (MountDialog.CanRide / Ride = chat "@ride"); J or the HUD Mount button toggles the window (KeybindOptions.MountWindow),
// refused with the NoMount message when the user has no mount type. Slot cells are drawn by InventoryWindow.slotItemCell
// (shared pick / lock state): right-click a reins / bells / saddle / ribbon / mask in the bag to fit it
// (MirItemCell.UseSlotItem), right-click a slot to take it off (MirItemCell.RemoveItem). Model: src/game/itemChanges.ts.
// Not ported: the dialog artwork (Prguse 160 / 167) and the Help button (HelpDialog is not ported).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { EquipmentSlot, MirAction, MountSlotName } from "../protocol/enums.generated.ts";
import type { ItemChanges } from "../game/itemChanges.ts";
import type { Inventory } from "../game/inventory.ts";
import type { LibImage } from "../assets/crystalLib.ts";
import { libAsync } from "../game/libraries.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

/** What the window needs from the user's MapObj (GameScene.User). */
export interface MountUserState {
  id: number;
  /** PlayerObject.MountType (-1 = none) / RidingMount. */
  mountType: number;
  ridingMount: boolean;
  /** PlayerObject.CurrentAction (MirAction). */
  action: number;
  /** PlayerObject.Fishing (the MirItemCell.UseItem gate). */
  fishing: boolean;
}

const frameCache = new Map<number, Promise<LibImage | null>>();
function prguseFrame(index: number): Promise<LibImage | null> {
  let p = frameCache.get(index);
  if (!p) {
    p = libAsync("Prguse").then((l) => (l ? l.image(index) : null)).catch(() => null);
    frameCache.set(index, p);
  }
  return p;
}

export class MountWindow {
  /** The HUD Mount button (MainDialogs MountButton); game.ts places it. */
  readonly hudButton: HTMLButtonElement;
  /** PlayerObject.MountTime (performance.now() of the user's last S.MountUpdate). */
  private mountTime = -Infinity;
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (frame: Uint8Array) => void;
  private readonly user: () => MountUserState | null;
  private readonly requestItem: (index: number) => void;
  private readonly now: () => number;
  private readonly root = el("div", { class: "window mount-window", hidden: true });
  private readonly nameLabel = el("div", { class: "mount-name" });
  private readonly loyaltyLabel = el("div", { class: "mount-loyalty" });
  private readonly canvas = el("canvas", { class: "mount-image", width: 330, height: 250 }) as HTMLCanvasElement;
  private readonly grid = el("div", { class: "inv-grid mount-grid" });
  /** MountImage: animation start index (-1 = no animation) and the frame shown. */
  private animStart = -1;
  private animFrame = 0;
  private animTimer = 0;
  private drawn = -1;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, changes: ItemChanges, send: (frame: Uint8Array) => void,
    user: () => MountUserState | null, requestItem: (index: number) => void, now: () => number) {
    this.stage = stage;
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.user = user;
    this.requestItem = requestItem;
    this.now = now;
    changes.userState = () => { const u = user(); return { ridingMount: !!u?.ridingMount, fishing: !!u?.fishing }; };
    this.hudButton = el("button", { title: "Mount (J)", onclick: () => this.toggle() }, "Mount") as HTMLButtonElement;
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Mount"), el("button", { class: "window-close", title: "Close (J)", onclick: () => this.toggle(false) }, "×")),
      this.nameLabel, this.loyaltyLabel, this.canvas,
      el("div", { class: "row mount-buttons" }, el("button", { class: "primary", title: "Ride / dismount (M)", onclick: () => { if (this.canRide()) this.ride(); } }, "Ride")),
      this.grid,
      el("div", { class: "socket-hint" }, "Right-click a mount item in the bag to fit it; right-click a slot to take it off."));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    signal.addEventListener("abort", () => this.stopAnimation());
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.MountWindow)) this.toggle();
      else if (matches(e, KeybindOptions.Mount) && this.canRide()) this.ride();
      else if (matches(e, KeybindOptions.Closeall)) this.toggle(false);
    }, { signal });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** MountDialog.Show (refused with NoMount when User.MountType < 0) / Hide. */
  toggle(show = this.root.hidden): void {
    if (show && this.root.hidden && (this.user()?.mountType ?? -1) < 0) {
      messageBox(this.stage, "You do not own a mount."); // ClientTextKeys.NoMount
      return;
    }
    this.root.hidden = !show;
    this.render();
  }

  /** MountDialog.CanRide: a mount type, 500 ms after the last MountUpdate, standing (mounted or not). */
  canRide(): boolean {
    const u = this.user();
    if (!u || u.mountType < 0 || this.mountTime + 500 > this.now()) return false;
    return u.action === MirAction.Standing || u.action === MirAction.MountStanding;
  }

  /** MountDialog.Ride: the server toggles riding on the "@ride" command. */
  ride(): void {
    this.send(ClientPackets.Chat({ Message: "@ride" }));
  }

  /** GameScene.MountUpdate (the user's part) + PlayerObject.MountUpdate (MountTime; MountType < 0 hides the dialog). */
  handle(p: ServerPacket): boolean {
    if (p.type !== "MountUpdate") return false;
    const u = this.user();
    if (!u || p.ObjectID !== u.id) return false;
    this.mountTime = this.now();
    if (p.MountType < 0) this.root.hidden = true;
    this.render(p.MountType);
    return true;
  }

  /** MountDialog.RefreshDialog: SwitchType (4 or 5 slot layout) + DrawMountAnimation. */
  render(mountType = this.user()?.mountType ?? -1): void {
    if (this.root.hidden) { this.stopAnimation(); return; }
    const mount = this.inv.equipment[EquipmentSlot.Mount] ?? null;
    const slots = mount?.Slots ?? null;
    const five = !slots || slots.length !== 4; // SwitchType: case 4 = Index 160 / StartIndex 1170, case 5 = 167 / 1330
    const startIndex = five ? 1330 : 1170;
    this.root.classList.toggle("four-slots", !five);
    const width = five ? 330 : 270;
    if (this.canvas.width !== width) { this.canvas.width = width; this.drawn = -1; } // a resize clears the canvas
    // DrawMountAnimation: name + loyalty only while a mount type is set
    const info = mount ? this.inv.info(mount) : undefined;
    if (mount && !info) this.requestItem(mount.ItemIndex);
    if (mountType >= 0 && mount && info) {
      this.nameLabel.textContent = itemFriendlyName(info) + (mount.Count > 1 ? ` (${mount.Count})` : "");
      this.loyaltyLabel.textContent = `${mount.CurrentDura} / ${mount.MaxDura} Loyalty`; // ClientTextKeys.LoyaltyProgress
    } else if (mountType < 0) {
      this.nameLabel.textContent = "";
      this.loyaltyLabel.textContent = "";
    }
    const start = mountType < 0 ? -1 : startIndex + mountType * 20;
    if (start !== this.animStart) { this.animStart = start; this.animFrame = 0; }
    if (start < 0) { this.stopAnimation(); this.clearCanvas(); this.drawn = -1; } else this.startAnimation();
    // Grid: MountSlot cells (Mask hidden for a 4-slot mount)
    const cells: HTMLElement[] = [];
    const n = slots ? Math.min(slots.length, five ? 5 : 4) : 0;
    for (let i = 0; i < n; i++) {
      const s = slots![i];
      if (s && !this.inv.info(s)) this.requestItem(s.ItemIndex); // GameScene.Bind
      cells.push(this.invWindow.slotItemCell("mount", i, MountSlotName[i] ?? ""));
    }
    this.grid.replaceChildren(...cells);
  }

  // — MountImage (MirAnimatedControl: AnimationCount 16, AnimationDelay 100, Loop, UseOffSet) —

  private startAnimation(): void {
    if (!this.animTimer) this.animTimer = window.setInterval(() => { this.animFrame = (this.animFrame + 1) % 16; this.draw(); }, 100);
    this.draw();
  }

  private stopAnimation(): void {
    if (this.animTimer) { window.clearInterval(this.animTimer); this.animTimer = 0; }
  }

  private clearCanvas(): void {
    this.canvas.getContext("2d")?.clearRect(0, 0, this.canvas.width, this.canvas.height);
  }

  /** Draws the current frame at MountImage.Location + the frame offset; the canvas covers dialog y 70..320. */
  private draw(): void {
    if (this.animStart < 0) return;
    const index = this.animStart + this.animFrame;
    if (index === this.drawn) return;
    const four = this.root.classList.contains("four-slots");
    const loc = four ? { x: 110, y: 250 } : { x: 0, y: 70 };
    void prguseFrame(index).then((img) => {
      if (this.animStart < 0 || this.animStart + this.animFrame !== index) return;
      this.drawn = index;
      this.clearCanvas();
      if (!img || !img.width || !img.height) return;
      this.canvas.getContext("2d")?.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), loc.x + img.x, loc.y + img.y - 70);
    });
  }
}
