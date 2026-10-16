// The user's magic UI: SkillBarDialog x 2 (Client/MirScenes/Dialogs/MainDialogs.cs: Prguse 2190 bars at (0, 0) and
// (216, 0) for keys 1-8 / 9-16, MagIcon icons, Prguse2 1260+ cooldown frames, key names), MagicButton (the rows of
// CharacterDialog.SkillPage, characterWindow.ts) and AssignKeyPanel (Prguse 710: pick F1-F8 / Ctrl+F1-F8, None,
// Save -> C.MagicKey, clearing the key from any other spell). S / F11 (KeybindOptions.Skills / Skills2) open the
// CharacterDialog on its skill page, R (KeybindOptions.Skillbar) shows / hides the bars. The hero's spells
// (keys 17-24, Shift+F1-F8) have their own window: heroMagicWindow.ts.

import { ClientPackets } from "../protocol/client.ts";
import type { Magic, UserMagic } from "../game/magic.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, getKeyBind, keyLabel, matches, matchesAny, type KeybindOption } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, libPicture, setPicture } from "./mir/controls.ts";
import { SKILL_DESCRIPTIONS } from "./skillHints.ts";
import { userWindows, registerUserWindow } from "./userRefs.ts";

/** MagIcon2[index] as an image URL (heroMagicWindow.ts still draws <img> icons). */
export function magicIcon(index: number): Promise<string | null> {
  return libPicture(Lib.MagIcon2, index).then((pic) => pic?.url ?? null);
}

/** Functions.PrintTimeSpanFromMilliSeconds. */
function printTimeSpanMs(ms: number): string {
  const s = Math.floor(ms / 1000) % 60, m = Math.floor(ms / 60000) % 60, h = Math.floor(ms / 3600000);
  const pad = (n: number) => String(n).padStart(2, "0");
  if (ms < 60000) return `${s}.${Math.floor((ms % 1000) / 100)}s`;
  if (ms < 3600000) return `${ms / 60000}m ${pad(s)}s`;
  if (ms < 86400000) return `${h}h ${pad(m)}m ${pad(s)}s`;
  return `${Math.floor(h / 24)}d ${h % 24}h ${pad(m)}m ${pad(s)}s`;
}

const fmt = (text: string, ...args: (string | number)[]) => text.replace(/\{(\d+)\}/g, (_, i: string) => String(args[Number(i)] ?? ""));

/** The skill descriptions' "next level" value (Level1 / Level2 / Level3 by current level). */
const nextLevel = (m: UserMagic) => (m.Level === 0 ? m.Level1 : m.Level === 1 ? m.Level2 : m.Level === 2 ? m.Level3 : 0);

/** Cooldown frame (SetDelay / ProcessSkillDelay): -1 = none. */
function coolDownFrame(m: UserMagic, totalFrames: number, now: number): number {
  if (!Number.isFinite(m.castTime) || m.delay <= 0) return -1;
  const timeLeft = m.castTime + m.delay - now;
  if (timeLeft < 100) return -1;
  const delayPerFrame = Math.trunc(m.delay / totalFrames);
  if (delayPerFrame <= 0) return -1;
  return totalFrames - Math.trunc(timeLeft / delayPerFrame);
}

const USER_KEYS = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "Ctrl\nF1", "Ctrl\nF2", "Ctrl\nF3", "Ctrl\nF4", "Ctrl\nF5", "Ctrl\nF6", "Ctrl\nF7", "Ctrl\nF8"];

/** AssignKeyPanel (modal, centred): the spell icon, "Select the Key for: X", the F-key buttons (Prguse 1656-1658, the
 *  chosen one held at 1658), None (Title 287) and Save (Title 156). `onSave(key)` sends the assignment. */
export function assignKeyPanel(magic: UserMagic, keyOffset: number, keyStrings: readonly string[], onSave: (key: number) => void): void {
  let key = magic.Key;
  const win = new MirWindow({ library: Lib.Prguse, index: 710, location: "center", modal: true, sort: true });
  new MirImage({ parent: win, library: Lib.MagIcon2, index: magic.Icon * 2, location: [16, 16] });
  new MirLabel({ parent: win, location: [49, 17], size: [230, 32], align: "center", text: `Select the Key for: ${magic.Name}` });
  const fkeys = keyStrings.map((text, i) => new MirButton({ parent: win, library: Lib.Prguse, index: 1656, hoverIndex: 1657, pressedIndex: 1658,
    location: [17 + 32 * (i % 8) + 5 * Math.floor((i % 8) / 4), 58 + 37 * Math.floor(i / 8)], sound: SoundList.ButtonA, text,
    onClick: () => { key = i + keyOffset; refresh(); } }));
  // AssignKeyPanel_BeforeDraw
  const refresh = () => fkeys.forEach((b, i) => { if (i === key - keyOffset) b.setIndices(1658, 1658, 1658); else b.setIndices(1656, 1657, 1658); });
  new MirButton({ parent: win, library: Lib.Title, index: 287, hoverIndex: 288, pressedIndex: 289, location: [284, 64], onClick: () => { key = 0; refresh(); } });
  new MirButton({ parent: win, library: Lib.Title, index: 156, hoverIndex: 157, pressedIndex: 158, location: [284, 101], onClick: () => { onSave(key); win.dispose(); } });
  refresh();
  win.show();
}

