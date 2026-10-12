// Doors: port of MapControl.Doors / GetDoor / Processdoors / OpenDoor / CheckDoorOpen (Client/MirScenes/GameScene.cs).
// A cell with DoorIndex > 0 belongs to a door; a door is created (closed) the first time a cell of it is drawn. While its
// state is not Closed, the front image is drawn at FrontImage + (ImageIndex + 1) * DoorOffset. Walking into a closed door
// asks the server to open it (C.Opendoor, at most every 4 s); S.Opendoor opens or closes it; an open door closes after 5 s.

import { DoorState } from "../protocol/enums.generated.ts";

export interface Door { index: number; state: number; imageIndex: number; lastTick: number }

export class Doors {
  private readonly doors = new Map<number, Door>();
  private doorTime = 0;

  clear(): void {
    this.doors.clear();
  }

  get(index: number): Door | undefined {
    return this.doors.get(index);
  }

  /** DrawFloor / DrawObjects: the door of a cell, created closed on first sight. */
  seen(index: number, now: number): Door {
    let d = this.doors.get(index);
    if (!d) { d = { index, state: DoorState.Closed, imageIndex: 0, lastTick: now }; this.doors.set(index, d); }
    return d;
  }

  /** Extra front-image frames for a cell of this door (0 while closed). */
  frameOffset(index: number, doorOffset: number, now: number): number {
    const d = this.seen(index, now);
    return d.state !== DoorState.Closed ? (d.imageIndex + 1) * doorOffset : 0;
  }

  /** MapControl.Processdoors. */
  process(now: number): void {
    for (const d of this.doors.values()) {
      if (d.state === DoorState.Opening || d.state === DoorState.Closing) {
        if (d.lastTick + 50 < now) {
          d.lastTick = now;
          d.imageIndex++;
          if (d.imageIndex === 1) { // "change the 1 if you want to animate doors opening/closing"
            d.imageIndex = 0;
            d.state = (d.state + 1) % 4;
          }
        }
      }
      if (d.state === DoorState.Open && d.lastTick + 5000 < now) {
        d.lastTick = now;
        d.state = DoorState.Closing;
      }
    }
  }

  /** MapControl.OpenDoor (S.Opendoor). */
  open(index: number, closed: boolean, now: number): void {
    const d = this.doors.get(index);
    if (!d) return;
    d.state = closed ? DoorState.Closing : d.state === DoorState.Open ? DoorState.Open : DoorState.Opening;
    d.imageIndex = 0;
    d.lastTick = now;
  }

  /** MapControl.CheckDoorOpen: true when the cell can be entered; a closed door is asked open (and refused for now). */
  checkOpen(doorIndex: number, now: number, send: (doorIndex: number) => void): boolean {
    if (doorIndex === 0) return true;
    const d = this.doors.get(doorIndex);
    if (!d) return false; // not drawn yet, so not on screen (and cannot be open)
    if (d.state === DoorState.Closed || d.state === DoorState.Closing) {
      if (now > this.doorTime) { this.doorTime = now + 4000; send(d.index); }
      return false;
    }
    if (d.state === DoorState.Open && d.lastTick + 4000 > now && now > this.doorTime) {
      this.doorTime = now + 4000;
      send(d.index);
    }
    return true;
  }
}
