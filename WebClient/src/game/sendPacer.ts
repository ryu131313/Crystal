// A token bucket for UI item requests (bulk sell / repair / sort / move buttons). The server counts socket reads and
// IP-blocks a connection for 24 h past Settings.MaxPacket (50) reads in 5 s (Server/MirNetwork/MirConnection.cs
// ReceiveData, _dataCounter / _dataCounterReset), so a UI that sends several requests per click paces them: a burst of
// 5, then about 3 a second. The modern UI (src/ui/modern/) sends its item requests through this; the classic UI does
// not use it (it sends one request per click, like the desktop). DOM-free; timers via setTimeout.

import { Emitter } from "./emitter.ts";

export interface SendPacerOptions {
  /** Bucket size: frames that may go out at once. */
  readonly burst?: number;
  /** Sustained frames per second (the refill rate). */
  readonly perSecond?: number;
}

export class SendPacer {
  /** The number of queued frames after it changed. */
  readonly queuedChanged = new Emitter<number>();
  private readonly burst: number;
  private readonly interval: number;
  private tokens: number;
  private last: number;
  private readonly queue: Uint8Array[] = [];
  private timer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;

  constructor(private readonly sendFrame: (frame: Uint8Array) => void, private readonly now: () => number, opts: SendPacerOptions = {}) {
    this.burst = Math.max(1, opts.burst ?? 5);
    this.interval = 1000 / Math.max(0.1, opts.perSecond ?? 3);
    this.tokens = this.burst;
    this.last = now();
  }

  /** Frames waiting for a token. */
  get queued(): number {
    return this.queue.length;
  }

  /** Sends now when a token is free and nothing is queued, else queues the frame (sent in order as tokens refill). */
  send(frame: Uint8Array): void {
    if (this.disposed) return;
    if (this.queue.length === 0 && this.take()) { this.sendFrame(frame); return; }
    this.queue.push(frame);
    this.queuedChanged.emit(this.queue.length);
    this.schedule();
  }

  /** Sends now only when a token is free and nothing is queued; false = not sent (nothing queued). */
  trySend(frame: Uint8Array): boolean {
    if (this.disposed || this.queue.length > 0 || !this.take()) return false;
    this.sendFrame(frame);
    return true;
  }

  /** Drops every queued frame (frames already sent stay sent). */
  cancel(): void {
    if (this.timer !== null) { clearTimeout(this.timer); this.timer = null; }
    if (this.queue.length === 0) return;
    this.queue.length = 0;
    this.queuedChanged.emit(0);
  }

  /** Cancels the queue and stops for good (the game scene's dispose). */
  dispose(): void {
    this.cancel();
    this.disposed = true;
  }

  private refill(): void {
    const t = this.now();
    const elapsed = t - this.last;
    if (elapsed <= 0) return;
    const gained = elapsed / this.interval;
    this.tokens = Math.min(this.burst, this.tokens + gained);
    this.last = t;
  }

  private take(): boolean {
    this.refill();
    if (this.tokens < 1) return false;
    this.tokens -= 1;
    return true;
  }

  private schedule(): void {
    if (this.timer !== null || this.queue.length === 0) return;
    const wait = Math.max(0, Math.ceil((1 - this.tokens) * this.interval));
    this.timer = setTimeout(() => {
      this.timer = null;
      this.drain();
    }, wait);
  }

  private drain(): void {
    if (this.disposed) return;
    let sent = false;
    while (this.queue.length > 0 && this.take()) {
      this.sendFrame(this.queue.shift()!);
      sent = true;
    }
    if (sent) this.queuedChanged.emit(this.queue.length);
    this.schedule();
  }
}