/** MagicButton (231 x 33): key label, the spell icon button (opens AssignKeyPanel), level / exp art, level, name, exp,
 *  the cooldown overlay (Prguse2 1290 + frame of 34). */
export class MagicButton {
  private readonly root: MirControl;
  private readonly skillButton: MirButton;
  private readonly levelLabel: MirLabel;
  private readonly nameLabel: MirLabel;
  private readonly expLabel: MirLabel;
  private readonly keyLabel: MirLabel;
  private readonly coolDown: MirImage;
  private magic: UserMagic | null = null;
  private icon = -1;

  constructor(parent: MirControl, location: readonly [number, number], heroMagic = false) {
    this.root = new MirControl({ parent, location, size: [231, 33] });
    this.skillButton = new MirButton({ parent: this.root, library: Lib.MagIcon2, index: 0, pressedIndex: 1, location: [36, 0], sound: SoundList.ButtonA,
      onClick: () => {
        const m = this.magic, bar = userWindows.magic;
        if (!m || heroMagic || !bar) return; // the hero's key panel lives in heroMagicWindow.ts
        assignKeyPanel(m, 1, USER_KEYS, (key) => bar.assign(m, key));
      } });
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
  update(m: UserMagic): void {
    this.magic = m;
    this.nameLabel.text = m.Name;
    this.levelLabel.text = String(m.Level);
    this.expLabel.text = m.Level === 0 ? `${m.Experience}/${m.Need1}` : m.Level === 1 ? `${m.Experience}/${m.Need2}` : m.Level === 2 ? `${m.Experience}/${m.Need3}` : "-";
    this.keyLabel.text = m.Key === 0 ? "" : `${["", "CTRL", "Shift"][Math.floor((m.Key - 1) / 8)] ?? ""}${m.Key > 8 ? "\n" : ""}F${((m.Key - 1) % 8) + 1}`;
    const desc = SKILL_DESCRIPTIONS[m.Spell];
    const hint = desc ? fmt(desc, m.Level, nextLevel(m), m.BaseCost) : "";
    if (this.skillButton.hint !== hint) this.skillButton.hint = hint;
    if (this.icon !== m.Icon) { this.icon = m.Icon; this.skillButton.setIndices(m.Icon * 2, -1, m.Icon * 2 + 1); }
    const frame = coolDownFrame(m, 34, performance.now());
    this.coolDown.visible = frame >= 0;
    if (frame >= 0) this.coolDown.index = 1290 + frame;
  }
}

/** SkillBarDialog (BarIndex 0 / 1). */
class SkillBar {
  readonly win: MirWindow;
  private readonly cells: MirImage[] = [];
  private readonly coolDowns: MirImage[] = [];
  private readonly keyNames: MirLabel[] = [];
  hasSkill = false;

  constructor(private readonly magic: Magic, readonly barIndex: number) {
    this.win = new MirWindow({ library: Lib.Prguse, index: 2190, movable: true, sort: true, location: [barIndex === 0 ? 0 : 216, 0] });
    // MagicKeyDialog_BeforeDraw: Prguse 2193 at (12, 0) at 50 %, under the bar
    const backdrop = document.createElement("div");
    backdrop.className = "mir-pic";
    backdrop.style.opacity = "0.5";
    backdrop.style.marginLeft = "12px";
    this.win.el.insertBefore(backdrop, this.win.el.firstChild);
    void setPicture(backdrop, Lib.Prguse, 2193, true);
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 2247, size: [16, 28], location: [0, 0], sound: SoundList.ButtonA, onClick: () => this.update() });
    for (let i = 0; i < 8; i++) {
      const key = i + 1 + 8 * barIndex;
      this.cells.push(new MirImage({ parent: this.win, library: Lib.MagIcon, index: -1, location: [i * 25 + 15, 3], onClick: () => useSpellKey(key) }));
      this.coolDowns.push(new MirImage({ parent: this.win, library: Lib.Prguse2, index: -1, location: [i * 25 + 15, 3], notControl: true, useOffset: true, opacity: 0.6, visible: false }));
    }
    new MirLabel({ parent: this.win, text: String(barIndex + 1), location: [0, 1], size: [10, 25], notControl: true });
    for (let i = 0; i < 8; i++) this.keyNames.push(new MirLabel({ parent: this.win, text: `F${i + 1}`, location: [i * 25 + 13, 0], size: [25, 25], notControl: true }));
  }

  /** SkillBarDialog.Update. */
  update(): void {
    const offset = this.barIndex * 8;
    this.hasSkill = this.magic.magics.some((m) => m.Key >= offset + 1 && m.Key <= offset + 9);
    for (let i = 1; i <= 8; i++) {
      const cell = this.cells[i - 1]!;
      const key = keyLabel(barOption(this.barIndex, i));
      this.keyNames[i - 1]!.text = key;
      const m = this.magic.magics.find((x) => x.Key === i + offset);
      cell.index = m ? m.Icon * 2 : -1;
      const hint = m ? fmt("{0}\nMP: {1}\nCooldown: {2}\nKey: {3}", m.Name, m.BaseCost + m.LevelCost * m.Level, printTimeSpanMs(m.delay), key) : "";
      if (cell.hint !== hint) cell.hint = hint;
      if (m) { this.hasSkill = true; this.keyNames[i - 1]!.text = ""; }
    }
  }

  /** ProcessSkillDelay (22 frames from Prguse2 1260). */
  process(now: number): void {
    if (!this.win.visible) return;
    const offset = this.barIndex * 8;
    for (let i = 0; i < 8; i++) {
      const m = this.magic.magics.find((x) => x.Key === i + offset + 1);
      const frame = m ? coolDownFrame(m, 22, now) : -1;
      this.coolDowns[i]!.visible = frame >= 0;
      if (frame >= 0) this.coolDowns[i]!.index = 1260 + frame;
    }
  }
}

