// The browser end of one WebRelay session: a WebSocket carrying one Crystal frame per binary message.
// Handles the protocol chores the desktop Network class does (C.ClientVersion on S.Connected, C.KeepAlive when idle)
// and hands every decoded, ported packet to `onPacket`. Unported ids are counted and dropped.

import { decodeServerPacket, decodeServerPacketAsync, isCompressedFrame, type ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ServerPacketIdsName } from "../protocol/enums.generated.ts";

export type SessionState = "connecting" | "open" | "closed";

export class Session {
  private ws: WebSocket;
  private lastSend = 0;
  private keepAliveTimer: number;
  state: SessionState = "connecting";
  onPacket: (p: ServerPacket) => void = () => {};
  onClose: (reason: string) => void = () => {};
  readonly unported = new Map<string, number>();
  readonly t0 = performance.now();

  constructor(url = Session.defaultUrl()) {
    this.ws = new WebSocket(url);
    this.ws.binaryType = "arraybuffer";
    this.ws.onopen = () => { this.state = "open"; };
    this.ws.onmessage = (e) => this.receive(new Uint8Array(e.data as ArrayBuffer));
    this.ws.onclose = (e) => {
      this.state = "closed";
      clearInterval(this.keepAliveTimer);
      this.onClose(e.reason || (e.code === 1006 ? "connection lost" : `closed (${e.code})`));
    };
    // Server [Network] TimeOut is 10 s by default; the desktop sends C.KeepAlive when it has sent nothing for a while.
    this.keepAliveTimer = window.setInterval(() => {
      if (this.state === "open" && performance.now() - this.lastSend > 4000) this.send(ClientPackets.KeepAlive(this.now()));
    }, 1000);
  }

  static defaultUrl(): string {
    return `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`;
  }

  /** CMain.Time equivalent: ms since the session started. */
  now(): bigint {
    return BigInt(Math.floor(performance.now() - this.t0));
  }

  send(frame: Uint8Array): void {
    if (this.state !== "open") return;
    this.ws.send(frame as Uint8Array<ArrayBuffer>);
    this.lastSend = performance.now();
  }

  close(): void {
    this.ws.close(1000, "bye");
  }

  /** Frames that arrived while a compressed packet was inflating: delivered after it, in order. */
  private readonly backlog: Uint8Array[] = [];
  private inflating = false;

  private receive(frame: Uint8Array): void {
    if (this.inflating) { this.backlog.push(frame); return; }
    if (isCompressedFrame(frame)) {
      this.inflating = true;
      decodeServerPacketAsync(frame)
        .then((p) => { if (p) this.deliver(p); }, (e) => console.error("[session] compressed packet failed:", e))
        .finally(() => {
          this.inflating = false;
          const queued = this.backlog.splice(0);
          for (const f of queued) this.receive(f);
        });
      return;
    }
    let p: ServerPacket | null;
    try {
      p = decodeServerPacket(frame);
    } catch (e) {
      const id = new DataView(frame.buffer, frame.byteOffset).getInt16(2, true);
      console.error(`[session] decode failed for ${ServerPacketIdsName[id] ?? id}:`, e);
      return;
    }
    if (!p) {
      const id = frame.length >= 4 ? new DataView(frame.buffer, frame.byteOffset).getInt16(2, true) : -1;
      const name = ServerPacketIdsName[id] ?? String(id);
      this.unported.set(name, (this.unported.get(name) ?? 0) + 1);
      return;
    }
    this.deliver(p);
  }

  private deliver(p: ServerPacket): void {
    if (p.type === "Connected") this.send(ClientPackets.ClientVersion());
    this.onPacket(p);
  }
}
