// Fishing windows with the desktop artwork (Client/MirScenes/Dialogs/FishingDialog.cs).
// FishingWindow = FishingDialog (Prguse 1340): the equipped rod's name (TitleLabel), the rod picture (FishingRod:
// StateItem 1333 / 1335, +1 with a hook, at (10, 40)) and the FishingSlot cells (Hook / Float / Bait / Finder / Reel);
// N or the HUD Fishing button toggles it (KeybindOptions.Fishing), refused with NoFishingRod without a rod; Escape
// (Closeall) hides it. Slot cells are InventoryWindow.boundCell("fishing", i): right-click a fishing part in the bag to
// fit it (MirItemCell.UseSlotItem, bait merges onto the bait stack), right-click a slot to take it off (RemoveItem).
// FishingStatusWindow = FishingStatusDialog (Prguse 1341): shown while S.FishingUpdate says the user is fishing; chance
// bar (Prguse 1342) + label, progress bar (Prguse 1349), the animated FishButton (Title 170, 10 frames, over the
// disabled Title 149; C.FishingCast CastOut = false, shown while a fish bites), AutoCastButton (Title 180) + AutoCastBox
// (Prguse 1343 / 1344; needs a Reel in the rod; C.FishingChangeAutocast) and the "Esc exit" tick (Prguse 1346 / 1347,
// Title 45). The state (bars, FoundFish, auto cast, Esc exit) is the Fishing model (src/game/fishing.ts); both windows
// follow its `updated` event. Casting out (GameScene.CanFish + C.FishingCast CastOut = true on a map click) lives in
// game.ts checkInput.
// Not ported: the FishingWait splash effects (PlayerObject.ProcessFrames); the FishButton follows S.FishingUpdate's
// FoundFish instead of the FishingWait frame.

import { FishingSlot } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import { fishingRod, type Fishing } from "../game/fishing.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, Lib, closeButton, uiSound, SCREEN_WIDTH } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

/** FishingDialog.Grid locations by FishingSlot. */
const GRID_LOCATIONS: readonly (readonly [number, number])[] = [[17, 203], [17, 241], [57, 241], [97, 241], [137, 241]];

export class FishingWindow {
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly requestItem: (index: number) => void;
  private readonly win: MirWindow;
  private readonly titleLabel: MirLabel;
  private readonly fishingRod: MirImage;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, fishing: Fishing,
    requestItem: (index: number) => void) {
    this.stage = stage;
    this.inv = inv;
    this.requestItem = requestItem;

    // FishingDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 1340, movable: true, sort: true, location: "center", onShow: () => this.render() });
    this.titleLabel = new MirLabel({ parent: this.win, location: [10, 4], size: [180, 20], center: true, notControl: true });
    // FishingRod_BeforeDraw: StateItems.Draw(image, Location + (10, 40), no offset)
    this.fishingRod = new MirImage({ parent: this.win, library: Lib.StateItems, index: 0, location: [10, 40], notControl: true, drawImage: false });
    closeButton(this.win, [175, 3], () => this.toggle(false));
    for (let i = 0; i < GRID_LOCATIONS.length; i++) placeCell(invWindow.boundCell("fishing", i, [34, 30]), this.win, GRID_LOCATIONS[i]);
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());

    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    // PlayerObject.FishingUpdate (the user's part): the dialog hides once the rod is gone
    const offUpdated = fishing.updated.on(() => { if (!fishingRod(this.inv)) this.toggle(false); });
    signal.addEventListener("abort", () => { offUpdated(); this.win.dispose(); });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Fishing)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall)) this.toggle(false);
    }, { signal });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** FishingDialog.Show (refused with NoFishingRod without a rod) / Hide. */
  toggle(show = !this.win.visible): void {
    if (show && !this.win.visible && !fishingRod(this.inv)) {
      messageBox(this.stage, "You are not holding a fishing rod."); // ClientTextKeys.NoFishingRod
      return;
    }
    if (show) this.win.show(); else this.win.hide();
  }

  /** FishingDialog_BeforeDraw (title) + FishingRod_BeforeDraw (rod picture). */
  render(): void {
    if (!this.win.visible) return;
    const rod = fishingRod(this.inv);
    const info = rod ? this.inv.info(rod) : undefined;
    if (rod && info) this.titleLabel.text = itemFriendlyName(info) + (rod.Count > 1 ? ` (${rod.Count})` : ""); // UserItem.FriendlyName
    let image = 0;
    if (rod && info) {
      image = info.Shape === 49 ? 1333 : 1335; // the desktop reads User.Weapon (the look shape)
      if (rod.Slots.length >= 5 && rod.Slots[FishingSlot.Hook]) image++;
    }
    this.fishingRod.drawImage = image !== 0; // the desktop would draw StateItems[0]; the dialog is never open without a rod
    this.fishingRod.index = image;
    if (rod) for (const s of rod.Slots) if (s && !this.inv.info(s)) this.requestItem(s.ItemIndex); // GameScene.Bind
  }
}

export class FishingStatusWindow {
  /** ChancePercent / ProgressPercent, FoundFish, auto cast and Esc exit (src/game/fishing.ts). */
  private readonly fishing: Fishing;
  private readonly win: MirWindow;
  private readonly chanceBar: MirImage;
  private readonly chanceLabel: MirLabel;
  private readonly progressBar: MirImage;
  private readonly fishButton: MirAnimated;
  private readonly autoCastButton: MirButton;
  private readonly autoCastBox: MirImage;
  private readonly escTick: MirImage;

