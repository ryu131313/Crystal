// Mount window: MountDialog (Client/MirScenes/Dialogs/MountDialog.cs) with the desktop artwork: Prguse 167 (5 slots) /
// 160 (4 slots), the mount name + loyalty labels, the Ride button (Prguse 155 / 164), the Help button, the animated
// MountImage (Prguse StartIndex + MountType * 20, 16 frames at 100 ms, MirAnimated) and the MountSlot cells (Reins /
// Bells / Saddle / Ribbon / Mask; 4-slot mounts hide the Mask). Ride / M = MountDialog.CanRide + Ride (chat "@ride");
// J or the HUD Mount button toggles the window (KeybindOptions.MountWindow), refused with NoMount without a mount type;
// Escape (Closeall) hides it. Slot cells are InventoryWindow.boundCell("mount", i) (shared pick / lock state): right-click
// a mount item in the bag to fit it (MirItemCell.UseSlotItem), right-click a slot to take it off (MirItemCell.RemoveItem).
// Models: src/game/itemChanges.ts (game.ts sets its userState) and src/game/mountState.ts (MountTime, CanRide, Ride). The
// Help button calls `onHelp` (classicUi.ts sets it to the HelpDialog "Mounts" page). The M ride key is a gameplay key of
// the game's key router (classicUi.ts bindInput).

import { EquipmentSlot } from "../protocol/enums.generated.ts";
import type { ItemChanges } from "../game/itemChanges.ts";
import type { MountState } from "../game/mountState.ts";
import type { Inventory } from "../game/inventory.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirButton, MirLabel, Lib, closeButton } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

/** What the window needs from the user's MapObj (GameScene.User); defined by the model. */
export type { MountUserState } from "../game/mountState.ts";

const SLOTS = 5; // Enum.GetNames(typeof(MountSlot)).Length

