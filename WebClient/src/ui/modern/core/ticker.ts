// The new UI's one clock (Review-Feature-Windows "One ticker", Performance.md T4-2): the UI's frame() drives it once a
// frame and windows subscribe only while shown (wm.ts UiWindow.onFrameShown / everyShown start and stop them), instead
// of each mock window keeping its own constructor setInterval. Frames stop in a background tab, and so does the UI work.
// One Ticker per UiRuntime (no module state); clear() drops every subscription on dispose.

export type FrameFn = (now: number) => void;

/** A ticker subscription that can be switched on and off (see dom.ts Switch). */
export interface TickSub { on(): void; off(): void; readonly running: boolean }

export class Ticker {
  private fns = new Set<FrameFn>();
  private snapshot: FrameFn[] | null = null;
  private last = 0;
  private dead = false;

  /** performance.now() of the last tick (0 before the first). */
  get now(): number { return this.last; }

  /** Runs `fn` every frame until the returned function is called. */
  subscribe(fn: FrameFn): () => void {
    if (this.dead) return () => {};
    this.fns.add(fn);
    this.snapshot = null;
    return () => { if (this.fns.delete(fn)) this.snapshot = null; };
  }

  /** A per-frame subscription that is off until on(). */
  frame(fn: FrameFn): TickSub {
    let stop: (() => void) | null = null;
    return {
      on: () => { if (!stop) stop = this.subscribe(fn); },
      off: () => { stop?.(); stop = null; },
      get running() { return stop !== null; },
    };
  }

  /** Runs `fn` at most every `ms` (on the first frame after on(), then each frame at least `ms` after the last run). Off
   *  until on(). Countdowns that show whole seconds use every(250) or every(1000). */
  every(ms: number, fn: FrameFn): TickSub {
    let due = 0;
    const sub = this.frame((now) => {
      if (now < due) return;
      due = now + ms;
      fn(now);
    });
    return {
      on: () => { if (!sub.running) { due = 0; sub.on(); } },
      off: () => sub.off(),
      get running() { return sub.running; },
    };
  }

  /** Called by the UI's frame() (one point per frame). A throwing subscriber is logged and does not stop the rest. */
  tick(now: number): void {
    this.last = now;
    if (!this.fns.size) return;
    const list = this.snapshot ??= [...this.fns];
    for (const fn of list) {
      if (!this.fns.has(fn)) continue; // removed by an earlier subscriber this frame
      try { fn(now); } catch (err) { console.error(err); }
    }
  }

  /** Drops every subscription; later subscribe() calls do nothing (dispose). */
  clear(): void { this.fns.clear(); this.snapshot = null; this.dead = true; }
}
