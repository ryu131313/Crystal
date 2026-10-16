// The bottom HUD bar: port of MainDialog (Client/MirScenes/Dialogs/MainDialogs.cs) with the desktop artwork:
// Prguse 1 (2 above 1024) bottom bar, the HP / MP orbs (Prguse 4; HPOnly = Warrior below level 26 uses the one-orb
// Prguse 6), the HP / MP labels (Settings.HPView) or the top / bottom numbers, level, name, experience bar (Prguse 8) with
// its percentage, gold, bag weight bar (Prguse 76, UI_32bit 473 / 472 when fuller) with free weight / free slots, the
// Inventory / Character / Skills / Quests / Options / Menu / Game Shop buttons, and the attack / pet / skill mode labels
// (Settings.ModeView, placed under the minimap by MiniMapDialog.Process). The hero info panel, hero menu and summon
// buttons are MainDialog children too; heroPanels.ts creates them with `main` as the parent.

import { MirWindow, MirButton, MirLabel, MirImage, Lib } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { MirClass } from "../protocol/enums.generated.ts";
import { KeybindOptions, keyLabel, type KeybindOption } from "../game/keybinds.ts";
import { gameSettings } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { pixelDetect, Section } from "./hudCommon.ts";
import { AMODE_TEXT, PMODE_TEXT } from "../game/modeText.ts";

/** ClientTextKeys.AttackMode_* / PetMode_* (English.json); defined in src/game/modeText.ts since phase 3a (game.ts reads them too). */
export { AMODE_TEXT, PMODE_TEXT };

/** GameLanguage "Name ({0})" hints with the current key (CMain.InputKeys.GetKey). */
export function keyHint(format: string, opt: KeybindOption): string {
  return format.replace("{0}", keyLabel(opt));
}

export interface MainDialogActions {
  inventory(): void;
  character(): void;
  skills(): void;
  quests(): void;
  options(): void;
  menu(): void;
  gameShop(): void;
  /** GoldLabel click: GameScene.PickedUpGold (the next map click asks how much to drop). */
  gold(): void;
}

/** What MainDialog.Process reads each frame. */
export interface MainState {
  name: string; cls: number; level: number; hp: number; mp: number; maxHp: number; maxMp: number;
  experience: bigint; maxExperience: bigint; gold: number; freeSlots: number; bagWeight: number; maxBagWeight: number;
  aMode: number; pMode: number;
  /** MiniMapDialog.Size.Height (the mode labels sit under it). */
  miniMapHeight: number;
}

export class MainDialog {
  readonly win: MirWindow;
  private readonly screenW: number;
  private readonly screenH: number;
  private readonly hpOrb: Section;
  private readonly mpOrb: Section;
  private readonly healthOrb: MirImage;
  private readonly healthLabel: MirLabel;
  private readonly manaLabel: MirLabel;
  private readonly topLabel: MirLabel;
  private readonly bottomLabel: MirLabel;
  private readonly levelLabel: MirLabel;
  private readonly characterName: MirLabel;
  private readonly expBar: Section;
  private readonly expLabel: MirLabel;
  private readonly goldLabel: MirLabel;
  private readonly weightBar: Section;
  private readonly weightLabel: MirLabel;
  private readonly spaceLabel: MirLabel;
  private readonly aModeLabel: MirLabel;
  private readonly pModeLabel: MirLabel;
  private readonly sModeLabel: MirLabel;

