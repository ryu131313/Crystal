// The user's intelligent creatures (collectible pets that follow the player and pick up items): UserObject
// IntelligentCreatures / SummonedCreatureType / CreatureSummoned / PearlCount, kept in step with UserInformation
// (UserObject.Load) and UpdateIntelligentCreatureList / NewIntelligentCreature (GameScene). game.ts feeds handle(); the
// dialog side of those handlers (rename prompt, hiding the dialog, the rename button) lives in the UI, which subscribes
// to `changed` (classic: src/ui/creatureWindow.ts).
// Also IntelligentCreatureItemFilter.SetItemFilter (Shared/Data/IntelligentCreatureData.cs).

import type { ServerPacket, ClientIntelligentCreature, IntelligentCreatureItemFilter } from "../protocol/server.ts";
import { IntelligentCreatureType, ItemGrade } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";

/** What a handled packet changed, for the dialog. */
export type CreatureChange =
  | { kind: "load" }                 // UserInformation
  | { kind: "replaced" }             // UpdateIntelligentCreatureList with a new count (list rebuilt)
  | { kind: "updated" }              // UpdateIntelligentCreatureList with the same count (entries replaced in place)
  | { kind: "new"; index: number }   // NewIntelligentCreature (appended at index)
  | { kind: "enableRename" };        // IntelligentCreatureEnableRename

export class Creatures {
  /** UserObject.IntelligentCreatures. */
  list: ClientIntelligentCreature[] = [];
  /** UserObject.SummonedCreatureType (IntelligentCreatureType, None = 99). */
  summonedType: number = IntelligentCreatureType.None;
  /** UserObject.CreatureSummoned. */
  summoned = false;
  /** UserObject.PearlCount (only UpdateIntelligentCreatureList carries it). */
  pearlCount = 0;
  /** What a handled packet changed (emitted after the model update), for the dialog. */
  readonly changed = new Emitter<CreatureChange>();

  /** Server packets. True = consumed (the creature packets; UserInformation is read by others too, so false). */
  handle(p: ServerPacket): boolean {
    const change = this.apply(p);
    if (!change) return false;
    this.changed.emit(change);
    return change.kind !== "load";
  }

  /** The model half of the GameScene handlers; returns what changed, or null when the packet is not a creature packet. */
  private apply(p: ServerPacket): CreatureChange | null {
    switch (p.type) {
      case "UserInformation": // UserObject.Load
        this.list = p.IntelligentCreatures;
        this.summonedType = p.SummonedCreatureType;
        this.summoned = p.CreatureSummoned;
        return { kind: "load" };
      case "UpdateIntelligentCreatureList": // GameScene.UpdateIntelligentCreatureList
        this.summoned = p.CreatureSummoned;
        this.summonedType = p.SummonedCreatureType;
        this.pearlCount = p.PearlCount;
        if (p.CreatureList.length !== this.list.length) {
          this.list = p.CreatureList.slice();
          return { kind: "replaced" };
        }
        for (let i = 0; i < p.CreatureList.length; i++) this.list[i] = p.CreatureList[i];
        return { kind: "updated" };
      case "NewIntelligentCreature": // GameScene.NewIntelligentCreature (model half)
        this.list.push(p.Creature);
        return { kind: "new", index: this.list.length - 1 };
      case "IntelligentCreatureEnableRename":
        return { kind: "enableRename" };
      default:
        return null;
    }
  }

  /** IntelligentCreatureDialog.GetCreatureFromSlot: the list index whose SlotIndex is `slot`, or -1. */
  fromSlot(slot: number): number {
    for (let i = 0; i < this.list.length; i++) if (this.list[i].SlotIndex === slot) return i;
    return -1;
  }
}

/** new IntelligentCreatureItemFilter() (pick up everything, any grade). */
export function defaultItemFilter(): IntelligentCreatureItemFilter {
  return {
    PetPickupAll: true, PetPickupGold: false, PetPickupWeapons: false, PetPickupArmours: false, PetPickupHelmets: false,
    PetPickupBoots: false, PetPickupBelts: false, PetPickupAccessories: false, PetPickupOthers: false, PickupGrade: ItemGrade.None,
  };
}

/** IntelligentCreatureItemFilter.SetItemFilter (exact port): 0 = all items, 1 gold, 2 weapons, 3 armours, 4 helmets,
 *  5 boots, 6 belts, 7 jewelry, 8 others. Every type ticked collapses to "all"; none ticked falls back to "all". */
