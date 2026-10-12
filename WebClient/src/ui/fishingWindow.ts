// Fishing windows. FishingWindow (FishingDialog): the equipped fishing rod (EquipmentSlot.Weapon with shape 49 / 50,
// Globals.FishingRodShapes) with its name, the rod picture (StateItem 1333 / 1335, +1 with a hook) and its slot items
// (FishingSlot Hook / Float / Bait / Finder / Reel); N or the HUD Fishing button toggles it (KeybindOptions.Fishing),
// refused with the NoFishingRod message without a rod. Slot cells are drawn by InventoryWindow.slotItemCell: right-click
// a fishing part in the bag to fit it (MirItemCell.UseSlotItem, bait merges onto the bait stack), right-click a slot to
// take it off (MirItemCell.RemoveItem). FishingStatusWindow (FishingStatusDialog): shown while S.FishingUpdate says the
// user is fishing; chance bar + label, progress bar, the Reel button (C.FishingCast CastOut = false, live only while a
// fish is found), the auto-cast toggle (needs a Reel in the rod; C.FishingChangeAutocast) and the "Esc exit" option.
// Casting out (GameScene.CanFish + C.FishingCast CastOut = true on a map click) lives in game.ts checkInput (riding.ts canFish).
// Not ported: the dialog artwork, the FishingWait splash effects (PlayerObject.ProcessFrames).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { EquipmentSlot, FishingSlot, FishingSlotName } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { libIcon } from "./buffBar.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const FISHING_ROD_SHAPES = [49, 50]; // Globals.FishingRodShapes

/** UserObject.HasFishingRod (User.Weapon in FishingRodShapes): the equipped weapon, or null. */
function fishingRod(inv: Inventory): UserItem | null {
  const rod = inv.equipment[EquipmentSlot.Weapon] ?? null;
  const info = rod ? inv.info(rod) : undefined;
  return rod && info && FISHING_ROD_SHAPES.includes(info.Shape) ? rod : null;
}


export class FishingWindow {
  /** The HUD Fishing button (MainDialogs FishingButton); game.ts places it. */
  readonly hudButton: HTMLButtonElement;
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (frame: Uint8Array) => void;
  private readonly requestItem: (index: number) => void;
  private readonly now: () => number;
  private readonly root = el("div", { class: "window fishing-window", hidden: true });
  private readonly title = el("span", {}, "Fishing");
  private readonly rodImage = el("img", { class: "fishing-rod", alt: "", draggable: false }) as HTMLImageElement;
  private rodIndex = -1;
  private readonly grid = el("div", { class: "inv-grid fishing-grid" });

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (frame: Uint8Array) => void,
    requestItem: (index: number) => void, now: () => number) {
    this.stage = stage;
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.requestItem = requestItem;
    this.now = now;
    this.hudButton = el("button", { title: "Fishing (N)", onclick: () => this.toggle() }, "Fishing") as HTMLButtonElement;
    this.root.append(
      el("div", { class: "window-title" }, this.title, el("button", { class: "window-close", title: "Close (N)", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "fishing-rod-box" }, this.rodImage),
      this.grid,
      el("div", { class: "socket-hint" }, "Right-click a fishing part in the bag to fit it; right-click a slot to take it off."));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Fishing)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall)) this.toggle(false);
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** FishingDialog.Show (refused with NoFishingRod without a rod) / Hide. */
  toggle(show = this.root.hidden): void {
    if (show && this.root.hidden && !fishingRod(this.inv)) {
      messageBox(this.stage, "You are not holding a fishing rod."); // ClientTextKeys.NoFishingRod
      return;
    }
    this.root.hidden = !show;
    this.render();
  }

  /** PlayerObject.FishingUpdate (the user's part): the dialog hides once the rod is gone. */
  handle(p: ServerPacket, userId: number): boolean {
    if (p.type !== "FishingUpdate" || p.ObjectID !== userId) return false;
    if (!fishingRod(this.inv)) this.toggle(false);
    return true;
  }

  /** FishingDialog_BeforeDraw (title) + FishingRod_BeforeDraw (rod picture) + the FishingSlot cells. */
  render(): void {
    if (this.root.hidden) return;
    const rod = fishingRod(this.inv);
    const info = rod ? this.inv.info(rod) : undefined;
    if (rod && info) this.title.textContent = itemFriendlyName(info) + (rod.Count > 1 ? ` (${rod.Count})` : "");
    let image = 0;
    if (rod && info) {
      image = info.Shape === 49 ? 1333 : 1335; // the desktop reads User.Weapon (the look shape)
      if (rod.Slots.length >= 5 && rod.Slots[FishingSlot.Hook]) image++;
    }
    if (image !== this.rodIndex) {
      this.rodIndex = image;
      this.rodImage.removeAttribute("src");
      if (image) void libIcon("StateItem", image).then((url) => { if (url && this.rodIndex === image) this.rodImage.src = url; });
    }
    const cells: HTMLElement[] = [];
    const n = rod ? Math.min(rod.Slots.length, 5) : 0;
    for (let i = 0; i < n; i++) {
      const s = rod!.Slots[i];
      if (s && !this.inv.info(s)) this.requestItem(s.ItemIndex); // GameScene.Bind
      cells.push(this.invWindow.slotItemCell("fishing", i, FishingSlotName[i] ?? ""));
    }
    this.grid.replaceChildren(...cells);
  }
}

