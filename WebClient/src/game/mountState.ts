// The user's mount state: PlayerObject.MountTime (set by the user's S.MountUpdate) and MountDialog.CanRide / Ride, used
// by the Mount key (the game's key router) and the MountDialog Ride button. game.ts feeds handle() from GameScene.MountUpdate
// after the object update; the UI follows `updated` (MountDialog: MountType < 0 hides it, then RefreshDialog).

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirAction } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";

/** What the mount needs from the user's MapObj (GameScene.User). */
export interface MountUserState {
  id: number;
  /** PlayerObject.MountType (-1 = none) / RidingMount. */
  mountType: number;
  ridingMount: boolean;
  /** PlayerObject.CurrentAction (MirAction). */
  action: number;
  /** PlayerObject.Fishing (the MirItemCell.UseItem gate). */
  fishing: boolean;
}

export class MountState {
  /** The user's S.MountUpdate was applied (PlayerObject.MountUpdate); `mountType` = the packet's MountType. */
  readonly updated = new Emitter<{ mountType: number }>();
  /** PlayerObject.MountTime (now() of the user's last S.MountUpdate). */
  private mountTime = -Infinity;

  /**
   * @param user the user's mount fields (null before S.UserInformation).
   * @param now performance.now() clock.
   */
  constructor(private readonly send: (frame: Uint8Array) => void, readonly user: () => MountUserState | null,
    private readonly now: () => number) {}

  /** MountDialog.CanRide: a mount type, 500 ms after the last MountUpdate, standing (mounted or not). */
  canRide(): boolean {
    const u = this.user();
    if (!u || u.mountType < 0 || this.mountTime + 500 > this.now()) return false;
    return u.action === MirAction.Standing || u.action === MirAction.MountStanding;
  }

  /** MountDialog.Ride: the server toggles riding on the "@ride" command. */
  ride(): void {
    this.send(ClientPackets.Chat({ Message: "@ride" }));
  }

  /** GameScene.MountUpdate (the user's part) + PlayerObject.MountUpdate (MountTime). Never consumes the packet. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "MountUpdate") return false;
    const u = this.user();
    if (!u || p.ObjectID !== u.id) return false;
    this.mountTime = this.now();
    this.updated.emit({ mountType: p.MountType });
    return false;
  }
}