  constructor(_stage: HTMLElement, invWindow: InventoryWindow, fishing: Fishing) {
    this.fishing = fishing;
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); }; // BeforeDraw re-checks the reel slot

    // FishingStatusDialog ctor (Size 244 x 128, Location ((ScreenWidth - 244) / 2, 300))
    this.win = new MirWindow({ library: Lib.Prguse, index: 1341, movable: true, sort: true, size: [244, 128], location: [Math.floor((SCREEN_WIDTH - 244) / 2), 300] });
    // ChanceBar_BeforeDraw / ProgressBar_BeforeDraw: Prguse 1342 / 1349 clipped to 2.16 px per percent
    this.chanceBar = this.bar(1342, [14, 64], 12);
    this.chanceLabel = new MirLabel({ parent: this.win, location: [14, 62], size: [216, 12], center: true, notControl: true });
    this.progressBar = this.bar(1349, [14, 79], 8);
    closeButton(this.win, [216, 4], () => this.cancel());
    new MirImage({ parent: this.win, library: Lib.Title, index: 149, location: [47, 95], notControl: true }); // FishDisableButton
    // FishButton: MirAnimatedButton (Index 170 + OffSet, PressedIndex 142)
    this.fishButton = new MirAnimated({ parent: this.win, library: Lib.Title, index: 170, animated: true, animationCount: 10, loop: true, animationDelay: 130,
      location: [47, 95], visible: false });
    const pressed = new MirImage({ parent: this.fishButton, library: Lib.Title, index: 142, location: [0, 0], notControl: true, visible: false });
    this.fishButton.el.addEventListener("pointerdown", (e) => { if (e.button === 0) pressed.visible = true; });
    for (const t of ["pointerup", "pointerleave"] as const) this.fishButton.el.addEventListener(t, () => { pressed.visible = false; });
    this.fishButton.onClick(() => { uiSound(SoundList.ButtonA); fishing.reelIn(); });
    this.autoCastButton = new MirButton({ parent: this.win, library: Lib.Title, index: 180, hoverIndex: 181, pressedIndex: 182, location: [110, 95], sound: SoundList.ButtonA,
      onClick: () => this.toggleAutoCast() });
    this.autoCastBox = new MirImage({ parent: this.win, library: Lib.Prguse, index: 1343, location: [172, 95] });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 1346, hoverIndex: 1346, pressedIndex: 1346, location: [135, 41], sound: SoundList.ButtonA,
      onClick: () => { fishing.toggleEscExit(); this.escTick.visible = fishing.escExit; } }); // ESCExitButton
    this.escTick = new MirImage({ parent: this.win, library: Lib.Prguse, index: 1347, location: [135, 41], notControl: true, visible: false });
    new MirImage({ parent: this.win, library: Lib.Title, index: 45, location: [150, 40], notControl: true }); // ESCExit

    const signal = gameSignal();
    const offUpdated = fishing.updated.on(() => this.update());
    signal.addEventListener("abort", () => { offUpdated(); this.win.dispose(); });
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall) && fishing.escExit) this.cancel(); }, { signal });
  }

  /** A NotControl bar: the picture clipped to the control's width. */
  private bar(index: number, location: readonly [number, number], height: number): MirImage {
    const b = new MirImage({ parent: this.win, library: Lib.Prguse, index, location, size: [0, height], notControl: true });
    b.el.style.overflow = "hidden";
    return b;
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** GameScene.FishingUpdate (the user's part, after the model update): label, Show while fishing else Hide, bars. */
  private update(): void {
    const f = this.fishing;
    this.chanceLabel.text = `${f.chancePercent}%`;
    if (f.fishing) this.win.show(); else this.win.hide();
    this.render();
  }

  /** FishingStatusDialog.Cancel (close button, Esc with "Esc exit"): reel in when open, then hide. */
  cancel(): void {
    if (this.win.visible) this.fishing.reelIn();
    this.win.hide();
  }

  /** AutoCastButton.Click: only with a reel in the rod. */
  private toggleAutoCast(): void {
    this.refreshAutoCast();
    if (this.fishing.toggleAutoCast()) this.autoCastBox.index = this.fishing.autoCast ? 1344 : 1343;
  }

  /** FishingStatusDialog_BeforeDraw: the model's auto cast check; the controls follow it while a rod is held. */
  private refreshAutoCast(): void {
    const f = this.fishing;
    if (f.refreshAutoCast()) {
      this.autoCastBox.visible = f.canAutoCast;
      this.autoCastButton.visible = f.canAutoCast;
    }
  }

  /** BeforeDraw: auto cast, the bar widths (2.16 px per percent, clamped to 216) and the FishButton. */
  render(): void {
    if (!this.win.visible) return;
    this.refreshAutoCast();
    const f = this.fishing;
    const width = (pct: number) => Math.max(0, Math.min(216, Math.trunc(2.16 * pct)));
    this.chanceBar.size = [width(f.chancePercent), 12];
    this.progressBar.size = [width(f.progressPercent), 8];
    this.fishButton.visible = f.foundFish;
  }
}
