// Hero magic window (Ctrl+S, KeybindOptions.HeroSkills): the hero's spells with level, experience, MP cost and key
// (CharacterDialog with MirGridType.HeroEquipment -> SkillPage; MagicButton with HeroMagic = true -> AssignKeyPanel(Magic, 17,
// Shift+F1-F8) with Actor = GameScene.Hero). Keys 17-24 cast on Shift+F1-F8 (Magic.useHeroSpell). Icons are MagIcon2[Icon * 2];
// the icon is shaded while the spell cools down.

import type { Magic, UserMagic } from "../game/magic.ts";
import { HERO_KEY_OFFSET } from "../game/magic.ts";
import { magicIcon } from "./magicBar.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

const keyName = (k: number) => (k <= HERO_KEY_OFFSET ? "-" : `Shift+F${k - HERO_KEY_OFFSET}`);

export class HeroMagicWindow {
  private readonly magic: Magic;
  private readonly window = el("div", { class: "window magic-window hero-magic-window", hidden: true });
  private readonly list = el("div", { class: "magic-list" });
  private rows: { m: UserMagic; shade: HTMLElement }[] = [];

  constructor(stage: HTMLElement, magic: Magic) {
    this.magic = magic;
    this.window.append(el("div", { class: "window-title" }, el("span", {}, "Hero Magic"), el("button", { class: "window-close", onclick: () => this.toggle(false), title: "Close (Ctrl+S)" }, "×")), this.list);
    this.window.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.window);
    magic.onHeroChange(() => this.render());
    // KeybindOptions.HeroSkills (Ctrl+S): only while GameScene.Hero exists
    window.addEventListener("keydown", (e) => {
      if (!matches(e, KeybindOptions.HeroSkills)) return; // Ctrl+S
      if (document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement) return;
      e.preventDefault(); // the browser's "save page"
      if (this.magic.heroId) this.toggle();
    }, { signal: gameSignal() });
  }

  get visible(): boolean { return !this.window.hidden; }

  toggle(show = this.window.hidden): void {
    this.window.hidden = !(show && this.magic.heroId !== 0);
    this.render();
  }

  render(): void {
    if (!this.magic.heroId) this.window.hidden = true; // the desktop disposes the hero dialogs below Summoned
    if (this.window.hidden) { this.rows = []; return; }
    this.rows = [];
    const rows = this.magic.heroMagics.map((m) => {
      const img = el("img", { alt: "" });
      void magicIcon(m.Icon * 2).then((url) => { if (url) img.src = url; });
      const shade = el("div", { class: "skill-shade" });
      this.rows.push({ m, shade });
      // MagicButton.Update: Exp x/NeedN below level 3
      const need = [m.Need1, m.Need2, m.Need3][m.Level];
      const keys = [0, 17, 18, 19, 20, 21, 22, 23, 24];
      const select = el("select", { title: "Key" }, ...keys.map((k) => el("option", { value: String(k), selected: k === m.Key }, keyName(k))));
      select.onchange = () => this.magic.assignHeroKey(m, Number(select.value));
      return el("div", { class: "magic-row" }, el("div", { class: "skill-slot" }, img, shade),
        el("div", { class: "magic-name" }, el("div", {}, m.Name), el("div", { class: "magic-meta" }, `Lv ${m.Level}${need ? `   Exp ${m.Experience}/${need}` : ""}   MP ${m.BaseCost + m.LevelCost * m.Level}`)),
        select);
    });
    this.list.replaceChildren(...(rows.length ? rows : [el("div", { class: "magic-meta" }, "Your hero has not learned any magic yet.")]));
  }

  /** Per frame: shade spells still cooling down (MagicButton.CoolDown). */
  tick(now: number): void {
    if (this.window.hidden) return;
    for (const { m, shade } of this.rows) {
      const left = Number.isFinite(m.castTime) && m.delay > 0 ? m.castTime + m.delay - now : 0;
      const frac = left > 0 ? Math.min(1, left / m.delay) : 0;
      shade.style.height = `${Math.round(frac * 100)}%`;
    }
  }
}
