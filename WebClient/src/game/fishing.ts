// The user's fishing state (Client/MirScenes/Dialogs/FishingDialog.cs FishingStatusDialog + GameScene.FishingUpdate):
// ChancePercent / ProgressPercent, PlayerObject.FoundFish, whether the user is fishing (the status dialog shows while
// S.FishingUpdate says so), auto cast (needs a Reel in the rod; C.FishingChangeAutocast) and the "Esc exit" tick
// (bEscExit). game.ts feeds handle() from GameScene.FishingUpdate after the object update; the UI follows `updated`.

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { EquipmentSlot, FishingSlot } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { Emitter } from "./emitter.ts";

/** Globals.FishingRodShapes. */
export const FISHING_ROD_SHAPES = [49, 50];

/** UserObject.HasFishingRod (User.Weapon in FishingRodShapes): the equipped weapon, or null. */
export function fishingRod(inv: Inventory): UserItem | null {
  const rod = inv.equipment[EquipmentSlot.Weapon] ?? null;
  const info = rod ? inv.info(rod) : undefined;
  return rod && info && FISHING_ROD_SHAPES.includes(info.Shape) ? rod : null;
}

export class Fishing {
  private _chancePercent = 0;
  private _progressPercent = 0;
  private _foundFish = false;
  private _fishing = false;
  private _canAutoCast = false;
  private _autoCast = false;
  private _escExit = false;
  /** The user's S.FishingUpdate was applied. */
  readonly updated = new Emitter<void>();

  /** @param userId GameScene.User.ObjectID (null without a user). */
  constructor(private readonly inv: Inventory, private readonly send: (frame: Uint8Array) => void,
    private readonly userId: () => number | null) {}

  /** FishingStatusDialog.ChancePercent / ProgressPercent (S.FishingUpdate). */
  get chancePercent(): number { return this._chancePercent; }
  get progressPercent(): number { return this._progressPercent; }
  /** PlayerObject.FoundFish: the FishButton shows only while a fish bites. */
  get foundFish(): boolean { return this._foundFish; }
  /** S.FishingUpdate.Fishing: FishingStatusDialog Show (true) / Hide (false). */
  get fishing(): boolean { return this._fishing; }
  /** FishingStatusDialog.CanAutoCast (the rod has a Reel; refreshAutoCast). */
  get canAutoCast(): boolean { return this._canAutoCast; }
  get autoCast(): boolean { return this._autoCast; }
  /** bEscExit: Escape reels in and closes. */
  get escExit(): boolean { return this._escExit; }

  /** GameScene.FishingUpdate (the user's part): bars, FoundFish, fishing. Never consumes the packet. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "FishingUpdate") return false;
    const id = this.userId();
    if (id === null || p.ObjectID !== id) return false;
    this._progressPercent = p.ProgressPercent;
    this._chancePercent = p.ChancePercent;
    this._foundFish = p.FoundFish;
    this._fishing = p.Fishing;
    this.updated.emit();
    return false;
  }

  /** FishButton.Click / FishingStatusDialog.Cancel: C.FishingCast CastOut = false (reel in). */
  reelIn(): void {
    this.send(ClientPackets.FishingCast({ CastOut: false }));
  }

  /** ESCExitButton.Click. */
  toggleEscExit(): void {
    this._escExit = !this._escExit;
  }

  /** AutoCastButton.Click: only with a reel in the rod. Returns false when auto cast could not change. */
  toggleAutoCast(): boolean {
    this.refreshAutoCast();
    if (!this._canAutoCast) return false;
    this._autoCast = !this._autoCast;
    this.send(ClientPackets.FishingChangeAutocast({ AutoCast: this._autoCast }));
    return true;
  }

  /** FishingStatusDialog_BeforeDraw: auto cast needs a rod with a Reel; losing it turns auto cast off on the server.
   *  Returns whether a rod is held (the dialog only updates the auto cast controls then). */
  refreshAutoCast(): boolean {
    const rod = fishingRod(this.inv);
    if (rod) this._canAutoCast = rod.Slots.length >= 5 && !!rod.Slots[FishingSlot.Reel];
    if (this._autoCast && !this._canAutoCast) {
      this._autoCast = false;
      this.send(ClientPackets.FishingChangeAutocast({ AutoCast: false }));
    }
    return rod !== null;
  }
}
