// Web-only auto potion (no desktop or server counterpart; the player-side twin of HeroObject.ProcessAutoPot). Below the
// HP / MP percent set in Options it drinks the best HP / MP potion in the bag or belt through the normal
// Inventory.use path (C.UseItem, the 300 ms UseItemTime gate, the belt refill), so the server sees an ordinary click.
//  - "Best" = the potion (Shape 0 normal or 1 sun) with the largest Stats[HP] (or MP); a tie takes the one that wastes
//    less of the other stat, then the smaller stack (frees a cell sooner).
//  - Like the hero (PotHealthAmount <= 0) it never stacks normal potions: HumanObject.ProcessRegen drains the server's
//    PotHealthAmount / PotManaAmount by 5 + Level / 10 every PotDelay (200 ms), and every successful Shape 0 S.UseItem
//    (the player's own clicks too) adds to this client copy, which drains at the same rate. Sun potions heal at once.
//  - AutoPotDelay: one try a second per pool, as HeroObject.
//  - A refused S.UseItem (CanUseItem: class / level / NoDrug map) skips that ItemIndex until the map, level or life
//    changes, so a NoDrug map costs one server hint per potion kind, not one a second.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ItemType, MirGridType, Stat } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { gameSettings } from "./settings.ts";

/** HeroObject.AutoPotDelay. */
const AUTO_POT_DELAY = 1000;
/** HumanObject.PotDelay. */
const POT_DELAY = 200;
const NORMAL_POTION = 0, SUN_POTION = 1;

export interface AutoPotState {
  hp: number; mp: number; maxHp: number; maxMp: number; level: number; dead: boolean;
}

type Pool = "hp" | "mp";

export class AutoPot {
  private readonly inventory: Inventory;
  /** Client copy of PotHealthAmount / PotManaAmount, valid at `potAt`. */
  private potHp = 0;
  private potMp = 0;
  private potAt = 0;
  private readonly tryAt: Record<Pool, number> = { hp: 0, mp: 0 };
  /** ItemIndexes the server refused since the last map / level / life change. */
  private readonly refused = new Set<number>();

  constructor(inventory: Inventory) {
    this.inventory = inventory;
  }

  /** Feed every server packet BEFORE Inventory.handle (it still holds the used item then). */
  handle(p: ServerPacket, now: number, level: number): void {
    switch (p.type) {
      case "UseItem": {
        if (p.Grid !== MirGridType.Inventory) break;
        const item = this.inventory.inventory.find((i) => i?.UniqueID === p.UniqueID);
        const info = item ? this.inventory.info(item) : undefined;
        if (!info || info.Type !== ItemType.Potion) break;
        if (!p.Success) { this.refused.add(info.Index); break; }
        if (info.Shape !== NORMAL_POTION) break;
        this.drain(now, level);
        this.potHp = Math.min(0xffff, this.potHp + (info.Stats[Stat.HP] ?? 0));
        this.potMp = Math.min(0xffff, this.potMp + (info.Stats[Stat.MP] ?? 0));
        break;
      }
      case "Death": case "Revived": // HumanObject.Die clears the pools
        this.potHp = this.potMp = 0;
        this.refused.clear();
        break;
      case "MapChanged": case "LevelChanged":
        this.refused.clear();
        break;
    }
  }

  /** Once a frame from game.ts step(). */
  process(now: number, s: AutoPotState): void {
    if (!gameSettings.AutoPot || s.dead) return;
    this.drain(now, s.level);
    this.check("hp", now, s, s.hp, s.maxHp, gameSettings.AutoPotHP, this.potHp);
    this.check("mp", now, s, s.mp, s.maxMp, gameSettings.AutoPotMP, this.potMp);
  }

  private check(pool: Pool, now: number, s: AutoPotState, cur: number, max: number, percent: number, pending: number): void {
    if (percent <= 0 || max <= 0 || pending > 0 || now < this.tryAt[pool]) return;
    if (Math.trunc((cur / max) * 100) >= percent) return; // HeroObject: PercentHealth < AutoHPPercent
    this.tryAt[pool] = now + AUTO_POT_DELAY;
    const index = this.best(pool, s.level);
    if (index >= 0) this.inventory.use(index, now);
  }

  /** The inventory index (bag or belt) of the best usable potion for `pool`, or -1. */
  private best(pool: Pool, level: number): number {
    const main = pool === "hp" ? Stat.HP : Stat.MP, other = pool === "hp" ? Stat.MP : Stat.HP;
    let bestIndex = -1, bestMain = 0, bestOther = 0, bestCount = 0;
    const list: readonly (UserItem | null)[] = this.inventory.inventory;
    for (let i = 0; i < list.length; i++) {
      const item = list[i];
      if (!item || this.inventory.isLocked(item)) continue;
      const info = this.inventory.info(item);
      if (!info || info.Type !== ItemType.Potion || (info.Shape !== NORMAL_POTION && info.Shape !== SUN_POTION)) continue;
      if (this.refused.has(info.Index)) continue;
      if (info.RequiredType === 0 && level < info.RequiredAmount) continue; // RequiredType.Level
      const m = info.Stats[main] ?? 0, o = info.Stats[other] ?? 0;
      if (m <= 0) continue;
      if (m > bestMain || (m === bestMain && (o < bestOther || (o === bestOther && item.Count < bestCount)))) {
        bestIndex = i; bestMain = m; bestOther = o; bestCount = item.Count;
      }
    }
    return bestIndex;
  }

  /** HumanObject.ProcessRegen's pot drain, caught up to `now`. */
  private drain(now: number, level: number): void {
    const ticks = Math.floor((now - this.potAt) / POT_DELAY);
    if (ticks <= 0) return;
    this.potAt += ticks * POT_DELAY;
    if (this.potHp <= 0 && this.potMp <= 0) { this.potAt = now; return; }
    const perTick = 5 + Math.trunc(level / 10);
    this.potHp = Math.max(0, this.potHp - ticks * perTick);
    this.potMp = Math.max(0, this.potMp - ticks * perTick);
  }
}
