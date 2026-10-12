// HeroManageDialog + HeroManageAvatar (Client/MirScenes/Dialogs/HeroDialogs.cs) with the desktop artwork: Prguse 1688 at
// (350, 350), the current hero's avatar at (15, 61) and up to 8 stored heroes (GameScene.HeroStorage, MaximumHeroCount)
// at (98 + 60 * (i % 4), 61 + 40 * (i / 4)), each moved 5 px when it gets a hero (HeroManageAvatar.Info) and drawn as
// Prguse HeroAvatar + 370 with ClientHeroInformation.ToString as the hint; slots past MaximumHeroCount - 1 show the
// default Prguse 1689 picture (locked). Opened by S.ManageHeroes (GameScene.ManageHeroes); clicking a stored hero asks
// ClientTextKeys.MakeActiveHero and sends C.ChangeHero { ListIndex = index + 1 }. Escape hides it (GameScene Closeall).
// NewHeroDialog (GameScene.NewHeroDialog = a NewCharacterDialog, newCharacterDialog.ts, with TitleLabel = Title 847 at
// (246, 11)): opened by S.HeroCreateRequest with the class buttons from CanCreateClass, OK sends C.NewHero, results
// from S.NewHero (GameScene.NewHero message boxes).

import { ClientPackets } from "../protocol/client.ts";
import { MirClassName, MirGenderName } from "../protocol/enums.generated.ts";
import type { ClientHeroInformation } from "../protocol/server.ts";
import { NEW_HERO_TEXT, type Hero } from "../game/hero.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, Lib, closeButton, libSize } from "./mir/controls.ts";
import { messageBox } from "./messageBox.ts";
import { heroAvatar } from "./heroPanels.ts";
import { NewCharacterDialog } from "./newCharacterDialog.ts";

type Send = (f: Uint8Array) => void;

const STORAGE_SLOTS = 8; // HeroManageDialog.Avatars
const DEFAULT_AVATAR = 1689; // HeroManageAvatar.DefaultIndex

/** ClientHeroInformation.ToString (the HeroManageAvatar hint). */
function heroHint(h: ClientHeroInformation): string {
  return `${h.Name}\nLevel ${h.Level} ${(MirGenderName[h.Gender] ?? "").toLowerCase()} ${(MirClassName[h.Class] ?? "").toLowerCase()}`;
}

/** HeroManageAvatar: Prguse DefaultIndex until a hero is set. */
class HeroManageAvatar {
  readonly image: MirImage;
  info: ClientHeroInformation | null = null;

  constructor(parent: MirWindow, onClick?: () => void) {
    this.image = new MirImage({ parent, library: Lib.Prguse, index: DEFAULT_AVATAR, visible: false, onClick });
  }

  /** HeroManageAvatar.Info setter (after RefreshInterface / SetCurrentHero set the base Location). */
  set(info: ClientHeroInformation | null, base: readonly [number, number]): void {
    this.info = info;
    this.image.location = base;
    if (!info) { this.image.visible = false; this.image.hint = ""; return; }
    this.image.location = [base[0] + 5, base[1] + 5];
    this.image.index = heroAvatar(info.Class, info.Gender) + 370;
    this.image.hint = heroHint(info);
    this.image.visible = true;
  }
}

export class HeroManageWindow {
  private readonly win: MirWindow;
  private readonly current: HeroManageAvatar;
  private readonly avatars: HeroManageAvatar[] = [];
  private closeBox: (() => void) | null = null;

