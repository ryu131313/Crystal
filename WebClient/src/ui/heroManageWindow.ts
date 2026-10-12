// HeroManageDialog + HeroManageAvatar (Client/MirScenes/Dialogs/HeroDialogs.cs): the current hero and up to 8 stored heroes
// (GameScene.HeroStorage, MaximumHeroCount); opened by S.ManageHeroes (GameScene.ManageHeroes), clicking a stored hero asks
// ClientTextKeys.MakeActiveHero and sends C.ChangeHero { ListIndex = index + 1 }.
// NewHeroDialog (GameScene.NewHeroDialog = a NewCharacterDialog with the hero title): opened by S.HeroCreateRequest with the
// class buttons from CanCreateClass, OK sends C.NewHero, results from S.NewHero (GameScene.NewHero message boxes).

import { ClientPackets } from "../protocol/client.ts";
import { MirClass, MirClassName, MirGender, MirGenderName } from "../protocol/enums.generated.ts";
import type { ClientHeroInformation } from "../protocol/server.ts";
import { NEW_HERO_TEXT, type Hero } from "../game/hero.ts";
import { messageBox } from "./messageBox.ts";
import { heroAvatar, setPrguseImage } from "./heroPanels.ts";
import { el } from "./screens.ts";

type Send = (f: Uint8Array) => void;

const STORAGE_SLOTS = 8; // HeroManageDialog.Avatars
const MIN_NAME = 3, MAX_NAME = 15; // Globals.MinCharacterNameLength / MaxCharacterNameLength
/** NewCharacterDialog.Reg, as written (the alternation makes it "starts with a letter / digit, or ends with 3-15 CJK"). */
const NAME_REG = new RegExp(`^[A-Za-z0-9]|[\\u4e00-\\u9fa5]{${MIN_NAME},${MAX_NAME}}$`);

/** ClientHeroInformation.ToString (the HeroManageAvatar hint). */
function heroHint(h: ClientHeroInformation): string {
  return `${h.Name}\nLevel ${h.Level} ${(MirGenderName[h.Gender] ?? "").toLowerCase()} ${(MirClassName[h.Class] ?? "").toLowerCase()}`;
}

export class HeroManageWindow {
  private readonly root = el("div", { class: "window hero-manage-window", hidden: true });
  private readonly current = el("div", { class: "hero-manage-current" });
  private readonly grid = el("div", { class: "hero-manage-grid" });
  private closeBox: (() => void) | null = null;

  constructor(private readonly stage: HTMLElement, private readonly hero: Hero, private readonly send: Send) {
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Heroes"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "hero-manage-body" }, el("div", {}, el("div", { class: "magic-meta" }, "Active"), this.current),
        el("div", {}, el("div", { class: "magic-meta" }, "Stored"), this.grid)));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    hero.onChange(() => this.onHeroChange());
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (!show) { this.closeBox?.(); this.closeBox = null; }
    this.render();
  }

  private onHeroChange(): void {
    if (this.hero.openManage) { // GameScene.ManageHeroes: HeroManageDialog.Show
      this.hero.openManage = false;
      this.toggle(true);
      return;
    }
    this.render(); // GameScene.ChangeHero / NewHeroInfo: RefreshInterface
  }

  /** HeroManageDialog.RefreshInterface + SetCurrentHero. */
  private render(): void {
    if (this.root.hidden) return;
    this.current.replaceChildren(this.avatar(this.hero.currentHero, false, null));
    const cells: HTMLElement[] = [];
    for (let i = 0; i < STORAGE_SLOTS; i++) {
      // slots past MaximumHeroCount - 1 (the current hero takes one) show locked
      if (i > this.hero.maximumCount - 2) { cells.push(this.avatar(null, true, null)); continue; }
      const info = this.hero.storage[i] ?? null;
      cells.push(this.avatar(info, false, info ? () => this.askChange(i, info) : null));
    }
    this.grid.replaceChildren(...cells);
  }

  /** HeroManageAvatar: the hero's face (HeroAvatar + 370) with ClientHeroInformation.ToString as the hint. */
  private avatar(info: ClientHeroInformation | null, locked: boolean, onClick: (() => void) | null): HTMLElement {
    const cell = el("div", { class: `hero-manage-avatar${locked ? " locked" : ""}${info ? "" : " empty"}` });
    if (locked) { cell.title = "Locked"; return cell; }
    if (!info) return cell;
    const img = el("img", { alt: "" });
    setPrguseImage(img, heroAvatar(info.Class, info.Gender) + 370);
    cell.append(img, el("span", { class: "hero-manage-name" }, info.Name));
    cell.title = heroHint(info);
    if (onClick) { cell.classList.add("clickable"); cell.onclick = onClick; }
    return cell;
  }

  /** Avatars[index].Click: MirMessageBox (YesNo) ClientTextKeys.MakeActiveHero. */
  private askChange(index: number, info: ClientHeroInformation): void {
    this.closeBox?.();
    this.closeBox = messageBox(this.stage, `Would you like to make ${info.Name} your active Hero?`, [
      { label: "Yes", primary: true, onClick: () => { this.closeBox = null; this.send(ClientPackets.ChangeHero({ ListIndex: index + 1 })); } },
      { label: "No", onClick: () => { this.closeBox = null; } },
    ]);
  }
}