export class FishingStatusWindow {
  /** FishingStatusDialog.ChancePercent / ProgressPercent (S.FishingUpdate). */
  chancePercent = 0;
  progressPercent = 0;
  /** PlayerObject.FoundFish: the Reel button (FishButton) is live only while a fish bites. */
  private foundFish = false;
  private canAutoCast = false;
  private autoCast = false;
  /** bEscExit: Escape reels in and closes. */
  private escExit = false;
  private readonly inv: Inventory;
  private readonly send: (frame: Uint8Array) => void;
  private readonly root = el("div", { class: "window fishing-status", hidden: true });
  private readonly chanceFill = el("div", { class: "fishing-bar-fill chance" });
  private readonly chanceLabel = el("span", { class: "fishing-bar-label" }, "0%");
  private readonly progressFill = el("div", { class: "fishing-bar-fill progress" });
  private readonly reelButton = el("button", { class: "primary", title: "Reel in" }, "Reel") as HTMLButtonElement;
  /** AutoCastButton + AutoCastBox (hidden without a reel). */
  private readonly autoButton = el("button", { title: "Cast again automatically (needs a reel)" }, "Auto cast") as HTMLButtonElement;
  private readonly escBox = el("input", { type: "checkbox" }) as HTMLInputElement;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (frame: Uint8Array) => void) {
    this.inv = inv;
    this.send = send;
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); }; // BeforeDraw re-checks the reel slot
    this.reelButton.onclick = () => this.send(ClientPackets.FishingCast({ CastOut: false })); // FishButton.Click
    this.autoButton.onclick = () => this.toggleAutoCast();
    this.escBox.onchange = () => { this.escExit = this.escBox.checked; }; // ESCExitButton.Click
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Fishing"), el("button", { class: "window-close", title: "Stop fishing", onclick: () => this.cancel() }, "×")),
      el("label", { class: "fishing-check" }, this.escBox, "Esc exit"),
      el("div", { class: "fishing-bar", title: "Chance" }, this.chanceFill, this.chanceLabel),
      el("div", { class: "fishing-bar thin", title: "Progress" }, this.progressFill),
      el("div", { class: "row fishing-buttons" }, this.reelButton, this.autoButton));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall) && this.escExit) this.cancel(); }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** GameScene.FishingUpdate (the user's part): bars, label, FoundFish, Show while fishing else Hide. */
  handle(p: ServerPacket, userId: number): boolean {
    if (p.type !== "FishingUpdate" || p.ObjectID !== userId) return false;
    this.progressPercent = p.ProgressPercent;
    this.chancePercent = p.ChancePercent;
    this.foundFish = p.FoundFish;
    this.root.hidden = !p.Fishing;
    this.render();
    return true;
  }

  /** FishingStatusDialog.Cancel (close button, Esc with "Esc exit"): reel in when open, then hide. */
  cancel(): void {
    if (!this.root.hidden) this.send(ClientPackets.FishingCast({ CastOut: false }));
    this.root.hidden = true;
  }

  /** AutoCastButton.Click: only with a reel in the rod. */
  private toggleAutoCast(): void {
    this.refreshAutoCast();
    if (!this.canAutoCast) return;
    this.autoCast = !this.autoCast;
    this.send(ClientPackets.FishingChangeAutocast({ AutoCast: this.autoCast }));
    this.render();
  }

  /** FishingStatusDialog_BeforeDraw: auto cast needs a rod with a Reel; losing it turns auto cast off on the server. */
  private refreshAutoCast(): void {
    const rod = this.inv.equipment[EquipmentSlot.Weapon] ?? null;
    const info = rod ? this.inv.info(rod) : undefined;
    if (rod && info && FISHING_ROD_SHAPES.includes(info.Shape)) this.canAutoCast = rod.Slots.length >= 5 && !!rod.Slots[FishingSlot.Reel];
    if (this.autoCast && !this.canAutoCast) {
      this.autoCast = false;
      this.send(ClientPackets.FishingChangeAutocast({ AutoCast: false }));
    }
  }

  /** The bar widths (ChanceBar / ProgressBar_BeforeDraw: 2.16 px per percent, clamped to 216) and the buttons. */
  render(): void {
    if (this.root.hidden) return;
    this.refreshAutoCast();
    const pct = (v: number) => `${Math.max(0, Math.min(100, v))}%`;
    this.chanceFill.style.width = pct(this.chancePercent);
    this.chanceLabel.textContent = `${this.chancePercent}%`;
    this.progressFill.style.width = pct(this.progressPercent);
    this.reelButton.disabled = !this.foundFish;
    this.reelButton.classList.toggle("fishing-bite", this.foundFish);
    this.autoButton.hidden = !this.canAutoCast;
    this.autoButton.textContent = `Auto cast: ${this.autoCast ? "On" : "Off"}`; // AutoCastBox 1344 / 1343
    this.autoButton.classList.toggle("primary", this.autoCast);
    this.escBox.checked = this.escExit;
  }
}
