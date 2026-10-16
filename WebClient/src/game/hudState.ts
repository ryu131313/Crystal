// HUD state: the packet-driven half of the small GameScene HUD parts, without the DOM (each UI draws it):
//   GameScene.OutputMessage / ProcessOuput  up to 10 lines, each gone after 5 s (S.SendOutputMessage and the game's own)
//   NoticeDialog       S.UpdateNotice (the `notice` event; NoticeDialog.Update ignores an empty message)
//   TimerDialog        S.SetTimer / S.ExpireTimer and TimerDialog.Process: the nearest timer counting down
//   CompassDialog      S.SetCompass (CompassDialog.SetPoint; Point.Empty clears it)
//   RollDialog         S.Roll (RollDialog.Setup / Roll / ReturnResult: the result calls the NPC page, `rollDone`)
//   ChatNoticeDialog   ChatDialog.ReceiveChat for Announcement lines (ChatLog.announcement), shown 10 s
//   S.OpenBrowser      the desktop opens the default browser; `openBrowser` carries http / https links only
//   S.NPCRequestInput  MirInputBox -> C.NPCConfirmInput (`requestInput`, answered with confirmInput)
// handle() consumes exactly these packets (GameScene.ProcessPacket returns after them). process(now) is the per-frame
// part (ProcessOuput, TimerDialog.Process, the chat notice timeout, the roll animation); the classic HudExtras runs it
// from its update().

import type { ServerPacket } from "../protocol/server.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { ClientPackets } from "../protocol/client.ts";
import { OutputMessageType } from "../protocol/enums.generated.ts";
import { Emitter } from "./emitter.ts";
import type { ChatLog } from "./chatLog.ts";

/** One GameScene.OutputMessages entry (OutputMessage: Message, ExpireTime, Type). */
export interface OutputLine { readonly message: string; readonly expire: number; readonly type: number }

/** One TimerDialog.ActiveTimers entry (ClientTimer: Key, Type, RelativeTime in seconds of CMain.Time, Refresh). */
interface Timer { key: string; type: number; relative: number; refresh: boolean }

/** What TimerDialog shows: its CurrentTimer's type and Counter (seconds left, may be below 0 right after an expiry). */
export interface TimerView { readonly counter: number; readonly type: number }

/** RollDialog state (Type 0 = die, 1 = yut). `frame` counts the animation frames shown so far (0 = the Setup picture). */
export interface RollState {
  readonly type: number;
  readonly page: string;
  readonly result: number;
  readonly rolling: boolean;
  readonly rolled: boolean;
  readonly frame: number;
}

/** The mutable RollDialog state (+ the next animation step time). */
interface Roll { type: number; page: string; result: number; rolling: boolean; rolled: boolean; frame: number; next: number }

/** Which part of the HUD state changed. */
export type HudPart = "outputs" | "timer" | "compass" | "chatNotice" | "roll";

export interface HudStateDeps {
  send: (f: Uint8Array) => void;
  /** performance.now() (CMain.Time). */
  now: () => number;
  /** SoundManager.PlaySound (the roll sounds). */
  play: (sound: number) => void;
  /** The chat log: its Announcement lines show the chat notice (ChatDialog.ReceiveChat -> ChatNoticeDialog.ShowNotice). */
  chatLog?: ChatLog;
}

export class HudState {
  /** A part changed (outputs: a line added; the rest: shown / hidden / updated). */
  readonly changed = new Emitter<HudPart>();
  /** S.UpdateNotice: NoticeDialog.Update(title, message). */
  readonly notice = new Emitter<{ title: string; message: string }>();
  /** S.OpenBrowser with an http / https URL (the href). */
  readonly openBrowser = new Emitter<string>();
  /** S.NPCRequestInput: the MirInputBox ("Please enter the required information."); answer with confirmInput. */
  readonly requestInput = new Emitter<{ npcId: number; pageName: string }>();
  /** RollDialog.ReturnResult: C.CallNPC "[page]" on the last NPC (GameScene.NPCID, gated by NPCTime). */
  readonly rollDone = new Emitter<{ page: string }>();

  private _outputs: OutputLine[] = [];
  private readonly timers: Timer[] = [];
  private currentTimer: Timer | null = null;
  private timerStarted = false;
  private timerCounter = 0;
  private timerTime = 0;
  private _timer: TimerView | null = null;
  private _compassTarget: WirePoint | null = null;
  private _chatNotice: { text: string; type: number; until: number } | null = null;
  private _roll: Roll | null = null;
  private readonly d: HudStateDeps;

  constructor(d: HudStateDeps) {
    this.d = d;
    d.chatLog?.announcement.on((text) => this.showChatNotice(text));
  }

  /** GameScene.OutputMessages (newest last, at most 10, unexpired as of the last process()). */
  get outputs(): readonly OutputLine[] { return this._outputs; }
  /** TimerDialog: the shown timer (null = hidden). */
  get timer(): TimerView | null { return this._timer; }
  /** CompassDialog.Destination (null = none). */
  get compassTarget(): WirePoint | null { return this._compassTarget; }
  /** ChatNoticeDialog: the shown announcement (type 0 = small text, 1 = large; null = hidden). */
  get chatNotice(): { readonly text: string; readonly type: number } | null { return this._chatNotice; }
  /** RollDialog (null = none set up or closed). */
  get roll(): RollState | null { return this._roll; }

  /** GameScene.OutputMessage. */
  output(message: string, type: number = OutputMessageType.Normal): void {
    this._outputs.push({ message, expire: this.d.now() + 5000, type });
    if (this._outputs.length > 10) this._outputs.shift();
    this.changed.emit("outputs");
  }

