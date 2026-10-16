// The hero's character window: GameScene.HeroDialog = CharacterDialog(MirGridType.HeroEquipment, Hero) with the desktop
// artwork (characterWindow.ts: Title 504, the tabs, the CharacterPage paperdoll from the hero's gender / hair / equipment
// with the hero's equipment cells, the two stat pages). Ctrl+C toggles it (KeybindOptions.HeroEquipment; the desktop
// ignores the key while GameScene.Hero is null); Escape hides it (GameScene Closeall: HeroDialog?.Hide()).
// The hero's equipment cells are persistent inventory-window cells on grid "heroEq" (shared pick; the clicks go to
// HeroInventoryWindow.click). The skill page is the hero skill window (heroMagicWindow.ts): CharacterWindow's own skill
// page lists only the user's magics, so its Skill tab hands over to that window (and its tabs hand back).
// The hero's stats (UserHeroObject.RefreshStats) come from the HeroStats model (src/game/heroStats.ts).

import type { Hero } from "../game/hero.ts";
import type { HeroStats } from "../game/heroStats.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { CharacterWindow } from "./characterWindow.ts";
import { userWindows } from "./userRefs.ts";
import { heroDialogs } from "./heroMagicWindow.ts";

type Page = "character" | "status" | "state" | "skill";

export class HeroCharacterWindow {
  readonly window: CharacterWindow;
  /** The hero's equipment cells (CharacterDialog.Grid with MirGridType.HeroEquipment), created once. */
  private readonly equipCells = new Map<number, HTMLElement>();

  /** @param heroStats UserHeroObject.Stats (the cached RefreshStats model). */
  constructor(stage: HTMLElement, private readonly hero: Hero, heroStats: HeroStats) {
    this.window = new CharacterWindow(stage, () => {
      const h = this.hero.info;
      if (!h) return null;
      return {
        name: h.name, cls: h.cls, level: h.level, hp: h.hp, mp: h.mp, experience: h.experience, maxExperience: h.maxExperience, stats: heroStats.stats(),
        gender: h.gender, hair: h.hair, guildName: "", guildRank: "", equipment: h.equipment,
      };
    }, { title: "Hero", ctrlKey: true, equipCell: (slot) => this.equipCell(slot) });
    heroDialogs.character = this;
    const signal = gameSignal();
    // GameScene Closeall: HeroDialog?.Hide()
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Closeall) && this.window.visible) this.window.toggle(false);
    }, { signal });
    signal.addEventListener("abort", () => { if (heroDialogs.character === this) heroDialogs.character = null; });
  }

  /** A persistent "heroEq" cell for the paperdoll (refreshed by every inventory render). */
  private equipCell(slot: number): HTMLElement {
    let c = this.equipCells.get(slot);
    if (!c) {
      c = userWindows.inventory?.boundCell("heroEq", slot) ?? document.createElement("div");
      this.equipCells.set(slot, c);
    }
    return c;
  }

  get visible(): boolean {
    return this.window.visible;
  }

  /** KeybindOptions.HeroEquipment / HeroMenuPanel.HeroEquipmentButton: toggles while the hero exists. */
  toggle(show = !this.window.visible): void {
    if (show && !this.hero.info) return;
    if (show) heroDialogs.skills?.toggle(false);
    this.window.toggle(show);
  }

  /** The hero skill window's Character / Status / State tabs: show this window on `page`. */
  openPage(page: Exclude<Page, "skill">): void {
    if (!this.hero.info) return;
    this.window.toggle(true);
    this.window.showPage(page);
  }

  /** Call every frame (like characterWindow.render()); hides itself once the hero is gone. */
  render(): void {
    if (this.window.visible) {
      // the Skill tab: the hero's skill page is heroMagicWindow.ts
      if (this.window.currentPage === "skill") {
        this.window.showPage("character");
        this.window.toggle(false);
        heroDialogs.skills?.toggle(true);
        return;
      }
      // Ctrl+C while the skill window is up: one HeroDialog, one page
      if (heroDialogs.skills?.visible) heroDialogs.skills.toggle(false);
    }
    this.window.render();
  }
}