const barOption = (bar: number, i: number) => ((bar === 0 ? KeybindOptions.Bar1Skill1 : KeybindOptions.Bar2Skill1) + i - 1) as KeybindOption;

/** SkillBarDialog cell click -> GameScene.UseSpell(key): replays the key's binding so game.ts casts at the mouse. */
function useSpellKey(key: number): void {
  const b = getKeyBind(barOption(key > 8 ? 1 : 0, ((key - 1) % 8) + 1));
  if (!b || b.key === "") return;
  const code = /^F\d+$/.test(b.key) ? b.key : /^[A-Z]$/.test(b.key) ? `Key${b.key}` : /^D\d$/.test(b.key) ? `Digit${b.key.slice(1)}` : b.key;
  window.dispatchEvent(new KeyboardEvent("keydown", { key: b.key.length === 1 ? b.key.toLowerCase() : b.key, code,
    ctrlKey: b.requireCtrl === 1, altKey: b.requireAlt === 1, shiftKey: b.requireShift === 1, bubbles: true, cancelable: true }));
}

export class MagicBar {
  private readonly magic: Magic;
  private readonly send: (f: Uint8Array) => void;
  private readonly bars: SkillBar[];
  /** Settings.SkillBar. */
  private showBars = true;

  constructor(_stage: HTMLElement, magic: Magic, send: (f: Uint8Array) => void) {
    this.magic = magic;
    this.send = send;
    registerUserWindow("magic", this);
    this.bars = [new SkillBar(magic, 0), new SkillBar(magic, 1)];
    magic.subscribe(() => this.render());
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.Skills (F11) / Skills2 (S without Ctrl): CharacterDialog.ShowSkillPage, or hide
      if (matchesAny(e, KeybindOptions.Skills, KeybindOptions.Skills2)) {
        if (e.key === "F11") e.preventDefault(); // the browser's full screen
        this.toggle();
      }
      // KeybindOptions.Skillbar: Settings.SkillBar on / off
      if (matches(e, KeybindOptions.Skillbar)) { this.showBars = !this.showBars; this.render(); }
    }, { signal });
    signal.addEventListener("abort", () => { for (const b of this.bars) b.win.dispose(); });
    this.render();
  }

  /** User.Magics. */
  get magics(): readonly UserMagic[] {
    return this.magic.magics;
  }

  /** Opens the CharacterDialog on its skill page (or hides it when the skill page is up); `show` forces a state. */
  toggle(show?: boolean): void {
    const ch = userWindows.character;
    if (!ch) return;
    if (show === undefined) ch.openPage("skill");
    else if (show) { ch.toggle(true); ch.showPage("skill"); }
    else ch.toggle(false);
  }

  /** AssignKeyPanel.SaveButton: one key per spell; the key is taken from whichever spell had it. */
  assign(m: UserMagic, key: number): void {
    for (const other of this.magic.magics) if (other.Key === key) other.Key = 0;
    this.send(ClientPackets.MagicKey({ Spell: m.Spell, Key: key, OldKey: m.Key }));
    m.Key = key;
    this.render();
  }

  /** SkillBarDialog.Update for both bars + GameScene.DialogProcess (Settings.SkillBar: Show only when HasSkill). */
  render(): void {
    for (const b of this.bars) {
      b.update();
      if (this.showBars && b.hasSkill) { if (!b.win.visible) b.win.show(); } else b.win.hide();
    }
    userWindows.character?.render();
  }

  /** Per frame: the bars' cooldown frames. */
  tick(now: number): void {
    for (const b of this.bars) b.process(now);
  }
}
