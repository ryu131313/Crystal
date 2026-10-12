// Intelligent creatures window with the desktop artwork (Client/MirScenes/Dialogs/IntelligentCreatureDialogs.cs):
// IntelligentCreatureDialog (Title 468), its CreatureButtons (Prguse2[Icon] + the 535 selection frame + the hover name
// label), IntelligentCreatureOptionsDialog (Title 469, nine Prguse 2086 / 2087 check boxes, Save / Cancel) and
// IntelligentCreatureOptionsGradeDialog (Prguse 237, Prev / Next), plus the dialog side of
// GameScene.NewIntelligentCreature / UpdateIntelligentCreatureList / IntelligentCreatureEnableRename. The model is
// src/game/creatures.ts. E (KeybindOptions.Creature) or the HUD Creatures button toggles it, Escape (Closeall) hides it;
// X (CreaturePickup) and Alt+A (CreatureAutoPickup) send C.IntelligentCreaturePickup for the cell under the mouse
// (200 ms throttle). Showing the window asks the server for live updates (C.RequestIntelligentCreatureUpdates true),
// hiding stops them. The CreatureImage is a MirAnimated (Prguse2 CreatureButton.SetCreatureFrames: the default loop, the
// extra loop once SwitchAnimTime has passed). Not ported: the food-buff label (invisible on the desktop too); the
// HelpPetButton is drawn but has no click handler, like the desktop.