export function setItemFilter(f: IntelligentCreatureItemFilter, idx: number): void {
  switch (idx) {
    case 0: // all items
      f.PetPickupAll = true;
      f.PetPickupGold = false;
      f.PetPickupWeapons = false;
      f.PetPickupArmours = false;
      f.PetPickupHelmets = false;
      f.PetPickupBoots = false;
      f.PetPickupBelts = false;
      f.PetPickupAccessories = false;
      f.PetPickupOthers = false;
      break;
    case 1: f.PetPickupAll = false; f.PetPickupGold = !f.PetPickupGold; break;
    case 2: f.PetPickupAll = false; f.PetPickupWeapons = !f.PetPickupWeapons; break;
    case 3: f.PetPickupAll = false; f.PetPickupArmours = !f.PetPickupArmours; break;
    case 4: f.PetPickupAll = false; f.PetPickupHelmets = !f.PetPickupHelmets; break;
    case 5: f.PetPickupAll = false; f.PetPickupBoots = !f.PetPickupBoots; break;
    case 6: f.PetPickupAll = false; f.PetPickupBelts = !f.PetPickupBelts; break;
    case 7: f.PetPickupAll = false; f.PetPickupAccessories = !f.PetPickupAccessories; break;
    case 8: f.PetPickupAll = false; f.PetPickupOthers = !f.PetPickupOthers; break;
  }
  if (f.PetPickupGold && f.PetPickupWeapons && f.PetPickupArmours && f.PetPickupHelmets && f.PetPickupBoots && f.PetPickupBelts && f.PetPickupAccessories && f.PetPickupOthers) {
    f.PetPickupAll = true;
    f.PetPickupGold = false;
    f.PetPickupWeapons = false;
    f.PetPickupArmours = false;
    f.PetPickupHelmets = false;
    f.PetPickupBoots = false;
    f.PetPickupBelts = false;
    f.PetPickupAccessories = false;
    f.PetPickupOthers = false;
  } else if (!f.PetPickupGold && !f.PetPickupWeapons && !f.PetPickupArmours && !f.PetPickupHelmets && !f.PetPickupBoots && !f.PetPickupBelts && !f.PetPickupAccessories && !f.PetPickupOthers) {
    f.PetPickupAll = true;
  }
}

/** The filter's tick state in IntelligentCreatureOptionsDialog order (RefreshFilter). */
export function itemFilterFlags(f: IntelligentCreatureItemFilter): boolean[] {
  return [f.PetPickupAll, f.PetPickupGold, f.PetPickupWeapons, f.PetPickupArmours, f.PetPickupHelmets, f.PetPickupBoots, f.PetPickupBelts, f.PetPickupAccessories, f.PetPickupOthers];
}

/** CreatureButton.SetCreatureFrames: the Prguse2 animation of the selected creature's picture
 *  (default loop, and the "extra" loop played once every 8 s). */
export interface CreatureFrames { defIdx: number; defCount: number; defDelay: number; exIdx: number; exCount: number; exDelay: number }

const FRAMES: Record<number, CreatureFrames> = {
  [IntelligentCreatureType.BabyPig]: { defIdx: 540, defCount: 6, defDelay: 200, exIdx: 550, exCount: 5, exDelay: 300 },
  [IntelligentCreatureType.Chick]: { defIdx: 570, defCount: 4, defDelay: 350, exIdx: 580, exCount: 10, exDelay: 200 },
  [IntelligentCreatureType.Kitten]: { defIdx: 600, defCount: 6, defDelay: 250, exIdx: 610, exCount: 10, exDelay: 200 },
  [IntelligentCreatureType.BabySkeleton]: { defIdx: 630, defCount: 11, defDelay: 200, exIdx: 650, exCount: 7, exDelay: 250 },
  [IntelligentCreatureType.Baekdon]: { defIdx: 660, defCount: 6, defDelay: 250, exIdx: 670, exCount: 8, exDelay: 250 },
  [IntelligentCreatureType.Wimaen]: { defIdx: 690, defCount: 4, defDelay: 350, exIdx: 700, exCount: 6, exDelay: 300 },
  [IntelligentCreatureType.BlackKitten]: { defIdx: 720, defCount: 6, defDelay: 250, exIdx: 730, exCount: 10, exDelay: 200 },
  [IntelligentCreatureType.BabyDragon]: { defIdx: 750, defCount: 6, defDelay: 300, exIdx: 760, exCount: 7, exDelay: 250 },
  [IntelligentCreatureType.OlympicFlame]: { defIdx: 780, defCount: 6, defDelay: 300, exIdx: 790, exCount: 10, exDelay: 200 },
  [IntelligentCreatureType.BabySnowMan]: { defIdx: 810, defCount: 6, defDelay: 300, exIdx: 820, exCount: 6, exDelay: 300 },
  [IntelligentCreatureType.Frog]: { defIdx: 840, defCount: 6, defDelay: 300, exIdx: 850, exCount: 6, exDelay: 300 },
  [IntelligentCreatureType.BabyMonkey]: { defIdx: 870, defCount: 6, defDelay: 300, exIdx: 880, exCount: 9, exDelay: 300 },
  [IntelligentCreatureType.AngryBird]: { defIdx: 1400, defCount: 12, defDelay: 300, exIdx: 1332, exCount: 12, exDelay: 300 },
  [IntelligentCreatureType.Foxey]: { defIdx: 1430, defCount: 9, defDelay: 300, exIdx: 1439, exCount: 8, exDelay: 300 },
  [IntelligentCreatureType.MedicalRat]: { defIdx: 1550, defCount: 8, defDelay: 300, exIdx: 1560, exCount: 16, exDelay: 300 },
  [IntelligentCreatureType.None]: { defIdx: 539, defCount: 1, defDelay: 0, exIdx: 539, exCount: 1, exDelay: 0 },
};
/** CreatureButton field initialisers (a type the switch does not list keeps them). */
const FRAMES_DEFAULT: CreatureFrames = { defIdx: 540, defCount: 6, defDelay: 400, exIdx: 550, exCount: 5, exDelay: 400 };

export function creatureFrames(petType: number): CreatureFrames {
  return FRAMES[petType] ?? FRAMES_DEFAULT;
}

/** IntelligentCreatureDialog.blackstoneProduceTime: 3 hours in seconds. */
export const BLACKSTONE_PRODUCE_TIME = 10800;

/** Globals.MinCharacterNameLength / MaxCharacterNameLength (IntelligentCreatureDialog.CreatureNameReg). */
export const CREATURE_NAME = /^[A-Za-z0-9]{3,15}$/;