export class MountWindow {
  /** HelpButton.Click (HelpDialog.DisplayPage("Mounts")); set by game.ts. */
  onHelp: (() => void) | null = null;
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly mount: MountState;
  private readonly requestItem: (index: number) => void;
  private readonly win: MirWindow;
  private readonly mountName: MirLabel;
  private readonly mountLoyalty: MirLabel;
  private readonly mountButton: MirButton;
  private readonly closeBtn: MirButton;
  private readonly helpButton: MirButton;
  private readonly mountImage: MirAnimated;
  private readonly grid: HTMLElement[] = [];
  /** MountDialog.StartIndex. */
  private startIndex = 1330;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, _changes: ItemChanges, mount: MountState,
    requestItem: (index: number) => void) {
    this.stage = stage;
    this.inv = inv;
    this.mount = mount;
    this.requestItem = requestItem;

    // MountDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 167, movable: true, sort: true, location: [10, 30], onShow: () => this.render(), onHide: () => this.render() });
    this.mountName = new MirLabel({ parent: this.win, location: [30, 10], size: [260, 15], center: true, notControl: true });
    this.mountLoyalty = new MirLabel({ parent: this.win, location: [30, 30], size: [260, 15], center: true, notControl: true });
    this.mountButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 155, hoverIndex: 156, pressedIndex: 157, location: [262, 70], sound: SoundList.ButtonA,
      onClick: () => { if (mount.canRide()) mount.ride(); } });
    this.closeBtn = closeButton(this.win, [297, 3], () => this.toggle(false));
    this.helpButton = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 257, hoverIndex: 258, pressedIndex: 259, location: [274, 3], sound: SoundList.ButtonA,
      onClick: () => this.onHelp?.() });
    this.mountImage = new MirAnimated({ parent: this.win, library: Lib.Prguse, index: 0, animated: false, animationCount: 16, animationDelay: 100, loop: true,
      notControl: true, useOffset: true, drawImage: false });
    for (let i = 0; i < SLOTS; i++) this.grid.push(placeCell(invWindow.boundCell("mount", i, [34, 30]), this.win, [0, 0]));
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    this.switchType(5);

    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    // GameScene.MountUpdate (the user's part): MountType < 0 hides the dialog, then RefreshDialog
    const offUpdated = mount.updated.on(({ mountType }) => {
      if (mountType < 0) this.win.hide();
      this.render(mountType);
    });
    signal.addEventListener("abort", () => { offUpdated(); this.win.dispose(); });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.MountWindow)) this.toggle();
      // ride: the game's key router (classicUi.ts bindInput, which re-checks canRide); a Mount key that cannot ride
      // falls through to Closeall, like the old chain
      else if (matches(e, KeybindOptions.Mount) && mount.canRide()) { /* router rides */ }
      else if (matches(e, KeybindOptions.Closeall)) this.toggle(false);
    }, { signal });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** MountDialog.Show (refused with NoMount when User.MountType < 0) / Hide. */
  toggle(show = !this.win.visible): void {
    if (show && !this.win.visible && (this.mount.user()?.mountType ?? -1) < 0) {
      messageBox(this.stage, "You do not own a mount."); // ClientTextKeys.NoMount
      return;
    }
    if (show) this.win.show(); else this.win.hide();
  }

  /** MountDialog_BeforeDraw -> RefreshDialog: SwitchType + DrawMountAnimation. */
  render(mountType = this.mount.user()?.mountType ?? -1): void {
    if (!this.win.visible) { this.mountImage.animated = false; return; }
    const mount = this.inv.equipment[EquipmentSlot.Mount] ?? null;
    const slots = mount?.Slots ?? null;
    if (slots) {
      this.switchType(slots.length);
      for (const s of slots) if (s && !this.inv.info(s)) this.requestItem(s.ItemIndex); // GameScene.Bind
    }
    // DrawMountAnimation
    if (mountType < 0) {
      // the desktop sets Index 0 with Animated false (its Prguse[0] is never on screen: the dialog hides without a mount)
      this.mountImage.index = 0;
      this.mountImage.animated = false;
      this.mountImage.drawImage = false;
      return;
    }
    this.mountImage.drawImage = true;
    this.mountImage.index = this.startIndex + mountType * 20;
    this.mountImage.animated = true;
    if (!mount) return;
    const info = this.inv.info(mount);
    if (!info) { this.requestItem(mount.ItemIndex); return; }
    this.mountName.text = itemFriendlyName(info) + (mount.Count > 1 ? ` (${mount.Count})` : ""); // UserItem.FriendlyName
    this.mountLoyalty.text = `${mount.CurrentDura} / ${mount.MaxDura} Loyalty`; // ClientTextKeys.LoyaltyProgress
  }

  /** MountDialog.SwitchType: the 4-slot (Prguse 160) or 5-slot (Prguse 167) layout. */
  private switchType(count: number): void {
    let x = 0, y = 0;
    if (count === 4) {
      this.win.index = 160;
      this.startIndex = 1170;
      this.mountName.size = [208, 15];
      this.mountLoyalty.size = [208, 15];
      this.mountImage.location = [110, 250];
      this.mountButton.setIndices(164, 165, 166);
      this.mountButton.location = [210, 70];
      this.closeBtn.location = [245, 3];
      this.helpButton.location = [221, 3];
      this.grid[4].hidden = true;
      x = 1; y = 1;
    } else if (count === 5) {
      this.win.index = 167;
      this.startIndex = 1330;
      this.mountName.size = [260, 15];
      this.mountLoyalty.size = [260, 15];
      this.mountImage.location = [0, 70];
      this.mountButton.setIndices(155, 156, 157);
      this.mountButton.location = [262, 70];
      this.closeBtn.location = [297, 3];
      this.helpButton.location = [274, 3];
      this.grid[4].hidden = false;
    } else return;
    for (let i = 0; i < SLOTS; i++) {
      this.grid[i].style.left = 36 + i * 54 + x + "px";
      this.grid[i].style.top = 323 + y + "px";
    }
  }
}
