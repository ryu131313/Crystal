// Intelligent creatures window: IntelligentCreatureDialog + CreatureButton + IntelligentCreatureOptionsDialog +
// IntelligentCreatureOptionsGradeDialog (Client/MirScenes/Dialogs/IntelligentCreatureDialogs.cs), and the dialog side of
// GameScene.NewIntelligentCreature / UpdateIntelligentCreatureList / IntelligentCreatureEnableRename. The model is
// src/game/creatures.ts. E (KeybindOptions.Creature) or the HUD Creatures button toggles it; X (CreaturePickup) and
// Alt+A (CreatureAutoPickup) send C.IntelligentCreaturePickup for the cell under the mouse (200 ms throttle).
// Showing the window asks the server for live updates (C.RequestIntelligentCreatureUpdates true), hiding stops them.
// Images: creature buttons are Prguse2[Icon], the picture is the CreatureButton.SetCreatureFrames Prguse2 animation, the
// pearl Prguse2 427. Not ported: the dialog artwork (Title 468 / 469, Prguse 237), the Help button, the food-buff label
// (invisible on the desktop too).

import type { ServerPacket, ClientIntelligentCreature, IntelligentCreatureItemFilter } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, IntelligentCreaturePickupMode, ItemGrade } from "../protocol/enums.generated.ts";
import {
  BLACKSTONE_PRODUCE_TIME, CREATURE_NAME, Creatures, creatureFrames, defaultItemFilter, itemFilterFlags, setItemFilter,
} from "../game/creatures.ts";
import { dateFromBinary, printTimeSpan } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { libIcon } from "./buffBar.ts";
import { inputBox, messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const BUTTONS = 10; // IntelligentCreatureDialog.CreatureButtons

/** IntelligentCreatureOptionsDialog.OptionNames (ClientTextKeys.AllItems .. Others). */
const OPTION_NAMES = ["All Items", "Gold", "Weapons", "Armours", "Helmets", "Boots", "Belts", "Jewelry", "Others"];

/** The grade label by ItemGrade value (IntelligentCreatureOptionsGradeDialog.GradeStrings; see the note in gradeName). */
const GRADE_NAMES: Record<number, string> = {
  [ItemGrade.None]: "All", [ItemGrade.Common]: "Common", [ItemGrade.Rare]: "Rare", [ItemGrade.Legendary]: "Legendary",
  [ItemGrade.Mythical]: "Mythical", [ItemGrade.Heroic]: "Heroic",
};
/** IntelligentCreatureOptionsGradeDialog.GradeNameColor. */
const GRADE_COLOURS: Record<number, string> = {
  [ItemGrade.Common]: "yellow", [ItemGrade.Rare]: "deepskyblue", [ItemGrade.Legendary]: "darkorange", [ItemGrade.Mythical]: "plum", [ItemGrade.Heroic]: "red",
};
const GRADE_MAX = ItemGrade.Heroic;

/** Sets an <img> to a library image (async; a later call wins). */
function setLibImage(img: HTMLImageElement, lib: string, index: number): void {
  const key = `${lib}#${index}`;
  if (img.dataset.key === key) return;
  img.dataset.key = key;
  img.removeAttribute("src");
  void libIcon(lib, index).then((url) => { if (url && img.dataset.key === key) img.src = url; });
}

interface CreatureButtonView { root: HTMLElement; icon: HTMLImageElement; name: HTMLElement; petType: number; selected: boolean }

export class CreatureWindow {
  readonly hudButton: HTMLButtonElement;
  private readonly stage: HTMLElement;
  private readonly creatures: Creatures;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly mouseCell: () => { X: number; Y: number } | null;
  private readonly now: () => number;

  private readonly root = el("div", { class: "window creature-window", hidden: true });
  private readonly picture = el("img", { class: "creature-picture", alt: "" });
  private readonly nameLabel = el("span", { class: "creature-name" });
  private readonly renameButton = el("button", { class: "creature-rename", hidden: true, title: "Rename" }, "Rename");
  private readonly deadline = el("div", { class: "creature-deadline" });
  private readonly fullnessFill = el("div", { class: "creature-bar-fill" });
  private readonly fullnessMin = el("div", { class: "creature-bar-min" });
  private readonly fullnessText = el("span", { class: "creature-bar-text" });
  private readonly fullnessBar: HTMLElement;
  private readonly info = [el("div", { class: "creature-info" }), el("div", { class: "creature-info" }), el("div", { class: "creature-info" })];
  private readonly summonButton = el("button", {}, "Summon");
  private readonly dismissButton = el("button", { hidden: true }, "Dismiss");
  private readonly releaseButton = el("button", {}, "Release");
  private readonly optionsButton = el("button", {}, "Options");
  private readonly modeButton = el("button", { class: "creature-mode" });
  private readonly buttons: CreatureButtonView[] = [];
  private readonly pearls = el("span", { class: "creature-pearls" }, "0");
  private readonly blackstoneFill = el("div", { class: "creature-bar-fill blackstone" });
  private readonly blackstoneText = el("span", { class: "creature-bar-text" });
  private readonly blackstoneBar: HTMLElement;

  // IntelligentCreatureOptionsDialog + IntelligentCreatureOptionsGradeDialog (one panel beside the window)
  private readonly options = el("div", { class: "creature-options", hidden: true });
  private readonly optionChecks: HTMLInputElement[] = [];
  private readonly gradeLabel = el("span", { class: "creature-grade" });
  private optionFilter: IntelligentCreatureItemFilter = defaultItemFilter();
  private gradeType: number = ItemGrade.None;

  /** IntelligentCreatureDialog.SelectedCreatureSlot (a button index; the creature is the one whose SlotIndex matches). */
  private selectedSlot = -1;
  private showing = false;
  // Picture animation (DrawCreatureAnimation): default loop, then the extra loop once SwitchAnimTime has passed.
  private switchAnimTime = 0;
  private animSwitched = false;
  private animStart = 0;
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

    this.fullnessBar = el("div", { class: "creature-bar" }, this.fullnessFill, this.fullnessMin, this.fullnessText);
    this.blackstoneBar = el("div", { class: "creature-bar" }, this.blackstoneFill, this.blackstoneText);
    this.renameButton.onclick = () => this.rename();
    this.summonButton.onclick = () => this.summon(true);
    this.dismissButton.onclick = () => this.summon(false);
    this.releaseButton.onclick = () => this.release();
    this.optionsButton.onclick = () => this.showOptions();
    this.modeButton.onclick = () => this.switchMode();

    const grid = el("div", { class: "creature-buttons" });
    for (let i = 0; i < BUTTONS; i++) {
      const icon = el("img", { alt: "" });
      const name = el("span", { class: "creature-button-name" });
      const root = el("div", { class: "creature-button", hidden: true }, icon, name);
      root.onclick = () => { this.selectButton(i); this.refresh(); }; // CreatureButton.PetButtonClick
      grid.append(root);
      this.buttons.push({ root, icon, name, petType: -1, selected: false });
    }

    for (let i = 0; i < OPTION_NAMES.length; i++) {
      const box = el("input", { type: "checkbox" });
      box.onclick = () => { setItemFilter(this.optionFilter, i); this.refreshFilter(); }; // CheckBoxClick
      this.optionChecks.push(box);
    }
    this.options.append(
      el("div", { class: "creature-grade-row" },
        el("button", { onclick: () => this.stepGrade(-1), title: "Previous grade" }, "‹"), this.gradeLabel,
        el("button", { onclick: () => this.stepGrade(1), title: "Next grade" }, "›")),
      ...this.optionChecks.map((box, i) => el("label", { class: "creature-option" }, box, OPTION_NAMES[i])),
      el("div", { class: "row" },
        el("button", { class: "primary", onclick: () => this.saveOptions() }, "Save"),
        el("button", { onclick: () => this.cancelOptions() }, "Cancel")),
    );

    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Creatures"), el("button", { class: "window-close", onclick: () => this.hide() }, "×")),
      el("div", { class: "creature-top" },
        this.picture,
        el("div", { class: "creature-details" },
          el("div", { class: "creature-name-row" }, this.nameLabel, this.renameButton),
          this.deadline,
          el("div", { class: "creature-bar-row" }, el("span", {}, "Fullness"), this.fullnessBar),
          ...this.info)),
      el("div", { class: "row creature-actions" }, this.summonButton, this.dismissButton, this.releaseButton, this.optionsButton, this.modeButton),
      grid,
      el("div", { class: "creature-bottom" },
        el("span", { class: "creature-pearl-row", title: "Pearls" }, this.pearlIcon(), this.pearls),
        el("div", { class: "creature-bar-row" }, el("span", {}, "BlackStone"), this.blackstoneBar)),
      this.options,
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    this.hudButton = el("button", { title: "Creatures (E)", onclick: () => this.toggle() }, "Creatures") as HTMLButtonElement;

    const signal = gameSignal();
    signal.addEventListener("abort", () => this.stopTimer());
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindSettings defaults: Creature = E, CreaturePickup = X without Alt, CreatureAutoPickup = Alt+A without Ctrl
      // (Ctrl+A is the pet mode).
      if (matches(e, KeybindOptions.Creature) && !e.repeat) this.toggle();
      else if (matches(e, KeybindOptions.CreaturePickup)) this.pickup(true);
      else if (matches(e, KeybindOptions.CreatureAutoPickup)) { e.preventDefault(); this.pickup(false); }
    }, { signal });
  }

  private pearlIcon(): HTMLImageElement {
    const img = el("img", { class: "creature-pearl", alt: "" });
    setLibImage(img, "Prguse2", 427);
    return img;
  }

  get visible(): boolean {
    return !this.root.hidden;
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
        this.renameButton.hidden = false;
        if (this.visible) this.refresh();
        break;
    }
    return true;
  }

  /** IntelligentCreatureDialog.Show. */
  show(): void {
    if (this.visible) return;
    if (this.creatures.list.length === 0) { messageBox(this.stage, "You do not own any creatures."); return; }
    if (!this.buttons.some((b) => b.selected)) this.selectButton(0);
    this.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: true }));
    this.root.hidden = false;
    this.showing = true;
    this.switchAnimTime = this.now() + 8000;
    this.animSwitched = false;
    this.animStart = this.now();
    this.refresh();
    this.stopTimer();
    this.timer = window.setInterval(() => this.tick(), 50);
  }

  /** IntelligentCreatureDialog.Hide (also hides the options / grade dialogs). */
  hide(): void {
    if (!this.visible) return;
    this.options.hidden = true;
    this.animSwitched = false;
    this.root.hidden = true;
    this.stopTimer();
    this.send(ClientPackets.RequestIntelligentCreatureUpdates({ Update: false }));
  }

  private stopTimer(): void {
    if (this.timer) window.clearInterval(this.timer);
    this.timer = 0;
  }

  /** The desktop redraws every frame (BeforeDraw -> RefreshDialog): the expiry / blackstone texts tick, the picture animates. */
  private tick(): void {
    if (!this.visible) { this.stopTimer(); return; }
    const t = this.now();
    if (t - this.lastRefresh >= 250) this.refresh();
    else this.animate();
  }

  // — CreatureButton —

  /** CreatureButton.SelectButton: select this button only, restart the animation timer. */
  private selectButton(i: number): void {
    const b = this.buttons[i];
    if (b.selected) return;
    for (const o of this.buttons) { o.selected = false; o.root.classList.remove("selected"); }
    b.selected = true;
    b.root.classList.add("selected");
    this.selectedSlot = i;
    this.switchAnimTime = this.now() + 10000;
    this.animSwitched = false;
    this.animStart = this.now();
  }

  /** CreatureButton.Clear. */
  private clearButton(b: CreatureButtonView): void {
    b.petType = -1;
    b.root.hidden = true;
    b.selected = false;
    b.root.classList.remove("selected");
  }

  // — Refresh (IntelligentCreatureDialog.RefreshDialog) —

  /** The list index of the selected creature, or -1 (BeforeAfterDraw / GetCreatureFromSlot). */
  private selectedIndex(): number {
    return this.selectedSlot < 0 ? -1 : this.creatures.fromSlot(this.selectedSlot);
  }

  private refresh(): void {
    this.lastRefresh = this.now();
    this.refreshInfo();
    this.refreshUI();
    this.refreshMode();
    this.refreshLabels();
    this.animate();
  }

  /** RefreshInfo: pearls, the buttons, and (on Show) the summoned creature's button selected. */
  private refreshInfo(): void {
    const c = this.creatures;
    this.pearls.textContent = String(c.pearlCount);
    let selectedButton = -1;
    for (let i = 0; i < BUTTONS; i++) {
      const b = this.buttons[i];
      if (i >= c.list.length) { this.clearButton(b); continue; }
      const pet = c.list[i];
      b.root.hidden = false;
      b.petType = pet.PetType;
      setLibImage(b.icon, "Prguse2", pet.Icon);
      b.name.textContent = pet.CustomName;
      b.root.title = pet.CustomName;
      if (this.showing && c.summoned && pet.PetType === c.summonedType) selectedButton = i;
    }
    this.showing = false;
    if (selectedButton >= 0) this.selectButton(selectedButton);
  }

  /** RefreshUI: which buttons are usable for the selection and the summon state. */
  private refreshUI(): void {
    const idx = this.selectedIndex();
    const has = idx >= 0;
    for (const e of [this.picture, this.nameLabel, this.deadline, ...this.info]) e.hidden = !has;
    if (!has) {
      this.renameButton.disabled = true;
      this.summonButton.disabled = true;
      this.summonButton.hidden = false;
      this.dismissButton.hidden = true;
      this.releaseButton.disabled = true;
      this.modeButton.disabled = true;
      this.optionsButton.disabled = true;
      return;
    }
    this.renameButton.disabled = false;
    this.releaseButton.disabled = false;
    this.optionsButton.disabled = false;
    this.modeButton.disabled = false;
    const c = this.creatures;
    if (c.summoned) {
      if (c.list[idx].PetType === c.summonedType) {
        this.dismissButton.hidden = false; // drawn over the Summon button on the desktop
        this.summonButton.hidden = true;
        this.releaseButton.disabled = true;
      } else {
        // Summon image 593 ("another creature is summoned"), disabled
        this.summonButton.hidden = false;
        this.summonButton.disabled = true;
        this.summonButton.title = "Another creature is summoned.";
        this.dismissButton.hidden = true;
      }
    } else {
      this.dismissButton.hidden = true;
      this.summonButton.hidden = false;
      this.summonButton.disabled = false;
      this.summonButton.title = "";
    }
  }

  /** RefreshMode: the pickup-mode button shows the current mode. */
  private refreshMode(): void {
    const idx = this.selectedIndex();
    if (idx < 0) { this.modeButton.textContent = "Mode"; return; }
    const auto = this.creatures.list[idx].petMode === IntelligentCreaturePickupMode.Automatic;
    this.modeButton.textContent = auto ? "Automatic" : "Semi-auto";
    this.modeButton.title = auto ? "Pickup mode: automatic (click for semi-automatic)" : "Pickup mode: semi-automatic (click for automatic)";
  }

  /** DrawCreatureAnimation (texts) + FullnessForeGround_AfterDraw + BlackStoneImageFG_AfterDraw + the hover labels. */
  private refreshLabels(): void {
    const idx = this.selectedIndex();
    if (idx < 0) {
      this.fullnessFill.style.width = "0%";
      this.fullnessMin.hidden = true;
      this.fullnessText.textContent = "";
      this.blackstoneFill.style.width = "0%";
      this.blackstoneText.textContent = "";
      return;
    }
    const pet = this.creatures.list[idx];
    const rules = pet.CreatureRules;
    const semi = rules.SemiAutoPickupEnabled
      ? `${rules.AutoPickupRange}x${rules.AutoPickupRange} ${rules.AutoPickupEnabled ? "auto/" : ""}${rules.SemiAutoPickupEnabled ? "semi-auto" : ""}${rules.MousePickupEnabled ? ", " : ""}`
      : "";
    // the desktop gates the mouse range on SemiAutoPickupEnabled as well
    const mouse = rules.SemiAutoPickupEnabled ? `${rules.MousePickupRange}x${rules.MousePickupRange} mouse` : "";
    this.nameLabel.textContent = pet.CustomName;
    this.info[0].textContent = `Can pickup items (${semi}${mouse}).`;
    this.info[1].textContent = rules.CanProduceBlackStone ? "Can produce BlackStones." : "";
    this.info[2].textContent = rules.CanProduceBlackStone ? "Can produce Pearls, used to buy Creature items." : "";
    const expireTicks = BigInt.asUintN(62, BigInt(pet.Expire));
    this.deadline.textContent = expireTicks === 0n // DateTime.MinValue
      ? "Expire: Never"
      : `Expire: ${printTimeSpan((dateFromBinary(BigInt(pet.Expire)).getTime() - Date.now()) / 1000)}`;

    let percent = Math.min(1, pet.Fullness / 10000);
    this.fullnessFill.style.width = `${Math.max(0, percent) * 100}%`;
    this.fullnessText.textContent = `${pet.Fullness} / 10000`;
    this.fullnessMin.hidden = false;
    this.fullnessMin.style.left = `${Math.min(1, Math.max(0, rules.MinimalFullness / 10000)) * 100}%`;
    this.fullnessMin.title = `Needed ${rules.MinimalFullness}`;

    const blackstone = Number(pet.BlackstoneTime);
    percent = Math.min(1, blackstone / BLACKSTONE_PRODUCE_TIME);
    this.blackstoneFill.style.width = `${Math.max(0, percent) * 100}%`;
    this.blackstoneText.textContent = rules.CanProduceBlackStone ? printTimeSpan(BLACKSTONE_PRODUCE_TIME - blackstone) : "No Production.";
  }

  /** DrawCreatureAnimation (frames): the default loop; after SwitchAnimTime the extra loop plays once, then 8 s again. */
  private animate(): void {
    const idx = this.selectedIndex();
    if (idx < 0) { this.picture.hidden = true; return; }
    const f = creatureFrames(this.creatures.list[idx].PetType);
    const t = this.now();
    const count = this.animSwitched ? f.exCount : f.defCount;
    const delay = Math.max(1, this.animSwitched ? f.exDelay : f.defDelay);
    const start = this.animSwitched ? f.exIdx : f.defIdx;
    let frame = Math.floor((t - this.animStart) / delay);
    if (frame >= count) {
      // a loop finished: switch to the extra loop when due, or back from it
      if (!this.animSwitched && this.switchAnimTime <= t) { this.animSwitched = true; this.switchAnimTime = t + 8000; }
      else if (this.animSwitched) { this.animSwitched = false; this.switchAnimTime = t + 8000; }
      this.animStart = t;
      frame = 0;
      setLibImage(this.picture, "Prguse2", this.animSwitched ? f.exIdx : f.defIdx);
      return;
    }
    setLibImage(this.picture, "Prguse2", start + frame);
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
    this.renameButton.hidden = true;
  }

  /** SummonButton / DismissButton. */
  private summon(on: boolean): void {
    const idx = this.selectedIndex();
    if (idx < 0) return;
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
    if (idx < 0 || !this.options.hidden) return;
    const filter = this.creatures.list[idx].Filter;
    this.optionFilter = { ...filter };
    this.gradeType = filter.PickupGrade;
    this.options.hidden = false;
    this.refreshFilter();
    this.refreshGrade();
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
    this.gradeLabel.textContent = GRADE_NAMES[this.gradeType] ?? "All";
    this.gradeLabel.style.color = GRADE_COLOURS[this.gradeType] ?? "white";
  }

  /** OptionsSaveButton: PickupGrade from the grade dialog, IntelligentCreatureDialog.SaveItemFilter. */
  private saveOptions(): void {
    this.optionFilter.PickupGrade = this.gradeType;
    this.options.hidden = true;
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
    this.options.hidden = true;
  }
}
