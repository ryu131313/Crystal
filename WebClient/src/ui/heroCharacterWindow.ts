// The hero's character window: GameScene.HeroDialog = CharacterDialog(MirGridType.HeroEquipment, Hero) with the desktop
// artwork (characterWindow.ts: Title 504, the tabs, the CharacterPage paperdoll from the hero's gender / hair / equipment
// with the hero's equipment cells, the two stat pages). Ctrl+C toggles it (KeybindOptions.HeroEquipment; the desktop
// ignores the key while GameScene.Hero is null); Escape hides it (GameScene Closeall: HeroDialog?.Hide()).
// The hero's equipment cells are persistent inventory-window cells on grid "heroEq" (shared pick; the clicks go to
// HeroInventoryWindow.click). The skill page is the hero skill window (heroMagicWindow.ts): CharacterWindow's own skill
// page lists only the user's magics, so its Skill tab hands over to that window (and its tabs hand back).
// The hero's stats are UserHeroObject.RefreshStats, which is UserObject.RefreshStats with the hero's CoreStats
// (S.HeroBaseStatsInfo), the hero's buffs (GetBuffDialog = HeroBuffsDialog) and no guild buffs (RefreshGuildBuffs
// returns unless this == User).

import { refreshStats, type UserStats } from "../game/stats.ts";
import type { ItemInfos } from "../game/items.ts";
import type { Hero } from "../game/hero.ts";
import { Stat } from "../protocol/enums.generated.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { CharacterWindow } from "./characterWindow.ts";
import { userWindows } from "./userRefs.ts";
import { heroDialogs } from "./heroMagicWindow.ts";

/** Stats are recomputed on hero changes and at most this often otherwise (item infos / magic levels arriving later). */
const REFRESH_MS = 1000;

type Page = "character" | "status" | "state" | "skill";

export class HeroCharacterWindow {
  readonly window: CharacterWindow;
  private cache: UserStats | null = null;
  private dirty = true;
  private computedAt = -Infinity;
  /** The hero's equipment cells (CharacterDialog.Grid with MirGridType.HeroEquipment), created once. */
  private readonly equipCells = new Map<number, HTMLElement>();

  /**
   * @param magics the hero's magics (UserHeroObject.Magics) for RefreshSkills; defaults to S.HeroInformation.Magics.
   * @param now performance.now() clock.
   */
  constructor(stage: HTMLElement, private readonly hero: Hero, private readonly infos: ItemInfos, private readonly now: () => number,
    private readonly magics: () => readonly { Spell: number; Level: number }[] = () => hero.info?.magics ?? []) {
    this.window = new CharacterWindow(stage, () => {
      const h = this.hero.info;
      if (!h) return null;
      return {
        name: h.name, cls: h.cls, level: h.level, hp: h.hp, mp: h.mp, experience: h.experience, maxExperience: h.maxExperience, stats: this.stats(),
        gender: h.gender, hair: h.hair, guildName: "", guildRank: "", equipment: h.equipment,
      };
    }, { title: "Hero", ctrlKey: true, equipCell: (slot) => this.equipCell(slot) });
    hero.onChange(() => { this.dirty = true; });
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

  /** Marks the stats stale (call where the user's stats are marked dirty, e.g. a new ItemInfo or a hero magic change). */
  invalidate(): void {
    this.dirty = true;
  }

  /** UserHeroObject.RefreshStats, cached; null while there is no hero. */
  stats(): UserStats | null {
    const h = this.hero.info;
    if (!h) { this.cache = null; return null; }
    const t = this.now();
    if (this.dirty || !this.cache || t - this.computedAt >= REFRESH_MS) {
      this.dirty = false;
      this.computedAt = t;
      this.cache = refreshStats({
        cls: h.cls, level: h.level, inventory: h.inventory, equipment: h.equipment, infos: this.infos, magics: this.magics(),
        buffs: this.hero.buffs, guildBuffStats: null, baseStats: this.hero.baseStats, hp: h.hp,
      });
    }
    return this.cache;
  }

  /** Hero.Stats[Stat.HP] / [Stat.MP] (HeroInfoPanel labels and bars). */
  maxHp(): number { return this.stats()?.stats[Stat.HP] ?? 0; }
  maxMp(): number { return this.stats()?.stats[Stat.MP] ?? 0; }

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
