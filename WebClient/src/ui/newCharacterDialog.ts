// NewCharacterDialog (Client/MirScenes/Dialogs/NewCharacterDialog.cs): Prguse 73, modal, the class / gender buttons
// (Prguse 2420-2440), the animated ChrSel.Lib preview (+ the Wizard's additive glow layer, index + 560), the name box and
// the class description. OK raises onCreate (SelectScene sends C.NewCharacter); Cancel hides the dialog.

import { MirWindow, MirImage, MirButton, MirLabel, MirTextBox, Lib } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { Globals, Text, Colour, setBorder, chrSelIndex } from "./accountCommon.ts";
import { SoundList } from "../game/audio.ts";
import { MirClass, MirGender } from "../protocol/enums.generated.ts";

// NewCharacterDialog.Reg (kept as the desktop wrote it: alternation binds looser than the anchors)
const REG = new RegExp(`^[A-Za-z0-9]|[\\u4e00-\\u9fa5]{${Globals.MinCharacterNameLength},${Globals.MaxCharacterNameLength}}$`);

const DESCRIPTION: Record<number, string> = {
  [MirClass.Warrior]: Text.WarriorsDescription, [MirClass.Wizard]: Text.WizardsDescription, [MirClass.Taoist]: Text.TaoistsDescription,
  [MirClass.Assassin]: Text.AssassinsDescription, [MirClass.Archer]: Text.ArchersDescription,
};

export class NewCharacterDialog {
  readonly win: MirWindow;
  readonly okButton: MirButton;
  readonly nameTextBox: MirTextBox;
  cls: number = MirClass.Warrior;
  gender: number = MirGender.Male;
  private readonly display: MirAnimated;
  private readonly glow: MirImage;
  private readonly description: MirLabel;
  private readonly classButtons: [number, MirButton][] = [];
  private readonly maleButton: MirButton;
  private readonly femaleButton: MirButton;
  private disposed = false;
  onCreate: (() => void) | null = null;

  constructor(parent: HTMLElement) {
    // NewCharacterDialog ctor
    this.win = new MirWindow({ parent, library: Lib.Prguse, index: 73, location: "center", modal: true, sort: true });
    new MirImage({ parent: this.win, library: Lib.Title, index: 20, location: [206, 11] });
    new MirButton({ parent: this.win, library: Lib.Title, index: 280, hoverIndex: 281, pressedIndex: 282, location: [425, 425], onClick: () => this.hide() });
    this.okButton = new MirButton({ parent: this.win, library: Lib.Title, index: 360, hoverIndex: 361, pressedIndex: 362, location: [160, 425], enabled: false, onClick: () => this.createCharacter() });
    this.nameTextBox = new MirTextBox({ parent: this.win, location: [325, 268], size: [240, 20], maxLength: Globals.MaxCharacterNameLength,
      onEnter: () => { if (this.okButton.enabled) this.okButton.el.click(); }, onChange: () => this.nameChanged() });

    this.display = new MirAnimated({ parent: this.win, library: Lib.ChrSel, index: 20, location: [120, 250], useOffset: true, animated: true, animationCount: 16, animationDelay: 250 });
    // AfterDraw: Libraries.ChrSel.DrawBlend(Index + 560, DisplayLocationWithoutOffSet, White, true) for the Wizard
    this.glow = new MirImage({ parent: this.win, library: Lib.ChrSel, index: 580, location: [120, 250], useOffset: true, notControl: true });
    this.glow.el.style.mixBlendMode = "plus-lighter";
    this.display.onFrame = () => this.updateGlow();

    const classButton = (cls: number, index: number, hover: number, pressed: number, x: number) => {
      const b = new MirButton({ parent: this.win, library: Lib.Prguse, index, hoverIndex: hover, pressedIndex: pressed, location: [x, 296], sound: SoundList.ButtonA,
        onClick: () => { this.cls = cls; this.updateInterface(); } });
      this.classButtons.push([cls, b]);
    };
    classButton(MirClass.Warrior, 2427, 2427, 2428, 323);
    classButton(MirClass.Wizard, 2429, 2430, 2431, 373);
    classButton(MirClass.Taoist, 2432, 2433, 2434, 423);
    classButton(MirClass.Assassin, 2435, 2436, 2437, 473);
    classButton(MirClass.Archer, 2438, 2439, 2440, 523);

    this.maleButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 2421, hoverIndex: 2421, pressedIndex: 2422, location: [323, 343], sound: SoundList.ButtonA,
      onClick: () => { this.gender = MirGender.Male; this.updateInterface(); } });
    this.femaleButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 2423, hoverIndex: 2424, pressedIndex: 2425, location: [373, 343], sound: SoundList.ButtonA,
      onClick: () => { this.gender = MirGender.Female; this.updateInterface(); } });

    this.description = new MirLabel({ parent: this.win, location: [279, 70], size: [278, 170], text: Text.WarriorsDescription });
  }

  get isDisposed(): boolean { return this.disposed; }

  /** NewCharacterDialog.Show: Warrior / Male / empty name. */
  show(): void {
    this.win.show();
    this.cls = MirClass.Warrior;
    this.gender = MirGender.Male;
    this.nameTextBox.text = "";
    this.nameChanged();
    this.updateInterface();
    this.nameTextBox.focus();
  }

  hide(): void { this.win.hide(); }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.onCreate = null;
    this.display.dispose();
    this.win.dispose();
  }

  /** CharacterNameTextBox_TextChanged. */
  private nameChanged(): void {
    const text = this.nameTextBox.text;
    if (!text) { this.okButton.enabled = false; setBorder(this.nameTextBox, null); }
    else if (!REG.test(text)) { this.okButton.enabled = false; setBorder(this.nameTextBox, Colour.Red); }
    else { this.okButton.enabled = true; setBorder(this.nameTextBox, Colour.Green); }
  }

  private createCharacter(): void {
    this.okButton.enabled = false;
    this.onCreate?.();
  }

  private updateGlow(): void {
    this.glow.visible = this.cls === MirClass.Wizard;
    this.glow.index = this.display.frame + 560;
  }

  /** NewCharacterDialog.UpdateInterface. */
  private updateInterface(): void {
    this.maleButton.index = this.gender === MirGender.Male ? 2421 : 2420;
    this.femaleButton.index = this.gender === MirGender.Female ? 2424 : 2423;
    const base: Record<number, number> = { [MirClass.Warrior]: 2426, [MirClass.Wizard]: 2429, [MirClass.Taoist]: 2432, [MirClass.Assassin]: 2435, [MirClass.Archer]: 2438 };
    for (const [cls, b] of this.classButtons) b.index = base[cls]! + (cls === this.cls ? 1 : 0);
    this.description.text = DESCRIPTION[this.cls] ?? "";
    this.display.index = chrSelIndex(this.cls, this.gender);
    this.updateGlow();
  }
}