const CLASSES: number[] = [MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin, MirClass.Archer];

/** GameScene.NewHeroDialog (NewCharacterDialog). */
export class NewHeroWindow {
  private readonly root = el("div", { class: "window new-hero-window", hidden: true });
  private readonly nameInput = el("input", { maxLength: MAX_NAME, placeholder: "Hero name" });
  private readonly okButton = el("button", { class: "primary", disabled: true }, "OK");
  private readonly classButtons = new Map<number, HTMLButtonElement>();
  private readonly genderButtons = new Map<number, HTMLButtonElement>();
  private cls: number = MirClass.Warrior;
  private gender: number = MirGender.Male;
  /** The S.HeroCreateRequest this dialog was opened for (a cancel does not reopen it). */
  private shownFor: boolean[] | null = null;

  constructor(private readonly stage: HTMLElement, private readonly hero: Hero, private readonly send: Send) {
    for (const c of CLASSES) {
      const b = el("button", { onclick: () => { this.cls = c; this.updateInterface(); } }, MirClassName[c] ?? String(c));
      this.classButtons.set(c, b);
    }
    for (const g of [MirGender.Male, MirGender.Female]) {
      const b = el("button", { onclick: () => { this.gender = g; this.updateInterface(); } }, MirGenderName[g] ?? String(g));
      this.genderButtons.set(g, b);
    }
    this.nameInput.addEventListener("input", () => this.nameChanged());
    // TextBox_KeyPress: Enter clicks OK when enabled
    this.nameInput.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key === "Enter") { e.preventDefault(); if (!this.okButton.disabled) this.create(); }
    });
    this.okButton.onclick = () => this.create();
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "New Hero"), el("button", { class: "window-close", onclick: () => this.cancel() }, "×")),
      el("label", {}, "Name"), this.nameInput,
      el("div", { class: "new-hero-label" }, "Class"), el("div", { class: "row new-hero-choices" }, ...this.classButtons.values()),
      el("div", { class: "new-hero-label" }, "Gender"), el("div", { class: "row new-hero-choices" }, ...this.genderButtons.values()),
      el("div", { class: "row" }, this.okButton, el("button", { onclick: () => this.cancel() }, "Cancel")));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    hero.onChange(() => this.onHeroChange());
  }

  private onHeroChange(): void {
    const result = this.hero.newHeroResult;
    if (result !== null) { // GameScene.NewHero
      this.hero.newHeroResult = null;
      this.okButton.disabled = !this.nameValid();
      const text = NEW_HERO_TEXT[result];
      if (text) messageBox(this.stage, text);
      if (result === 1 || result === 5) this.nameInput.focus();
    }
    const req = this.hero.createRequest;
    if (!req) { if (!this.root.hidden) this.hide(); this.shownFor = null; return; } // NewHeroDialog.Hide
    if (req !== this.shownFor) this.show(req); // GameScene.HeroCreateRequest
  }

  /** NewCharacterDialog.Show: Warrior / Male, empty name; class buttons from CanCreateClass. */
  private show(req: boolean[]): void {
    this.shownFor = req;
    for (const [c, b] of this.classButtons) b.hidden = !req[c];
    this.cls = MirClass.Warrior;
    this.gender = MirGender.Male;
    this.nameInput.value = "";
    this.nameChanged();
    this.updateInterface();
    this.root.hidden = false;
    this.nameInput.focus();
  }

  private hide(): void {
    this.root.hidden = true;
    this.nameInput.blur();
  }

  /** CancelButton: Hide (the request stays answered; the server sends a new HeroCreateRequest next time). */
  private cancel(): void {
    this.hide();
  }

  private nameValid(): boolean {
    const n = this.nameInput.value;
    return n.length > 0 && NAME_REG.test(n);
  }

  /** CharacterNameTextBox_TextChanged. */
  private nameChanged(): void {
    const n = this.nameInput.value;
    this.okButton.disabled = !this.nameValid();
    this.nameInput.classList.toggle("name-bad", n.length > 0 && !NAME_REG.test(n));
    this.nameInput.classList.toggle("name-good", this.nameValid());
  }

  /** CreateCharacter (OKButton disabled until S.NewHero) + GameScene's OnCreateCharacter. */
  private create(): void {
    if (!this.nameValid()) return;
    this.okButton.disabled = true;
    this.send(ClientPackets.NewHero({ Name: this.nameInput.value, Gender: this.gender, Class: this.cls }));
  }

  /** NewCharacterDialog.UpdateInterface (selected buttons). */
  private updateInterface(): void {
    for (const [c, b] of this.classButtons) b.classList.toggle("selected", c === this.cls);
    for (const [g, b] of this.genderButtons) b.classList.toggle("selected", g === this.gender);
  }
}
