// The hero's computed stats: UserHeroObject.RefreshStats, which is UserObject.RefreshStats with the hero's CoreStats
// (S.HeroBaseStatsInfo), the hero's buffs (GetBuffDialog = HeroBuffsDialog) and no guild buffs (RefreshGuildBuffs
// returns unless this == User). Cached: recomputed after invalidate() / a hero change, and at most once a second
// otherwise (item infos / magic levels arriving later). Read by the hero character window, the hero info panel
// (Hero.Stats[Stat.HP] / [Stat.MP]) and MirItemCell.CanWearItem for the hero's equipment.

import { refreshStats, type UserStats } from "./stats.ts";
import type { ItemInfos } from "./items.ts";
import type { Hero } from "./hero.ts";
import { Stat } from "../protocol/enums.generated.ts";

/** Stats are recomputed on hero changes and at most this often otherwise (item infos / magic levels arriving later). */
const REFRESH_MS = 1000;

export class HeroStats {
  private cache: UserStats | null = null;
  private dirty = true;
  private computedAt = -Infinity;

  /**
   * @param now performance.now() clock.
   * @param magics the hero's magics (UserHeroObject.Magics) for RefreshSkills; defaults to S.HeroInformation.Magics.
   */
  constructor(private readonly hero: Hero, private readonly infos: ItemInfos, private readonly now: () => number,
    private readonly magics: () => readonly { Spell: number; Level: number }[] = () => hero.info?.magics ?? []) {
    hero.onChange(() => { this.dirty = true; });
  }

  /** Marks the stats stale (hero item changes, GameScene.GameDataChanged). */
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
}
