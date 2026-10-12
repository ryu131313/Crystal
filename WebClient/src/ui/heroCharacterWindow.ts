// The hero's character window: GameScene.HeroDialog = CharacterDialog(MirGridType.HeroEquipment, Hero), status page only
// (the hero's equipment cells belong to the hero item windows). Ctrl+C toggles it (KeybindOptions.HeroEquipment; the
// desktop ignores the key while GameScene.Hero is null). The hero's stats are UserHeroObject.RefreshStats, which is
// UserObject.RefreshStats with the hero's CoreStats (S.HeroBaseStatsInfo), the hero's buffs (GetBuffDialog =
// HeroBuffsDialog) and no guild buffs (RefreshGuildBuffs returns unless this == User).

import { refreshStats, type UserStats } from "../game/stats.ts";
import type { ItemInfos } from "../game/items.ts";
import type { Hero } from "../game/hero.ts";
import { Stat } from "../protocol/enums.generated.ts";
import { CharacterWindow } from "./characterWindow.ts";
import { el } from "./screens.ts";

/** Stats are recomputed on hero changes and at most this often otherwise (item infos / magic levels arriving later). */
const REFRESH_MS = 1000;

export class HeroCharacterWindow {
  readonly window: CharacterWindow;
  /** HeroMenuPanel.HeroEquipmentButton's HUD stand-in (optional; the hero menu panel has its own button). */
  readonly hudButton = el("button", { title: "Hero character (Ctrl+C)", onclick: () => this.toggle() }, "Hero");
  private cache: UserStats | null = null;
  private dirty = true;
  private computedAt = -Infinity;

  /**
   * @param magics the hero's magics (UserHeroObject.Magics) for RefreshSkills; defaults to S.HeroInformation.Magics.
   * @param now performance.now() clock.
   */
  constructor(stage: HTMLElement, private readonly hero: Hero, private readonly infos: ItemInfos, private readonly now: () => number,
    private readonly magics: () => readonly { Spell: number; Level: number }[] = () => hero.info?.magics ?? []) {
    this.window = new CharacterWindow(stage, () => {
      const h = this.hero.info;
      if (!h) return null;
      return { name: h.name, cls: h.cls, level: h.level, hp: h.hp, mp: h.mp, experience: h.experience, maxExperience: h.maxExperience, stats: this.stats() };
    }, { title: "Hero", className: "hero-char-window", ctrlKey: true });
    hero.onChange(() => { this.dirty = true; });
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
    this.window.toggle(show);
  }

  /** Call every frame (like characterWindow.render()); hides itself once the hero is gone. */
  render(): void {
    this.window.render();
  }
}