  constructor(screen: readonly [number, number], act: MainDialogActions) {
    const [SW, SH] = screen;
    this.screenW = SW;
    this.screenH = SH;
    // MainDialog ctor: Index = Resolution 1024 ? 1 : 2 (both 1024 x 152), centred at the bottom
    const W = 1024, H = 152;
    this.win = new MirWindow({ library: Lib.Prguse, index: SW === 1024 ? 1 : 2, location: [Math.floor(SW / 2 - W / 2), SH - H], sort: false, visible: true });
    this.win.el.classList.add("mir-hud");
    pixelDetect(this.win);
    // LeftCap / RightCap (Resolution > 1024)
    new MirImage({ parent: this.win, library: Lib.Prguse, index: 12, location: [-67, H - 96], visible: SW > 1024 });
    new MirImage({ parent: this.win, library: Lib.Prguse, index: 13, location: [1024, H - 104], visible: SW > 1024 });

    const button = (index: number, location: [number, number], hint: string, onClick: () => void, sound: number = SoundList.ButtonA) =>
      new MirButton({ parent: this.win, library: Lib.Prguse, index, hoverIndex: index + 1, pressedIndex: index + 2, location, sound, hint, onClick });
    button(1903, [W - 96, 76], keyHint("Inventory ({0})", KeybindOptions.Inventory), act.inventory);
    button(1900, [W - 119, 76], keyHint("Character ({0})", KeybindOptions.Equipment), act.character);
    button(1906, [W - 73, 76], keyHint("Skills ({0})", KeybindOptions.Skills), act.skills);
    button(1909, [W - 50, 76], keyHint("Quests ({0})", KeybindOptions.Quests), act.quests);
    button(1912, [W - 27, 76], keyHint("Options ({0})", KeybindOptions.Options), act.options);
    button(1960, [W - 55, 35], "Menu", act.menu, SoundList.ButtonC);
    button(826, [W - 105, 35], keyHint("Game Shop ({0})", KeybindOptions.GameShop), act.gameShop, SoundList.ButtonC);

    // HealthOrb (0, 30), NotControl; the orb pictures are drawn by HealthOrb_BeforeDraw
    this.healthOrb = new MirImage({ parent: this.win, location: [0, 30], size: [104, 80], notControl: true });
    this.hpOrb = new Section(this.healthOrb);
    this.mpOrb = new Section(this.healthOrb);
    this.healthLabel = new MirLabel({ parent: this.healthOrb, autoSize: true, location: [0, 27] });
    this.manaLabel = new MirLabel({ parent: this.healthOrb, autoSize: true, location: [0, 42] });
    this.topLabel = new MirLabel({ parent: this.healthOrb, size: [85, 30], align: "center", location: [9, 20] });
    this.bottomLabel = new MirLabel({ parent: this.healthOrb, size: [85, 30], align: "center", location: [9, 50] });
    this.levelLabel = new MirLabel({ parent: this.win, autoSize: true, location: [5, 108] });
    this.characterName = new MirLabel({ parent: this.win, size: [90, 16], center: true, location: [6, 120] });

    // ExperienceBar (9, 143): Prguse 8 (1004 x 8), DrawImage = false, drawn as a section by ExperienceBar_BeforeDraw
    const expControl = new MirImage({ parent: this.win, location: [9, 143], size: [1004, 8], notControl: true });
    this.expBar = new Section(expControl);
    this.expLabel = new MirLabel({ parent: expControl, autoSize: true, notControl: true, location: [1004 / 2 - 20, -10] });

    this.goldLabel = new MirLabel({ parent: this.win, vCenter: true, location: [W - 105, 119], size: [99, 13] });
    this.goldLabel.el.addEventListener("click", (e) => { e.stopPropagation(); act.gold(); });

    const weightControl = new MirImage({ parent: this.win, location: [W - 105, 103], size: [76, 12], notControl: true });
    this.weightBar = new Section(weightControl);
    this.weightLabel = new MirLabel({ parent: this.win, location: [W - 105, 101], size: [40, 14], notControl: true });
    this.spaceLabel = new MirLabel({ parent: this.win, location: [W - 30, 101], size: [26, 14], notControl: true });

    this.aModeLabel = new MirLabel({ parent: this.win, autoSize: true, foreColour: "#ffff00", location: [899, -448] });
    this.pModeLabel = new MirLabel({ parent: this.win, autoSize: true, foreColour: "#ffa500", location: [230, 125] });
    this.sModeLabel = new MirLabel({ parent: this.win, autoSize: true, foreColour: "#32cd32", location: [899, -463] });
    for (const l of [this.aModeLabel, this.pModeLabel, this.sModeLabel]) l.notControl = true;

    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  /** The dialog's top-left on the stage (MainDialog.Location). */
  get location(): readonly [number, number] { return this.win.location; }

  /** MainDialog.Process + the BeforeDraw handlers. */
  update(s: MainState): void {
    const hpOnly = s.cls === MirClass.Warrior && s.level < 26; // HPOnly
    const modeView = gameSettings.ModeView;
    this.aModeLabel.visible = this.pModeLabel.visible = this.sModeLabel.visible = modeView;
    if (modeView) {
      this.aModeLabel.text = AMODE_TEXT[s.aMode] ?? "";
      this.pModeLabel.text = PMODE_TEXT[s.pMode] ?? "";
      this.sModeLabel.text = gameSettings.SkillMode ? "[Skill Mode: ~]" : "[Skill Mode: Ctrl]";
      // MiniMapDialog.Process places them under the minimap
      const mx = this.win.location[0];
      const x = this.screenW - 126 - 3 - mx;
      this.sModeLabel.location = [x, s.miniMapHeight + 150 - this.screenH];
      this.aModeLabel.location = [x, s.miniMapHeight + 165 - this.screenH];
      this.pModeLabel.location = [x, s.miniMapHeight + 180 - this.screenH];
    }
    const before = this.healthLabel.text + "|" + this.manaLabel.text;
    if (gameSettings.HPView) {
      this.healthLabel.text = `HP ${s.hp}/${s.maxHp}`;
      this.manaLabel.text = hpOnly ? "" : `MP ${s.mp}/${s.maxMp} `;
      this.topLabel.text = "";
      this.bottomLabel.text = "";
    } else {
      if (hpOnly) {
        this.topLabel.text = `${s.hp}\n--`;
        this.bottomLabel.text = `${s.maxHp}`;
      } else {
        this.topLabel.text = ` ${s.hp}    ${s.mp} \n---------------`;
        this.bottomLabel.text = ` ${s.maxHp}    ${s.maxMp} `;
      }
      this.healthLabel.text = "";
      this.manaLabel.text = "";
    }
    // Label_SizeChanged: centred on x = 50
    if (before !== this.healthLabel.text + "|" + this.manaLabel.text) for (const l of [this.healthLabel, this.manaLabel]) {
      const w = l.el.offsetWidth;
      l.location = [50 - Math.floor(w / 2), l.location[1]];
    }
    this.levelLabel.text = String(s.level);
    const ratio = s.maxExperience > 0n ? Number(s.experience) / Number(s.maxExperience) : 0;
    this.expLabel.text = `${+(ratio * 100).toFixed(2)}%`; // {0:#0.##%}
    this.goldLabel.text = s.gold.toLocaleString("en-US");
    this.characterName.text = s.name;
    this.spaceLabel.text = String(s.freeSlots);
    this.weightLabel.text = String(s.maxBagWeight - s.bagWeight);

    // HealthOrb_BeforeDraw: Prguse 4 (6 for HPOnly) cropped from the bottom, 80 px tall
    const orbHeight = (cur: number, max: number) => Math.max(0, Math.min(80, cur === max ? 80 : max > 0 ? Math.trunc((80 * cur) / max) : 0));
    let h = orbHeight(s.hp, s.maxHp);
    this.hpOrb.set(Lib.Prguse, hpOnly ? 6 : 4, 0, 80 - h, hpOnly ? 100 : 50, h);
    this.hpOrb.move(0, 80 - h);
    if (hpOnly) this.mpOrb.set(Lib.Prguse, 4, 0, 0, 0, 0);
    else {
      h = orbHeight(s.mp, s.maxMp);
      this.mpOrb.set(Lib.Prguse, 4, 51, 80 - h, 50, h);
      this.mpOrb.move(51, 80 - h);
    }

    // ExperienceBar_BeforeDraw: (Width - 3) * percent
    const p = Math.min(1, ratio);
    this.expBar.set(Lib.Prguse, 8, 0, 0, p > 0 ? Math.trunc((1004 - 3) * p) : 0, 8);

    // WeightBar_BeforeDraw: Prguse 76 up to 50 %, UI_32bit 473 up to 75 %, else 472; (Width - 2) * percent
    const wp = Math.min(1, s.maxBagWeight > 0 ? s.bagWeight / s.maxBagWeight : 0);
    const [lib, index] = wp <= 0.5 ? [Lib.Prguse, 76] : wp <= 0.75 ? [Lib.UI_32bit, 473] : [Lib.UI_32bit, 472];
    this.weightBar.set(lib, index, 0, 0, wp > 0 ? Math.trunc((76 - 2) * wp) : 0, 12);
  }
}