import type { ServerPacket, ClientIntelligentCreature, IntelligentCreatureItemFilter } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, IntelligentCreaturePickupMode, ItemGrade } from "../protocol/enums.generated.ts";
import {
  BLACKSTONE_PRODUCE_TIME, CREATURE_NAME, Creatures, creatureFrames, defaultItemFilter, itemFilterFlags, setItemFilter,
} from "../game/creatures.ts";
import { dateFromBinary, printTimeSpan } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirCheckBox, MirLabel, Lib, closeButton, libSize, type Point } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { inputBox, messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const BUTTONS = 10; // IntelligentCreatureDialog.CreatureButtons

/** IntelligentCreatureOptionsDialog.OptionNames (ClientTextKeys.AllItems .. Others). */
const OPTION_NAMES = ["All Items", "Gold", "Weapons", "Armours", "Helmets", "Boots", "Belts", "Jewelry", "Others"];

/** The grade label by ItemGrade value. The desktop's GradeStrings list Mythical before Legendary while the index is the
 *  ItemGrade value (so it labels Legendary "Mythical"); the web names each grade by its value. */
const GRADE_NAMES: Record<number, string> = {
  [ItemGrade.None]: "All", [ItemGrade.Common]: "Common", [ItemGrade.Rare]: "Rare", [ItemGrade.Legendary]: "Legendary",
  [ItemGrade.Mythical]: "Mythical", [ItemGrade.Heroic]: "Heroic",
};
/** IntelligentCreatureOptionsGradeDialog.GradeNameColor (Color.Yellow / DeepSkyBlue / DarkOrange / Plum / Red / White). */
const GRADE_COLOURS: Record<number, string> = {
  [ItemGrade.Common]: "#ffff00", [ItemGrade.Rare]: "#00bfff", [ItemGrade.Legendary]: "#ff8c00", [ItemGrade.Mythical]: "#dda0dd", [ItemGrade.Heroic]: "#ff0000",
};
const GRADE_MAX = ItemGrade.Heroic;

/** Option / grade dialog offsets from the creature dialog (locationOffset). */
const OPTIONS_OFFSET: Point = [450, 63];
const GRADE_OFFSET: Point = [449, 39];

/** CreatureButton (MirControl, Size 231 x 33): PetButton + SelectionImage + NameLabel. */
interface CreatureButtonView {
  root: MirControl;
  petButton: MirButton;
  selection: MirImage;
  name: MirLabel;
  petType: number;
  selected: boolean;
}

type HoverTarget = "min" | "fullness" | "blackstone" | null;

export class CreatureWindow {
  private readonly stage: HTMLElement;
  private readonly creatures: Creatures;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly mouseCell: () => { X: number; Y: number } | null;
  private readonly now: () => number;

  private readonly win: MirWindow;
  private readonly renameButton: MirButton;
  private readonly summonButton: MirButton;
  private readonly dismissButton: MirButton;
  private readonly releaseButton: MirButton;
  private readonly optionsButton: MirButton;
  private readonly automaticButton: MirButton;
  private readonly semiAutoButton: MirButton;
  private readonly buttons: CreatureButtonView[] = [];
  /** Holds the CreatureImage at its z-order (the MirAnimated is rebuilt when the loop changes). */
  private readonly imageHolder: MirControl;
  private creatureImage: MirAnimated | null = null;
  private animKey = "";
  private readonly fullnessFG: MirControl;
  private readonly fullnessFGPic: MirImage;
  private readonly fullnessMin: MirImage;
  private readonly fullnessNow: MirImage;
  private readonly blackStoneBG: MirImage;
  private readonly blackStoneFG: MirControl;
  private readonly blackStoneFGPic: MirImage;
  private readonly creatureName: MirLabel;
  private readonly creatureDeadline: MirLabel;
  private readonly creaturePearls: MirLabel;
  private readonly creatureInfo: MirLabel[];
  private readonly hoverLabel: MirLabel;
  private hoverTarget: HoverTarget = null;
  /** FullnessFG / BlackStoneImageFG sizes (Library.GetTrueSize; [0, 0] while loading). */
  private fullnessSize: readonly [number, number] = [0, 0];
  private blackStoneSize: readonly [number, number] = [0, 0];
  private blackStoneBGSize: readonly [number, number] = [0, 0];

  // IntelligentCreatureOptionsDialog + IntelligentCreatureOptionsGradeDialog
  private readonly optionsWin: MirWindow;
  private readonly optionChecks: MirCheckBox[] = [];
  private readonly gradeWin: MirWindow;
  private readonly gradeLabel: MirLabel;
  private optionFilter: IntelligentCreatureItemFilter = defaultItemFilter();
  private gradeType: number = ItemGrade.None;

  /** IntelligentCreatureDialog.SelectedCreatureSlot (a button index; the creature is the one whose SlotIndex matches). */
  private selectedSlot = -1;
  private showing = false;
  /** SwitchAnimTime / AnimSwitched (DrawCreatureAnimation). */
  private switchAnimTime = 0;
  private animSwitched = false;
  private timer = 0;
  private lastRefresh = 0;
  private pickupTime = 0; // GameScene.IntelligentCreaturePickupTime

  constructor(stage: HTMLElement, creatures: Creatures, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void,
    mouseCell: () => { X: number; Y: number } | null, now: () => number = () => performance.now()) {
    this.stage = stage;
    this.creatures = creatures;
    this.send = send;
    this.chat = chat;
    this.mouseCell = mouseCell;
    this.now = now;

    // — IntelligentCreatureDialog ctor —
    const win = this.win = new MirWindow({ library: Lib.Title, index: 468, movable: true, sort: true, location: "center" });
    const close = closeButton(win, [0, 3], () => this.hide());
    const help = new MirButton({ parent: win, library: Lib.Prguse2, index: 257, hoverIndex: 258, pressedIndex: 259, location: [0, 3], sound: SoundList.ButtonA });
    void win.ready.then(() => { // Location = (Size.Width - 25 / 48, 3)
      close.location = [win.size[0] - 25, 3];
      help.location = [win.size[0] - 48, 3];
    });
    const titleButton = (index: number, location: Point, onClick: () => void, visible = true) =>
      new MirButton({ parent: win, library: Lib.Title, index, hoverIndex: index + 1, pressedIndex: index + 2, location, sound: SoundList.ButtonA, visible, onClick });
    this.renameButton = titleButton(570, [344, 50], () => this.rename(), false);
    this.summonButton = titleButton(576, [113, 217], () => this.summon(true));
    this.dismissButton = titleButton(580, [113, 217], () => this.summon(false));
    this.releaseButton = titleButton(583, [255, 217], () => this.release());
    this.optionsButton = titleButton(573, [375, 160], () => this.showOptions());
    this.automaticButton = titleButton(610, [375, 187], () => this.switchMode()); // the image reads "Enable" (should be "Auto")
    this.semiAutoButton = titleButton(613, [375, 187], () => this.switchMode()); // the image reads "Disable" (should be "SemiAuto")

    for (let i = 0; i < BUTTONS; i++) {
      const location: Point = [44 + (i % 5) * 81, 259 + (i >= 5 ? 40 : 0)];
      // the 231 x 33 CreatureButton overlaps its neighbours: only the PetButton takes the mouse
      const root = new MirControl({ parent: win, location, size: [231, 33], visible: false, notControl: true });
      const petButton = new MirButton({ parent: root, library: Lib.Prguse2, index: 0, pressedIndex: 1, location: [0, 0], sound: SoundList.ButtonA,
        onClick: () => this.selectButton(i) }); // CreatureButton.PetButtonClick
      petButton.el.style.pointerEvents = "auto";
      const selection = new MirImage({ parent: root, library: Lib.Prguse2, index: 535, location: [-2, -2], notControl: true, visible: false });
      const name = new MirLabel({ parent: root, location: [-22, -12], size: [80, 15], center: true, notControl: true, visible: false });
      petButton.el.addEventListener("pointerenter", () => { name.visible = true; }); // PetButtonMouseEnter
      petButton.el.addEventListener("pointerleave", () => { name.visible = false; });
      this.buttons.push({ root, petButton, selection, name, petType: -1, selected: false });
    }

    // CreatureImage + the bars
    this.imageHolder = new MirControl({ parent: win, location: [0, 0], notControl: true });
    new MirImage({ parent: win, library: Lib.Prguse2, index: 530, location: [185, 129], notControl: true }); // FullnessBG (NotControl: no hover)
    this.fullnessFG = new MirControl({ parent: win, location: [185, 129] });
    this.fullnessFGPic = this.clipped(this.fullnessFG, 531);
    this.fullnessMin = new MirImage({ parent: win, library: Lib.Prguse2, index: 532, location: [179, 118] });
    this.fullnessNow = new MirImage({ parent: win, library: Lib.Prguse2, index: 533, location: [179, 143], notControl: true });
    new MirImage({ parent: win, library: Lib.Prguse2, index: 427, location: [29, 348], notControl: true }); // PearlImage
    this.blackStoneBG = new MirImage({ parent: win, library: Lib.Prguse2, index: 428, location: [215, 348], notControl: true });
    this.blackStoneFG = new MirControl({ parent: win, location: [242, 353] });
    this.blackStoneFGPic = this.clipped(this.blackStoneFG, 420);
    void libSize(Lib.Prguse2, 531).then((s) => { this.fullnessSize = s; this.fullnessFG.size = s; this.refreshBars(); });
    void libSize(Lib.Prguse2, 420).then((s) => { this.blackStoneSize = s; this.blackStoneFG.size = s; this.refreshBars(); });
    void libSize(Lib.Prguse2, 428).then((s) => { this.blackStoneBGSize = s; });
    this.hover(this.fullnessMin.el, "min");
    this.hover(this.fullnessFG.el, "fullness");
    this.hover(this.blackStoneFG.el, "blackstone");

    // Labels
    this.creatureName = new MirLabel({ parent: win, location: [170, 50], size: [166, 21], center: true, notControl: true });
    this.creatureDeadline = new MirLabel({ parent: win, location: [140, 85], size: [350, 21], vCenter: true, notControl: true });
    this.creaturePearls = new MirLabel({ parent: win, location: [53, 348], autoSize: true, vCenter: true, text: "0", notControl: true });
    this.creatureInfo = [161, 176, 191].map((y) => new MirLabel({ parent: win, location: [19, y], size: [350, 15], vCenter: true, notControl: true }));
    this.hoverLabel = new MirLabel({ parent: win, location: [0, 0], size: [100, 15], center: true, notControl: true });

    // IntelligentCreatureDialog.OnMouseDown: the option dialogs stay in front of it
    win.el.addEventListener("pointerdown", () => {
      if (this.gradeWin.visible) this.gradeWin.bringToFront();
      if (this.optionsWin.visible) this.optionsWin.bringToFront();
    });

    // — IntelligentCreatureOptionsDialog ctor —
    this.optionsWin = new MirWindow({ library: Lib.Title, index: 469, movable: false, sort: true });
    for (let i = 0; i < OPTION_NAMES.length; i++) {
      this.optionChecks.push(new MirCheckBox({ parent: this.optionsWin, library: Lib.Prguse, tickedIndex: 2087, unTickedIndex: 2086, location: [16, 16 + i * 30],
        labelText: OPTION_NAMES[i], onChange: () => { setItemFilter(this.optionFilter, i); this.refreshFilter(); } })); // CheckBoxClick
    }
    new MirButton({ parent: this.optionsWin, library: Lib.Title, index: 586, hoverIndex: 587, pressedIndex: 588, location: [10, 280], sound: SoundList.ButtonA,
      onClick: () => this.saveOptions() });
    new MirButton({ parent: this.optionsWin, library: Lib.Title, index: 590, hoverIndex: 591, pressedIndex: 592, location: [60, 280], sound: SoundList.ButtonA,
      onClick: () => this.cancelOptions() });

    // — IntelligentCreatureOptionsGradeDialog ctor —
    this.gradeWin = new MirWindow({ library: Lib.Prguse, index: 237, movable: false, sort: true });
    new MirButton({ parent: this.gradeWin, library: Lib.Prguse, index: 396, hoverIndex: 396, pressedIndex: 397, location: [96, 5], sound: SoundList.ButtonA,
      onClick: () => this.stepGrade(1) });
    new MirButton({ parent: this.gradeWin, library: Lib.Prguse, index: 398, hoverIndex: 398, pressedIndex: 399, location: [76, 5], sound: SoundList.ButtonA,
      onClick: () => this.stepGrade(-1) });
    this.gradeLabel = new MirLabel({ parent: this.gradeWin, location: [8, 0], size: [70, 21], vCenter: true, notControl: true });


    const signal = gameSignal();
    signal.addEventListener("abort", () => {
      this.stopTimer();
      this.creatureImage?.dispose();
      this.win.dispose();
      this.optionsWin.dispose();
      this.gradeWin.dispose();
    });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindSettings defaults: Creature = E, CreaturePickup = X without Alt, CreatureAutoPickup = Alt+A without Ctrl
      // (Ctrl+A is the pet mode).
      if (matches(e, KeybindOptions.Creature) && !e.repeat) this.toggle();
      else if (matches(e, KeybindOptions.CreaturePickup)) this.pickup(true);
      else if (matches(e, KeybindOptions.CreatureAutoPickup)) { e.preventDefault(); this.pickup(false); }
      else if (matches(e, KeybindOptions.Closeall)) this.hide();
    }, { signal });
  }

  /** A FullnessFG / BlackStoneImageFG picture (DrawImage = false + AfterDraw's clipped Draw): the image inside a
   *  NotControl box whose width is the drawn section. */
  private clipped(parent: MirControl, index: number): MirImage {
    const pic = new MirImage({ parent, library: Lib.Prguse2, index, location: [0, 0], size: [0, 0], notControl: true });
    pic.el.style.overflow = "hidden";
    return pic;
  }

  /** Control_MouseEnter / Control_MouseLeave for the hover label. */
  private hover(target: HTMLElement, kind: Exclude<HoverTarget, null>): void {
    target.addEventListener("pointerenter", () => {
      if (this.selectedIndex() < 0) return;
      this.hoverTarget = kind;
      this.refreshHover();
    });
    target.addEventListener("pointerleave", () => {
      this.hoverTarget = null;
      this.hoverLabel.text = "";
      this.hoverLabel.visible = false;
    });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** GameScene KeybindOptions.Creature: Show / Hide. */
  toggle(show = !this.visible): void {
    if (show) this.show();
    else this.hide();
  }

  /** GameScene KeybindOptions.CreaturePickup (mouse) / CreatureAutoPickup (semi-auto): MapControl.MapLocation, 200 ms throttle. */
  private pickup(mouseMode: boolean): void {
    const t = this.now();
    if (t <= this.pickupTime) return;
    const cell = this.mouseCell();
    if (!cell) return;
    this.pickupTime = t + 200;
    this.send(ClientPackets.IntelligentCreaturePickup({ MouseMode: mouseMode, Location: { X: cell.X, Y: cell.Y } }));
  }

  /** Server packets: the model update (creatures.ts) plus the dialog half of the GameScene handlers. */
  handle(p: ServerPacket): boolean {
    const change = this.creatures.handle(p);
    if (!change) return false;
    switch (change.kind) {
      case "load":
        if (this.visible) this.refresh();
        return false; // UserInformation: others read it too
      case "replaced": // GameScene.UpdateIntelligentCreatureList: count changed
        for (const b of this.buttons) this.clearButton(b);
        this.hide();
        break;
      case "updated":
        if (this.visible) this.refresh();
        break;
      case "new": { // GameScene.NewIntelligentCreature: name the new creature (non-blocking MirInputBox)
        const creature = this.creatures.list[change.index];
        inputBox(this.stage, "Please give your creature a name.", creature.CustomName, (value) => {
          if (this.visible) this.refresh();
          creature.CustomName = value;
          this.sendUpdate(creature);
        });
        break;
      }
      case "enableRename": // GameScene.IntelligentCreatureEnableRename
        this.renameButton.visible = true;
        if (this.visible) this.refresh();
        break;
    }
    return true;
  }

  /** IntelligentCreatureDialog.Show. */
  show(): void {
    if (this.visible) return;
    if (this.creatures.list.length === 0) { messageBox(this.stage, "You do not own any creatures."); return; } // ClientTextKeys.NoCreatures
    if (!this.buttons.some((b) => b.selected)) this.selectButton(0, false);
    this.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: true }));
    this.win.show();
    this.showing = true;
    this.switchAnimTime = this.now() + 8000;
    this.animSwitched = false;
    this.refresh();
    this.stopTimer();
    this.timer = window.setInterval(() => this.tick(), 50);
  }

  /** IntelligentCreatureDialog.Hide (also hides the options / grade dialogs). */
  hide(): void {
    if (!this.visible) return;
    this.optionsWin.hide();
    this.gradeWin.hide();
    this.animSwitched = false;
    this.win.hide();
    this.stopTimer();
    this.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: false }));
  }

  private stopTimer(): void {
    if (this.timer) window.clearInterval(this.timer);
    this.timer = 0;
  }

  /** The desktop redraws every frame: BeforeDraw -> RefreshDialog (the expiry / blackstone texts tick) and the option
   *  dialogs' BeforeDraw (they follow the creature dialog). */
  private tick(): void {
    if (!this.visible) { this.stopTimer(); return; }
    this.followDialog();
    if (this.now() - this.lastRefresh >= 250) this.refresh();
  }

  /** IntelligentCreatureOptions(Grade)Dialog_BeforeDraw: Location = dialog Location + locationOffset. */
  private followDialog(): void {
    const [x, y] = this.win.location;
    this.optionsWin.location = [x + OPTIONS_OFFSET[0], y + OPTIONS_OFFSET[1]];
    this.gradeWin.location = [x + GRADE_OFFSET[0], y + GRADE_OFFSET[1]];
  }

  // — CreatureButton —

  /** CreatureButton.SelectButton: select this button only, restart the animation timer, Update(). */
  private selectButton(i: number, update = true): void {
    const b = this.buttons[i];
    if (b.selected) return;
    for (const o of this.buttons) { o.selected = false; o.selection.visible = false; }
    b.selected = true;
    b.selection.visible = true;
    this.selectedSlot = i;
    this.switchAnimTime = this.now() + 10000;
    this.animSwitched = false;
    if (update && this.visible) this.refresh();
  }

  /** CreatureButton.Clear. */
  private clearButton(b: CreatureButtonView): void {
    b.petType = -1;
    b.root.visible = false;
    b.selected = false;
    b.selection.visible = false;
  }

  // — Refresh (IntelligentCreatureDialog.RefreshDialog) —

  /** The list index of the selected creature, or -1 (BeforeAfterDraw / GetCreatureFromSlot). */
  private selectedIndex(): number {
    return this.selectedSlot < 0 ? -1 : this.creatures.fromSlot(this.selectedSlot);
  }

  private refresh(): void {
    this.lastRefresh = this.now();
    this.followDialog();
    this.refreshInfo();
    this.refreshUI();
    this.refreshMode();
    this.drawCreatureAnimation();
    this.refreshBars();
  }

  /** RefreshInfo: pearls, the buttons (CreatureButton.Update), and (on Show) the summoned creature's button selected. */
  private refreshInfo(): void {
    const c = this.creatures;
    this.creaturePearls.text = String(c.pearlCount);
    let selectedButton = -1;
    for (let i = 0; i < BUTTONS; i++) {
      const b = this.buttons[i];
      if (i >= c.list.length) { this.clearButton(b); continue; }
      const pet = c.list[i];
      b.root.visible = true;
      b.petType = pet.PetType;
      b.petButton.setIndices(pet.Icon, -1, pet.Icon); // SetButtonInfo: Index = PressedIndex = Icon
      b.name.text = pet.CustomName;
      if (this.showing && c.summoned && pet.PetType === c.summonedType) selectedButton = i;
    }
    this.showing = false;
    if (selectedButton >= 0) this.selectButton(selectedButton, false);
  }

  /** RefreshUI: which buttons are usable for the selection and the summon state. */
  private refreshUI(): void {
    const idx = this.selectedIndex();
    const has = idx >= 0;
    this.imageHolder.visible = has;
    this.creatureName.visible = has;
    this.creatureDeadline.visible = has;
    for (const l of this.creatureInfo) l.visible = has;
    if (!has) {
      this.renameButton.enabled = false;
      this.summonButton.enabled = false;
      this.dismissButton.enabled = false;
      this.dismissButton.visible = false;
      this.releaseButton.enabled = false;
      this.semiAutoButton.enabled = false;
      this.automaticButton.enabled = false;
      this.optionsButton.enabled = false;
      return;
    }
    this.renameButton.enabled = true;
    this.releaseButton.enabled = true;
    this.optionsButton.enabled = true;
    this.semiAutoButton.enabled = true;
    this.automaticButton.enabled = true;
    const c = this.creatures;
    if (c.summoned) {
      if (c.list[idx].PetType === c.summonedType) {
        this.dismissButton.enabled = true; // drawn over the Summon button
        this.dismissButton.visible = true;
        this.releaseButton.enabled = false;
      } else {
        this.summonButton.setIndices(593, 594, 595); // "another creature is summoned"
        this.summonButton.enabled = false;
        this.dismissButton.enabled = false;
        this.dismissButton.visible = false;
      }
    } else {
      this.dismissButton.enabled = false;
      this.dismissButton.visible = false;
      this.summonButton.setIndices(576, 577, 578);
      this.summonButton.enabled = true;
    }
  }

  /** RefreshMode: Automatic or SemiAuto button by the creature's pickup mode. */
  private refreshMode(): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const auto = this.creatures.list[idx].petMode === IntelligentCreaturePickupMode.Automatic;
    this.automaticButton.visible = auto;
    this.semiAutoButton.visible = !auto;
  }

  /** DrawCreatureAnimation: the labels and the CreatureImage loop. */
  private drawCreatureAnimation(): void {
    const idx = this.selectedIndex();
    if (idx < 0) { this.setAnimation(-1, 0, 0); return; }
    const pet = this.creatures.list[idx];
    const rules = pet.CreatureRules;
    const semi = rules.SemiAutoPickupEnabled
      ? `${rules.AutoPickupRange}x${rules.AutoPickupRange} ${rules.AutoPickupEnabled ? "auto/" : ""}${rules.SemiAutoPickupEnabled ? "semi-auto" : ""}${rules.MousePickupEnabled ? ", " : ""}`
      : "";
    // the desktop gates the mouse range on SemiAutoPickupEnabled as well
    const mouse = rules.SemiAutoPickupEnabled ? `${rules.MousePickupRange}x${rules.MousePickupRange} mouse` : "";
    this.creatureName.text = pet.CustomName;
    this.creatureInfo[0].text = `Can pickup items (${semi}${mouse}).`; // ClientTextKeys.CanPickupItems
    this.creatureInfo[1].text = rules.CanProduceBlackStone ? "Can produce BlackStones." : "";
    this.creatureInfo[2].text = rules.CanProduceBlackStone ? "Can produce Pearls, used to buy Creature items." : "";
    const expireTicks = BigInt.asUintN(62, BigInt(pet.Expire));
    this.creatureDeadline.text = expireTicks === 0n // DateTime.MinValue
      ? "Expire: Never"
      : `Expire: ${printTimeSpan((dateFromBinary(BigInt(pet.Expire)).getTime() - Date.now()) / 1000)}`;
    const f = creatureFrames(pet.PetType);
    if (this.animSwitched) this.setAnimation(f.exIdx, f.exCount, f.exDelay);
    else this.setAnimation(f.defIdx, f.defCount, f.defDelay);
  }

  /** CreatureImage (MirAnimatedControl Prguse2, Loop, UseOffSet, at (50, 110)) with Index / AnimationCount / AnimationDelay;
   *  rebuilt when the loop changes, since MirAnimated's count and delay are fixed at construction. */
  private setAnimation(start: number, count: number, delay: number): void {
    const key = `${start}/${count}/${delay}`;
    if (key === this.animKey) return;
    this.animKey = key;
    this.creatureImage?.dispose();
    this.creatureImage = null;
    if (start < 0) return;
    const img = new MirAnimated({ parent: this.imageHolder, library: Lib.Prguse2, index: start, animationCount: count, animationDelay: delay, loop: true,
      animated: true, useOffset: true, notControl: true, location: [50, 110] });
    img.afterAnimation = () => this.loopFinished();
    this.creatureImage = img;
  }

  /** DrawCreatureAnimation's AnimExFinished: switch to the extra loop when due, or back from it, 8 s apart. */
  private loopFinished(): void {
    const t = this.now();
    if (!this.animSwitched && this.switchAnimTime <= t) { this.animSwitched = true; this.switchAnimTime = t + 8000; }
    else if (this.animSwitched) { this.animSwitched = false; this.switchAnimTime = t + 8000; }
    else return;
    this.drawCreatureAnimation();
  }

  /** BeforeAfterDraw + FullnessForeGround_AfterDraw + BlackStoneImageFG_AfterDraw. */
  private refreshBars(): void {
    const idx = this.selectedIndex();
    const has = idx >= 0;
    this.fullnessFG.visible = has;
    this.fullnessMin.visible = has;
    this.fullnessNow.visible = has;
    if (!has) { this.blackStoneFGPic.size = [0, this.blackStoneSize[1]]; return; }
    const pet = this.creatures.list[idx];
    const [fw, fh] = this.fullnessSize;
    let percent = Math.min(1, pet.Fullness / 10000);
    if (percent <= 0) {
      this.fullnessFGPic.size = [0, fh];
      this.fullnessNow.location = [179, 143];
    } else {
      const w = Math.trunc(fw * percent);
      this.fullnessFGPic.size = [w, fh];
      this.fullnessNow.location = [185 + w - 8, 143];
      this.fullnessMin.location = [185 + Math.trunc(fw * (pet.CreatureRules.MinimalFullness / 10000)) - 8, 118];
    }
    const [bw, bh] = this.blackStoneSize;
    percent = Math.min(1, Number(pet.BlackstoneTime) / BLACKSTONE_PRODUCE_TIME);
    this.blackStoneFGPic.size = [percent > 0 ? Math.trunc(bw * percent) : 0, bh];
    this.refreshHover();
  }

  /** The hover label text and place (Control_MouseEnter, refreshed by the AfterDraws). */
  private refreshHover(): void {
    const idx = this.selectedIndex();
    if (!this.hoverTarget || idx < 0) return;
    const pet = this.creatures.list[idx];
    const label = this.hoverLabel;
    label.visible = true;
    const blackstone = Number(pet.BlackstoneTime);
    switch (this.hoverTarget) {
      case "min":
        label.text = `Needed ${pet.CreatureRules.MinimalFullness}`; // ClientTextKeys.NeededBy
        label.size = [150, 15];
        label.location = [this.fullnessMin.location[0] + 8 - 75, 129 - 18];
        break;
      case "fullness":
        label.text = `${pet.Fullness} / 10000`;
        label.size = this.fullnessSize;
        label.location = [185, 129 - 2];
        break;
      case "blackstone":
        // MouseEnter shows the time left; AfterDraw (only while some time has passed) shows NoProduction without production
        label.text = blackstone > 0 && !pet.CreatureRules.CanProduceBlackStone ? "No Production." : printTimeSpan(BLACKSTONE_PRODUCE_TIME - blackstone);
        label.size = this.blackStoneBGSize;
        label.location = [215 + 5, 348 - 2];
        break;
    }
  }

  // — Buttons (IntelligentCreatureDialog.ButtonClick) —

  private sendUpdate(creature: ClientIntelligentCreature, summon = false, unsummon = false, release = false): void {
    this.send(ClientPackets.UpdateIntelligentCreature({ Creature: creature, SummonMe: summon, UnSummonMe: unsummon, ReleaseMe: release }));
  }

  /** CreatureRenameButton: MirInputBox, CreatureNameReg (3-15 letters / digits), the button hides once used. */
  private rename(): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const creature = this.creatures.list[idx];
    inputBox(this.stage, "Please enter a new name for the creature.", creature.CustomName, (value) => {
      if (!CREATURE_NAME.test(value)) {
        messageBox(this.stage, "Creature name must be between 3 and 15 characters.");
        return false; // the desktop keeps the input box open
      }
      this.refresh();
      creature.CustomName = value;
      this.sendUpdate(creature);
    });
    this.renameButton.visible = false;
  }

  /** SummonButton / DismissButton. */
  private summon(on: boolean): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    this.summonButton.enabled = !on;
    this.dismissButton.enabled = on;
    this.dismissButton.visible = on;
    this.refresh();
    this.sendUpdate(this.creatures.list[idx], on, !on, false);
  }

  /** ReleaseButton: the creature's name typed again (case-insensitive), then ReleaseMe; the dialog clears and hides. */
  private release(): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const creature = this.creatures.list[idx];
    inputBox(this.stage, "Please enter the creature's name for verification.", "", (value) => {
      if (value.toLowerCase() !== creature.CustomName.toLowerCase()) {
        this.chat("Verification Failed!!", ChatType.System);
        return;
      }
      for (const b of this.buttons) this.clearButton(b);
      this.hide();
      this.sendUpdate(creature, false, false, true);
    });
  }

  /** SemiAutoModeButton / AutomaticModeButton: switch the pickup mode when the creature's rules allow the other one. */
  private switchMode(): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const creature = this.creatures.list[idx];
    if (creature.petMode === IntelligentCreaturePickupMode.Automatic) {
      if (!creature.CreatureRules.SemiAutoPickupEnabled) return;
      creature.petMode = IntelligentCreaturePickupMode.SemiAutomatic;
    } else {
      if (!creature.CreatureRules.AutoPickupEnabled) return;
      creature.petMode = IntelligentCreaturePickupMode.Automatic;
    }
    this.refresh();
    this.sendUpdate(creature);
  }

  // — Options (IntelligentCreatureOptionsDialog + IntelligentCreatureOptionsGradeDialog) —

  /** OptionsMenuButton: Show(filter) + Show(PickupGrade). Edits a copy, so Cancel leaves the creature's filter as it was. */
  private showOptions(): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const filter = this.creatures.list[idx].Filter;
    this.followDialog();
    if (!this.optionsWin.visible) {
      this.optionFilter = { ...filter };
      this.optionsWin.show();
      this.refreshFilter();
    }
    if (!this.gradeWin.visible) {
      this.gradeType = filter.PickupGrade;
      this.gradeWin.show();
      this.refreshGrade();
    }
  }

  /** IntelligentCreatureOptionsDialog.RefreshFilter. */
  private refreshFilter(): void {
    const flags = itemFilterFlags(this.optionFilter);
    for (let i = 0; i < this.optionChecks.length; i++) this.optionChecks[i].checked = flags[i];
  }

  /** IntelligentCreatureOptionsGradeDialog Next / Prev (clamped to All .. Heroic). */
  private stepGrade(d: number): void {
    this.gradeType = Math.max(0, Math.min(GRADE_MAX, this.gradeType + d));
    this.refreshGrade();
  }

  /** RefreshGradeFilter: the label and its GradeNameColor. */
  private refreshGrade(): void {
    this.gradeLabel.text = GRADE_NAMES[this.gradeType] ?? "All";
    this.gradeLabel.foreColour = GRADE_COLOURS[this.gradeType] ?? "#ffffff";
  }

  /** OptionsSaveButton: PickupGrade from the grade dialog, IntelligentCreatureDialog.SaveItemFilter. */
  private saveOptions(): void {
    this.optionFilter.PickupGrade = this.gradeType;
    this.gradeWin.hide();
    this.optionsWin.hide();
    const idx = this.selectedIndex();
    if (idx < 0) return;
    const creature = this.creatures.list[idx];
    creature.Filter = this.optionFilter;
    this.optionFilter = defaultItemFilter();
    this.sendUpdate(creature);
  }

  /** OptionsCancelButton: reset the dialogs' state and close them. */
  private cancelOptions(): void {
    this.optionFilter = defaultItemFilter();
    this.gradeType = ItemGrade.None;
    this.refreshGrade();
    this.gradeWin.hide();
    this.refreshFilter();
    this.optionsWin.hide();
  }
}
