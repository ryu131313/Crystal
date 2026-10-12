// The hero's skills (Ctrl+S, KeybindOptions.HeroSkills): GameScene.HeroDialog (CharacterDialog with
// MirGridType.HeroEquipment) on its SkillPage, with the desktop artwork: Title 504, the Character / Status / State / Skill
// tabs (Title 500-503, Skill held), the name / guild labels and class image, SkillPage Title 508 at (8, 90) with seven
// MagicButtons (HeroMagic = true: the icon opens AssignKeyPanel(Magic, 17, Shift+F1-F8) with Actor = GameScene.Hero)
// and Back / Next (Prguse 398 / 396). Keys 17-24 cast on Shift+F1-F8 (Magic.useHeroSpell).
// The web hero CharacterDialog (heroCharacterWindow.ts, a CharacterWindow) lists only the user's magics on its skill
// page, so this window draws the hero's skill page: its other tabs open that window on their page, and its Skill tab
// opens this one; the two never show together (one HeroDialog). Escape hides it (GameScene Closeall).

import type { Magic, UserMagic } from "../game/magic.ts";
import type { HeroCharacterWindow } from "./heroCharacterWindow.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, closeButton } from "./mir/controls.ts";
import { assignKeyPanel } from "./magicBar.ts";
import { SKILL_DESCRIPTIONS } from "./skillHints.ts";

/** The hero's two halves of GameScene.HeroDialog (registered by their constructors). */
export const heroDialogs: { character: HeroCharacterWindow | null; skills: HeroMagicWindow | null } = { character: null, skills: null };

/** AssignKeyPanel key strings for the hero (MagicButton.SkillButton.Click with HeroMagic). */
const HERO_KEYS = ["Shift\nF1", "Shift\nF2", "Shift\nF3", "Shift\nF4", "Shift\nF5", "Shift\nF6", "Shift\nF7", "Shift\nF8"];
const PREFIXES = ["", "CTRL", "Shift"]; // MagicButton.Prefixes

const fmt = (text: string, ...args: (string | number)[]) => text.replace(/\{(\d+)\}/g, (_, i: string) => String(args[Number(i)] ?? ""));
/** The skill descriptions' "next level" value (Level1 / Level2 / Level3 by current level). */
const nextLevel = (m: UserMagic) => (m.Level === 0 ? m.Level1 : m.Level === 1 ? m.Level2 : m.Level === 2 ? m.Level3 : 0);

/** MagicButton.SetDelay / ProcessSkillDelay: the cooldown frame of 34, -1 = none. */
function coolDownFrame(m: UserMagic, totalFrames: number, now: number): number {
  if (!Number.isFinite(m.castTime) || m.delay <= 0) return -1;
  const timeLeft = m.castTime + m.delay - now;
  if (timeLeft < 100) return -1;
  const delayPerFrame = Math.trunc(m.delay / totalFrames);
  if (delayPerFrame <= 0) return -1;
  return totalFrames - Math.trunc(timeLeft / delayPerFrame);
}

/** MagicButton (231 x 33) with HeroMagic = true. */
class HeroMagicButton {
  private readonly root: MirControl;
  private readonly skillButton: MirButton;
  private readonly levelLabel: MirLabel;
  private readonly nameLabel: MirLabel;
  private readonly expLabel: MirLabel;
  private readonly keyLabel: MirLabel;
  private readonly coolDown: MirImage;
  private magic: UserMagic | null = null;
  private icon = -1;

  constructor(parent: MirControl, location: readonly [number, number], assign: (m: UserMagic) => void) {
    this.root = new MirControl({ parent, location, size: [231, 33] });
    this.skillButton = new MirButton({ parent: this.root, library: Lib.MagIcon2, index: 0, pressedIndex: 1, location: [36, 0], sound: SoundList.ButtonA,
      onClick: () => { if (this.magic) assign(this.magic); } });
    new MirImage({ parent: this.root, library: Lib.Title, index: 516, location: [73, 7], notControl: true });
    new MirImage({ parent: this.root, library: Lib.Title, index: 517, location: [73, 19], notControl: true });
    this.levelLabel = new MirLabel({ parent: this.root, autoSize: true, location: [88, 2], notControl: true });
    this.nameLabel = new MirLabel({ parent: this.root, autoSize: true, location: [109, 2], notControl: true });
    this.expLabel = new MirLabel({ parent: this.root, autoSize: true, location: [109, 15], notControl: true });
    this.keyLabel = new MirLabel({ parent: this.root, autoSize: true, location: [2, 2], notControl: true });
    this.coolDown = new MirImage({ parent: this.root, library: Lib.Prguse2, index: -1, location: [36, 0], notControl: true, useOffset: true, opacity: 0.6 });
  }

  set visible(v: boolean) { this.root.visible = v; }

  /** MagicButton.Update + SetDelay. */
  update(m: UserMagic, now: number): void {
    this.magic = m;
    this.nameLabel.text = m.Name;
    this.levelLabel.text = String(m.Level);
    this.expLabel.text = m.Level === 0 ? `${m.Experience}/${m.Need1}` : m.Level === 1 ? `${m.Experience}/${m.Need2}` : m.Level === 2 ? `${m.Experience}/${m.Need3}` : "-";
    this.keyLabel.text = m.Key === 0 ? "" : `${PREFIXES[Math.floor((m.Key - 1) / 8)] ?? ""}${m.Key > 8 ? "\n" : ""}F${((m.Key - 1) % 8) + 1}`;
    const desc = SKILL_DESCRIPTIONS[m.Spell];
    const hint = desc ? fmt(desc, m.Level, nextLevel(m), m.BaseCost) : "";
    if (this.skillButton.hint !== hint) this.skillButton.hint = hint;
    if (this.icon !== m.Icon) { this.icon = m.Icon; this.skillButton.setIndices(m.Icon * 2, -1, m.Icon * 2 + 1); }
    const frame = coolDownFrame(m, 34, now);
    this.coolDown.visible = frame >= 0;
    if (frame >= 0) this.coolDown.index = 1290 + frame;
  }
}

