// The user's poison timers from S.PoisonStatus (web-only; the desktop ignores the packet and only knows the S.Poisoned
// flags). Each S.PoisonStatus is the FULL current set with the time left per poison; the delayed-explosion countdown is
// one entry of type PoisonType.DelayedExplosion. The server sends it to the player only, when the set changes or a
// duration is refreshed, never per tick: timers count down locally from the arrival time. S.Poisoned (the flag set)
// drops entries whose flag went away, so a cleared poison never lingers until its timer runs out.
// game.ts feeds handle(); handle() never consumes.

import type { ClientPoisonStatus, ServerPacket } from "../protocol/server.ts";
import { Emitter } from "./emitter.ts";

export interface PoisonTimer {
  /** PoisonType flag value (one bit). */
  readonly type: number;
  /** PoisonTime-style payload (damage per tick, slow amount, ...), as the server sent it. */
  readonly value: number;
  /** `now()` time at which the poison ends (arrival time + TimeLeft). */
  readonly endsAt: number;
}

export class PoisonTimers {
  /** The set was replaced (S.PoisonStatus) or trimmed (S.Poisoned). */
  readonly changed = new Emitter<void>();
  private _list: PoisonTimer[] = [];

  constructor(private readonly now: () => number) {}

  /** The current poisons, in the server's order (expired ones are kept until the server or S.Poisoned drops them). */
  get list(): readonly PoisonTimer[] { return this._list; }

  /** The entry of one PoisonType flag, if active. */
  get(type: number): PoisonTimer | undefined {
    return this._list.find((t) => t.type === type);
  }

  /** Milliseconds left on a timer (0 when over). */
  timeLeft(t: PoisonTimer, now = this.now()): number {
    return Math.max(0, t.endsAt - now);
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "PoisonStatus": {
        const now = this.now();
        this._list = p.Poisons.map((e: ClientPoisonStatus) => ({ type: e.Type, value: e.Value, endsAt: now + Math.max(0, e.TimeLeft) }));
        this.changed.emit();
        break;
      }
      case "Poisoned": { // GameScene.Poisoned: User.Poison = flags; drop timers whose flag is gone
        const kept = this._list.filter((t) => (p.Poison & t.type) !== 0);
        if (kept.length !== this._list.length) { this._list = kept; this.changed.emit(); }
        break;
      }
    }
    return false;
  }

  /** Log-out / dispose: forget every timer. */
  clear(): void {
    if (this._list.length === 0) return;
    this._list = [];
    this.changed.emit();
  }
}