  /** ChatNoticeDialog.ShowNotice (ChatDialog.ReceiveChat for Announcement lines). */
  showChatNotice(text: string, type = 0): void {
    this._chatNotice = { text, type, until: this.d.now() + 10000 };
    this.changed.emit("chatNotice");
  }

  /** GameScene.ProcessPacket for the HUD packets; true = consumed (all of them). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "SendOutputMessage": this.output(p.Message, p.Type); return true;
      case "UpdateNotice": this.notice.emit({ title: p.Notice.Title, message: p.Notice.Message }); return true;
      case "SetTimer": this.addTimer(p.Key, p.Seconds, p.Type); return true;
      case "ExpireTimer": this.expireTimer(p.Key); return true;
      case "SetCompass":
        // CompassDialog.SetPoint: (0, 0) = Point.Empty clears it
        this._compassTarget = p.Location.X === 0 && p.Location.Y === 0 ? null : { ...p.Location };
        this.changed.emit("compass");
        return true;
      case "Roll": this.setupRoll(p.Type, p.Page, p.Result, p.AutoRoll); return true;
      case "OpenBrowser": {
        let url: URL | null = null;
        try { url = new URL(p.Url); } catch { /* not a URL */ }
        if (url && (url.protocol === "https:" || url.protocol === "http:")) this.openBrowser.emit(url.href);
        return true;
      }
      case "NPCRequestInput": this.requestInput.emit({ npcId: p.NPCID, pageName: p.PageName }); return true;
      default: return false;
    }
  }

  /** MirInputBox OK for S.NPCRequestInput: C.NPCConfirmInput. */
  confirmInput(npcId: number, pageName: string, value: string): void {
    this.d.send(ClientPackets.NPCConfirmInput({ NPCID: npcId, PageName: pageName, Value: value }));
  }

  /** RollDialog click before the result: RollDialog.Roll (the die spins 290..293 six times, the yut throws once). */
  startRoll(): void {
    const r = this._roll;
    if (!r || r.rolling || r.rolled) return;
    this.doRoll(r);
  }

  /** RollDialog click after the result: hides it. */
  closeRoll(): void {
    if (!this._roll) return;
    this._roll = null;
    this.changed.emit("roll");
  }

  /** Per frame: ProcessOuput, TimerDialog.Process, the chat notice timeout and the roll animation. */
  process(now: number): void {
    // ProcessOuput
    const before = this._outputs.length;
    this._outputs = this._outputs.filter((o) => now < o.expire);
    if (this._outputs.length !== before) this.changed.emit("outputs");
    this.processTimers(now);
    if (this._chatNotice && now > this._chatNotice.until) { this._chatNotice = null; this.changed.emit("chatNotice"); }
    // RollDialog animation: 100 ms a frame
    const r = this._roll;
    if (r?.rolling && now >= r.next) {
      r.next = now + 100;
      const die = r.type === 0, count = die ? 4 : 6, total = die ? count * 6 : count;
      if (r.frame < total) r.frame++;
      else {
        r.rolling = false;
        r.rolled = true;
        this.rollDone.emit({ page: r.page }); // RollDialog.ReturnResult
      }
      this.changed.emit("roll");
    }
  }

  /** TimerDialog.AddTimer. */
  private addTimer(key: string, seconds: number, type: number): void {
    const relative = seconds + Math.floor(this.d.now() / 1000);
    const t = this.timers.find((x) => x.key === key);
    if (t) { t.relative = relative; t.type = type; t.refresh = true; return; }
    this.timers.push({ key, type, relative, refresh: true });
  }

  /** TimerDialog.ExpireTimer. */
  private expireTimer(key: string): void {
    const t = this.timers.find((x) => x.key === key);
    if (!t) return;
    t.relative = 0;
    if (t === this.currentTimer) this.timerCounter = 0;
  }

  /** TimerDialog.Process. */
  private processTimers(now: number): void {
    const best = this.timers.reduce<Timer | null>((a, b) => (!a || b.relative < a.relative ? b : a), null);
    if (best && (best !== this.currentTimer || best.refresh)) {
      this.currentTimer = best;
      best.refresh = false;
      this.timerStarted = true;
      this.timerTime = now + 1000;
      this.timerCounter = best.relative - Math.floor(now / 1000);
      this.showTimer();
    }
    if (!this.currentTimer || !this.timerStarted || now < this.timerTime) return;
    this.timerCounter--;
    this.timerTime = now + 1000;
    if (this.timerCounter < 0) {
      this.timerStarted = false;
      this.timers.splice(this.timers.indexOf(this.currentTimer), 1);
      this._timer = null;
      this.changed.emit("timer");
      return;
    }
    this.showTimer();
  }

  /** TimerDialog.UpdateGraphic's input. */
  private showTimer(): void {
    this._timer = { counter: this.timerCounter, type: this.currentTimer?.type ?? 0 };
    this.changed.emit("timer");
  }

  /** RollDialog.Setup. */
  private setupRoll(type: number, page: string, result: number, autoRoll: boolean): void {
    const r: Roll = { type, page, result, rolling: false, rolled: false, frame: 0, next: 0 };
    this._roll = r;
    this.changed.emit("roll");
    if (autoRoll) this.doRoll(r);
  }

  /** RollDialog.Roll. */
  private doRoll(r: Roll): void {
    r.rolling = true;
    r.frame = 0;
    r.next = 0;
    this.d.play(r.type === 0 ? 10600 : 10601);
    this.changed.emit("roll");
  }
}