export class HeroMagicWindow {
  private readonly magic: Magic;
  private readonly win: MirWindow;
  private readonly nameLabel: MirLabel;
  private readonly guildLabel: MirLabel;
  private readonly classImage: MirImage;
  private readonly buttons: HeroMagicButton[] = [];
  private startIndex = 0;

  constructor(_stage: HTMLElement, magic: Magic) {
    this.magic = magic;
    heroDialogs.skills = this;
    // CharacterDialog ctor (the skill page only)
    this.win = new MirWindow({ library: Lib.Title, index: 504, movable: true, sort: true, location: [1024 - 264, 0] });
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    const skill = new MirImage({ parent: this.win, library: Lib.Title, index: 508, location: [8, 90] });
    const tab = (page: "character" | "status" | "state" | null, index: number, pressed: number, x: number) =>
      new MirButton({ parent: this.win, library: Lib.Title, index, pressedIndex: pressed, location: [x, 70], size: [64, 20], sound: SoundList.ButtonA,
        onClick: () => { if (!page) return; this.toggle(false); heroDialogs.character?.openPage(page); } });
    tab("character", -1, 500, 8);
    tab("status", -1, 501, 70);
    tab("state", -1, 502, 132);
    tab(null, 503, 503, 194); // ShowSkillPage: SkillButton.Index = 503
    closeButton(this.win, [241, 3], () => this.toggle(false));
    this.nameLabel = new MirLabel({ parent: this.win, location: [0, 12], size: [264, 20], center: true, notControl: true });
    this.guildLabel = new MirLabel({ parent: this.win, location: [0, 33], size: [264, 30], center: true, notControl: true });
    this.classImage = new MirImage({ parent: this.win, library: Lib.Prguse, index: 100, location: [15, 33], notControl: true });
    // SkillPage: Magics[7], NextButton, BackButton
    for (let i = 0; i < 7; i++) this.buttons.push(new HeroMagicButton(skill, [8, 8 + i * 33], (m) => this.assignKey(m)));
    new MirButton({ parent: skill, library: Lib.Prguse, index: 396, pressedIndex: 397, location: [140, 250], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex + 7 >= this.magic.heroMagics.length) return; this.startIndex += 7; this.render(); } });
    new MirButton({ parent: skill, library: Lib.Prguse, index: 398, pressedIndex: 399, location: [90, 250], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex - 7 < 0) return; this.startIndex -= 7; this.render(); } });

    magic.onHeroChange(() => this.render());
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      if (document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement) return;
      // KeybindOptions.HeroSkills (Ctrl+S): only while GameScene.Hero exists
      if (matches(e, KeybindOptions.HeroSkills)) {
        e.preventDefault(); // the browser's "save page"
        if (this.magic.heroId) this.toggle();
        return;
      }
      if (matches(e, KeybindOptions.Closeall)) this.toggle(false); // HeroDialog?.Hide()
    }, { signal });
    signal.addEventListener("abort", () => { this.win.dispose(); if (heroDialogs.skills === this) heroDialogs.skills = null; });
  }

  get visible(): boolean { return this.win.visible; }

  /** KeybindOptions.HeroSkills: HeroDialog.Show + ShowSkillPage, or Hide when the skill page is up. */
  toggle(show = !this.win.visible): void {
    if (show && this.magic.heroId === 0) return;
    if (show) {
      if (heroDialogs.character?.visible) heroDialogs.character.window.toggle(false);
      this.win.show();
    } else this.win.hide();
    this.render();
  }

  /** MagicButton.SkillButton.Click (HeroMagic): AssignKeyPanel(Magic, 17, Shift+F1-F8) unless the hero is gone. */
  private assignKey(m: UserMagic): void {
    if (!this.magic.heroId) return;
    assignKeyPanel(m, 17, HERO_KEYS, (key) => this.magic.assignHeroKey(m, key));
  }

  /** CharacterDialog.RefreshInterface for the skill page. */
  render(): void {
    if (!this.magic.heroId) { this.win.hide(); return; } // the desktop disposes the hero dialogs below Summoned
    if (!this.win.visible) return;
    const v = heroDialogs.character?.window.lastView ?? null;
    this.nameLabel.text = v?.name ?? "";
    this.guildLabel.text = " "; // Actor.GuildName + " " + Actor.GuildRankName (a hero has neither)
    if (v) this.classImage.index = 100 + Math.min(4, Math.max(0, v.cls)); // Warrior 100 .. Archer 104
    const list = this.magic.heroMagics;
    if (this.startIndex >= list.length) this.startIndex = 0;
    const now = performance.now();
    for (let i = 0; i < this.buttons.length; i++) {
      const m = list[i + this.startIndex];
      this.buttons[i]!.visible = !!m;
      if (m) this.buttons[i]!.update(m, now);
    }
  }

  /** Per frame: the cooldown frames (MagicButton.CoolDown). */
  tick(_now: number): void {
    if (this.win.visible) this.render();
  }
}