  constructor(private readonly stage: HTMLElement, private readonly hero: Hero, private readonly send: Send) {
    // HeroManageDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 1688, movable: true, sort: true, location: [350, 350], onHide: () => { this.closeBox?.(); this.closeBox = null; } });
    const close = closeButton(this.win, [0, 4], () => this.toggle(false));
    void libSize(Lib.Prguse, 1688).then(([w]) => { close.location = [w - 24, 4]; });
    this.current = new HeroManageAvatar(this.win);
    for (let i = 0; i < STORAGE_SLOTS; i++) {
      const index = i;
      this.avatars.push(new HeroManageAvatar(this.win, () => { const info = this.avatars[index]!.info; if (info) this.askChange(index, info); }));
    }
    hero.onChange(() => this.onHeroChange());
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Closeall)) this.toggle(false); // HeroManageDialog?.Hide()
    }, { signal });
    signal.addEventListener("abort", () => this.win.dispose());
  }

  toggle(show = !this.win.visible): void {
    if (show) { this.render(); this.win.show(); } // HeroManageDialog.Show: RefreshInterface first
    else this.win.hide();
  }

  private onHeroChange(): void {
    if (this.hero.openManage) { // GameScene.ManageHeroes: HeroManageDialog.Show
      this.hero.openManage = false;
      this.toggle(true);
      return;
    }
    if (this.win.visible) this.render(); // GameScene.ChangeHero / NewHeroInfo: RefreshInterface
  }

  /** HeroManageDialog.RefreshInterface + SetCurrentHero. */
  private render(): void {
    this.current.set(this.hero.currentHero, [15, 61]);
    for (let i = 0; i < this.avatars.length; i++) {
      const a = this.avatars[i]!;
      const base: [number, number] = [98 + 60 * (i % 4), 61 + 40 * Math.floor(i / 4)];
      if (i > this.hero.maximumCount - 2) {
        // Info = null, then Visible = true and NotControl: the locked slot keeps its last picture (DefaultIndex at first)
        a.set(null, base);
        a.image.visible = true;
        a.image.notControl = true;
        continue;
      }
      a.set(this.hero.storage[i] ?? null, base);
      a.image.notControl = false;
    }
  }

  /** Avatars[index].Click: MirMessageBox (YesNo) ClientTextKeys.MakeActiveHero. */
  private askChange(index: number, info: ClientHeroInformation): void {
    this.closeBox?.();
    this.closeBox = messageBox(this.stage, `Would you like to make ${info.Name} your active Hero?`, [
      { label: "Yes", onClick: () => { this.closeBox = null; this.send(ClientPackets.ChangeHero({ ListIndex: index + 1 })); } },
      { label: "No", onClick: () => { this.closeBox = null; } },
    ]);
  }
}

/** GameScene.NewHeroDialog (NewCharacterDialog with the hero title). */
export class NewHeroWindow {
  private readonly dialog: NewCharacterDialog;
  /** The S.HeroCreateRequest this dialog was opened for (a cancel does not reopen it). */
  private shownFor: boolean[] | null = null;

  constructor(private readonly stage: HTMLElement, private readonly hero: Hero, private readonly send: Send) {
    this.dialog = new NewCharacterDialog(stage);
    // NewHeroDialog.TitleLabel.Index = 847, Location = (246, 11): NewCharacterDialog keeps its TitleLabel private, so its
    // Title 20 picture is hidden and the hero title drawn in its place
    const title = this.dialog.win.el.querySelector(`.mir-pic[data-pic="${Lib.Title}#20"]`)?.parentElement;
    if (title && title !== this.dialog.win.el) title.hidden = true;
    new MirImage({ parent: this.dialog.win, library: Lib.Title, index: 847, location: [246, 11] });
    // NewHeroDialog.OnCreateCharacter -> C.NewHero
    this.dialog.onCreate = () => this.send(ClientPackets.NewHero({ Name: this.dialog.nameTextBox.text, Gender: this.dialog.gender, Class: this.dialog.cls }));
    hero.onChange(() => this.onHeroChange());
    gameSignal().addEventListener("abort", () => this.dialog.dispose());
  }

  /** NewCharacterDialog's WarriorButton .. ArcherButton (kept private there), by class. */
  private classButtons(): [number, MirButton][] {
    return (this.dialog as unknown as { classButtons?: [number, MirButton][] }).classButtons ?? [];
  }

  private onHeroChange(): void {
    const result = this.hero.newHeroResult;
    if (result !== null) { // GameScene.NewHero
      this.hero.newHeroResult = null;
      this.dialog.okButton.enabled = true;
      const text = NEW_HERO_TEXT[result];
      if (text) messageBox(this.stage, text);
      if (result === 1 || result === 5) this.dialog.nameTextBox.focus();
    }
    const req = this.hero.createRequest;
    if (!req) { this.dialog.hide(); this.shownFor = null; return; } // NewHeroDialog.Hide
    if (req !== this.shownFor) { // GameScene.HeroCreateRequest
      this.shownFor = req;
      for (const [cls, b] of this.classButtons()) b.visible = !!req[cls];
      this.dialog.show();
    }
  }
}
