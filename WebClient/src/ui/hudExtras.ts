// Small HUD parts with the desktop artwork:
//   GameScene.OutputMessage / ProcessOuput  up to 10 lines at (20, 25 + i * 13), 10pt, LimeGreen (Quest Gold, Guild
//                                            DeepPink), each gone after 5 s (S.SendOutputMessage and the game's own)
//   NoticeDialog       Prguse 961, title, 19 lines with {text/Colour} and (text/link) markup, scroll buttons, OK / close
//   TimerDialog        the nearest timer as Prguse2 900+ digits (mm:ss, hh:mm past an hour) + the egg timer animation
//   CompassDialog      Prguse2 1470 + 40 directions pointing at the destination (hidden on arrival)
//   RollDialog         die (Prguse 282 / 290..293 / 281 + result) or yut (Items 2581..2586 / 2587 + result)
//   ChatNoticeDialog   the Announcement banner (Prguse 1361 at 70 % + 1360, yellow 10pt), 10 s
//   S.OpenBrowser      the desktop opens the default browser; here the link opens in a new tab (http / https only)
//   S.NPCRequestInput  MirInputBox -> C.NPCConfirmInput

import type { ServerPacket } from "../protocol/server.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { ClientPackets } from "../protocol/client.ts";
import { OutputMessageType } from "../protocol/enums.generated.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirImage, MirLabel, MirButton, MirControl, closeButton, Lib, getUiRoot } from "./mir/controls.ts";
import { inputBox } from "./messageBox.ts";

export interface HudWorld {
  stage: HTMLElement;
  send: (f: Uint8Array) => void;
  now: () => number;
  play: (sound: number) => void;
  /** RollDialog.ReturnResult: CallNPC "[page]" on the last NPC (GameScene.NPCID), gated by NPCTime. */
  callNpcPage: (page: string) => void;
  screen?: readonly [number, number];
}

/** Color.FromName for the notice markup (CSS knows the same names; unknown names draw black like Color.Empty). */
const cssColour = (name: string) => (CSS.supports("color", name) ? name : "#000");

class NoticeDialog {
  private readonly win: MirWindow;
  private readonly nameLabel: MirLabel;
  private readonly up: MirButton;
  private readonly down: MirButton;
  private readonly bar: MirButton;
  private readonly lines: MirLabel[] = [];
  private current: string[] = [];
  private index = 0;
  private static readonly MAX = 19;

  constructor(SW: number, SH: number) {
    this.win = new MirWindow({ library: Lib.Prguse, index: 961, location: [Math.floor((SW - 316) / 2), Math.floor((SH - 466) / 3)], sort: true });
    this.nameLabel = new MirLabel({ parent: this.win, autoSize: true, font: "bold 10pt Arial", foreColour: "#deb887", location: [30, 6] });
    closeButton(this.win, [289, 3], () => this.win.hide());
    new MirButton({ parent: this.win, library: Lib.Title, index: 193, hoverIndex: 194, pressedIndex: 195, location: [120, 436], onClick: () => this.win.hide() });
    this.up = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 470, hoverIndex: 471, pressedIndex: 472, size: [16, 14], location: [293, 33], sound: SoundList.ButtonA, visible: false,
      onClick: () => { if (this.index <= 0) return; this.index--; this.render(); } });
    this.down = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 473, hoverIndex: 474, pressedIndex: 475, size: [16, 14], location: [293, 418], sound: SoundList.ButtonA,
      onClick: () => { if (this.index + NoticeDialog.MAX >= this.current.length) return; this.index++; this.render(); } });
    this.bar = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [293, 46], sound: 0 });
    // PositionBar_OnMoving: drag between y 46 and 399
    this.bar.el.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || this.current.length <= NoticeDialog.MAX) return;
      e.stopPropagation();
      const s = getUiRoot().getBoundingClientRect().width / (getUiRoot().offsetWidth || 1) || 1;
      const startY = e.clientY, startTop = this.bar.location[1];
      const move = (m: PointerEvent) => {
        const y = Math.max(46, Math.min(399, Math.round(startTop + (m.clientY - startY) / s)));
        const interval = Math.trunc(400 / (this.current.length - NoticeDialog.MAX)) || 1;
        this.index = Math.floor((y - 46) / interval);
        this.render(false);
        this.bar.location = [293, y];
      };
      const upFn = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", upFn); };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", upFn);
    });
    this.win.el.addEventListener("wheel", (e) => {
      if (this.current.length <= NoticeDialog.MAX) return;
      this.index = Math.max(0, Math.min(this.current.length - NoticeDialog.MAX, this.index + Math.sign(e.deltaY)));
      this.render();
    });
    for (let i = 0; i < NoticeDialog.MAX; i++) this.lines.push(new MirLabel({ parent: this.win, size: [420, 20], location: [25, 50 + i * 20], font: "10pt Arial", notControl: false }));
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  /** NoticeDialog.Update. */
  update(title: string, message: string): void {
    if (!message.trim()) return;
    if (title) this.nameLabel.text = title;
    this.current = message.split("\r\n");
    this.index = 0;
    const scroll = this.current.length > NoticeDialog.MAX;
    this.up.visible = this.down.visible = this.bar.visible = scroll;
    this.render();
    this.win.show();
  }

  /** NewText: the colour / link markup becomes inline spans. */
  private render(updateBar = true): void {
    for (let i = 0; i < NoticeDialog.MAX; i++) {
      const label = this.lines[i]!;
      const line = this.current[this.index + i];
      label.el.replaceChildren();
      if (line === undefined) continue;
      const re = /\{((.*?)\/(.*?))\}|\(((.*?)\/(.*?))\)/g;
      let last = 0;
      for (const m of line.matchAll(re)) {
        label.el.append(line.slice(last, m.index));
        const span = document.createElement("span");
        if (m[1] !== undefined) { span.textContent = m[2]!; span.style.color = cssColour(m[3]!); }
        else {
          // NewLink: yellow, red under the mouse; only http:// links open
          span.textContent = m[5]!;
          span.style.color = "#ffff00";
          const link = m[6]!;
          span.addEventListener("pointerenter", () => { span.style.color = "#ff0000"; });
          span.addEventListener("pointerleave", () => { span.style.color = "#ffff00"; });
          span.addEventListener("click", () => { if (/^http:\/\//i.test(link) || /^https:\/\//i.test(link)) window.open(link, "_blank", "noopener,noreferrer"); });
        }
        label.el.append(span);
        last = m.index! + m[0].length;
      }
      label.el.append(line.slice(last));
    }
    if (updateBar && this.current.length > NoticeDialog.MAX) {
      const interval = Math.trunc(400 / (this.current.length - NoticeDialog.MAX));
      this.bar.location = [293, Math.max(46, Math.min(399, 46 + this.index * interval))];
    }
  }
}

interface Timer { key: string; type: number; relative: number; refresh: boolean }

/** TimerDialog: digits at (0|22|44|58|80, 70) + the egg timer at (23, 0), (ScreenWidth - 120, ScreenHeight - 230). */
class TimerDialog {
  private readonly root: MirControl;
  private readonly egg: MirImage;
  private readonly digits: MirImage[];
  private readonly timers: Timer[] = [];
  private current: Timer | null = null;
  private started = false;
  private counter = 0;
  private time = 0;
  private eggBase = 960;
  private eggFrame = 0;
  private nextEgg = 0;

  constructor(parent: HTMLElement, SW: number, SH: number) {
    this.root = new MirControl({ parent, location: [SW - 120, SH - 230], size: [120, 100], notControl: true, visible: false, className: "mir-hud" });
    this.egg = new MirImage({ parent: this.root, library: Lib.Prguse2, index: 960, location: [23, 0], useOffset: true, notControl: true });
    this.digits = [0, 22, 44, 58, 80].map((x, i) => new MirImage({ parent: this.root, library: Lib.Prguse2, index: i === 2 ? 910 : 900, location: [x, 70], useOffset: true, notControl: true }));
    gameSignal().addEventListener("abort", () => this.root.dispose());
  }

  add(key: string, seconds: number, type: number, now: number): void {
    const relative = seconds + Math.floor(now / 1000);
    const t = this.timers.find((x) => x.key === key);
    if (t) { t.relative = relative; t.type = type; t.refresh = true; return; }
    this.timers.push({ key, type, relative, refresh: true });
  }

  expire(key: string): void {
    const t = this.timers.find((x) => x.key === key);
    if (!t) return;
    t.relative = 0;
    if (t === this.current) this.counter = 0;
  }

  /** TimerDialog.Process. */
  process(now: number): void {
    const best = this.timers.reduce<Timer | null>((a, b) => (!a || b.relative < a.relative ? b : a), null);
    if (best && (best !== this.current || best.refresh)) {
      this.current = best;
      best.refresh = false;
      this.started = true;
      this.time = now + 1000;
      this.counter = best.relative - Math.floor(now / 1000);
      this.updateGraphic();
    }
    // MirAnimatedControl: 6 frames, 333 ms, looping while a timer runs
    if (this.egg.visible && now >= this.nextEgg) { this.nextEgg = now + 333; this.eggFrame = (this.eggFrame + 1) % 6; this.egg.index = this.eggBase + this.eggFrame; }
    if (!this.current || !this.started || now < this.time) return;
    this.counter--;
    this.time = now + 1000;
    if (this.counter < 0) {
      this.root.visible = false;
      this.started = false;
      this.timers.splice(this.timers.indexOf(this.current), 1);
      return;
    }
    this.updateGraphic();
  }

  private updateGraphic(): void {
    const c = Math.max(0, this.counter);
    const h = Math.floor(c / 3600) % 24, m = Math.floor((c % 3600) / 60), s = c % 60;
    const [a, b] = h > 0 ? [h, m] : [m, s];
    this.digits[0]!.index = 900 + Math.floor(a / 10);
    this.digits[1]!.index = 900 + (a % 10);
    this.digits[3]!.index = 900 + Math.floor(b / 10);
    this.digits[4]!.index = 900 + (b % 10);
    this.root.visible = true;
    const type = this.current?.type ?? 0;
    this.egg.visible = type === 1 || type === 2;
    this.eggBase = type === 2 ? 440 : 960;
  }
}

export class HudExtras {
  private readonly w: HudWorld;
  private readonly outputLines: MirLabel[] = [];
  private outputs: { message: string; expire: number; type: number }[] = [];
  private readonly notice: NoticeDialog;
  private readonly timer: TimerDialog;
  private readonly compass: MirControl;
  private readonly compassImage: MirImage;
  private compassTarget: WirePoint | null = null;
  private readonly chatNotice: MirImage;
  private readonly chatNoticeLayout: MirImage;
  private readonly chatNoticeText: [MirLabel, MirLabel];
  private chatNoticeUntil = 0;
  private readonly rollRoot: MirControl;
  private readonly rollImage: MirImage;
  private roll: { type: number; page: string; result: number; rolling: boolean; rolled: boolean; frame: number; next: number } | null = null;

  constructor(w: HudWorld) {
    this.w = w;
    const [SW, SH] = w.screen ?? [1024, 768];
    const root = w.stage;
    // GameScene.OutputLines (drawn by the scene, under the dialogs)
    for (let i = 0; i < 10; i++) {
      const l = new MirLabel({ parent: root, autoSize: true, font: "10pt Arial", foreColour: "#32cd32", location: [20, 25 + i * 13], notControl: true, visible: false, className: "mir-hud" });
      this.outputLines.push(l);
    }
    this.notice = new NoticeDialog(SW, SH);
    this.timer = new TimerDialog(root, SW, SH);
    this.compass = new MirControl({ parent: root, location: [SW / 2 - 25, SH / 2 - 120], size: [10, 10], notControl: true, visible: false, className: "mir-hud" });
    this.compassImage = new MirImage({ parent: this.compass, library: Lib.Prguse2, index: 1470, useOffset: true, notControl: true });
    // ChatNoticeDialog
    this.chatNotice = new MirImage({ parent: root, library: Lib.Prguse, index: 1361, location: [Math.floor(SW / 2 - 330), Math.floor(SH / 6 - 12)], visible: false, notControl: true, className: "mir-hud" });
    this.chatNotice.picOpacity = 0.7;
    this.chatNoticeText = [
      new MirLabel({ parent: this.chatNotice, center: true, font: "10pt Arial", foreColour: "#ffff00", location: [0, -6], size: [660, 40], notControl: true }),
      new MirLabel({ parent: this.chatNotice, center: true, font: "15pt Arial", foreColour: "#ffff00", location: [0, 0], size: [660, 40], notControl: true }),
    ];
    this.chatNoticeLayout = new MirImage({ parent: this.chatNotice, library: Lib.Prguse, index: 1360, location: [0, 0], notControl: true });
    // RollDialog
    this.rollRoot = new MirControl({ parent: root, visible: false, className: "mir-hud" });
    this.rollImage = new MirImage({ parent: this.rollRoot, library: Lib.Prguse, index: 282, useOffset: true });
    this.rollImage.onClick(() => {
      const r = this.roll;
      if (!r || r.rolling) return;
      if (r.rolled) { this.rollRoot.visible = false; return; }
      this.doRoll(r);
    });
    for (const c of [this.rollRoot]) c.el.addEventListener("mousedown", (e) => e.stopPropagation());
    gameSignal().addEventListener("abort", () => {
      for (const l of this.outputLines) l.dispose();
      this.compass.dispose(); this.chatNotice.dispose(); this.rollRoot.dispose();
    });
  }

  /** GameScene.OutputMessage. */
  output(message: string, type: number = OutputMessageType.Normal): void {
    this.outputs.push({ message, expire: this.w.now() + 5000, type });
    if (this.outputs.length > 10) this.outputs.shift();
  }

  /** ChatNoticeDialog.ShowNotice (ChatDialog.ReceiveChat for Announcement lines). */
  showChatNotice(text: string, type = 0): void {
    this.chatNotice.index = type === 0 ? 1361 : 1363;
    this.chatNoticeLayout.index = type === 0 ? 1360 : 1362;
    this.chatNoticeText[0].text = this.chatNoticeText[1].text = text;
    this.chatNoticeText[0].visible = type === 0;
    this.chatNoticeText[1].visible = type === 1;
    this.chatNotice.visible = true;
    this.chatNoticeUntil = this.w.now() + 10000;
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "SendOutputMessage": this.output(p.Message, p.Type); return true;
      case "UpdateNotice": this.notice.update(p.Notice.Title, p.Notice.Message); return true;
      case "SetTimer": this.timer.add(p.Key, p.Seconds, p.Type, this.w.now()); return true;
      case "ExpireTimer": this.timer.expire(p.Key); return true;
      case "SetCompass":
        // CompassDialog.SetPoint: (0, 0) = Point.Empty clears it
        this.compassTarget = p.Location.X === 0 && p.Location.Y === 0 ? null : { ...p.Location };
        return true;
      case "Roll": this.setupRoll(p.Type, p.Page, p.Result, p.AutoRoll); return true;
      case "OpenBrowser": {
        let url: URL | null = null;
        try { url = new URL(p.Url); } catch { /* not a URL */ }
        if (url && (url.protocol === "https:" || url.protocol === "http:")) window.open(url.href, "_blank", "noopener,noreferrer");
        return true;
      }
      case "NPCRequestInput":
        inputBox(this.w.stage, "Please enter the required information.", "", (value) => {
          this.w.send(ClientPackets.NPCConfirmInput({ NPCID: p.NPCID, PageName: p.PageName, Value: value }));
        });
        return true;
      default: return false;
    }
  }

  /** RollDialog.Setup. */
  private setupRoll(type: number, page: string, result: number, autoRoll: boolean): void {
    const [SW, SH] = this.w.screen ?? [1024, 768];
    const r = { type, page, result, rolling: false, rolled: false, frame: 0, next: 0 };
    this.roll = r;
    if (type === 0) { this.rollRoot.size = [65, 65]; this.rollRoot.location = [SW / 2 - 38, SH / 2 - 40]; this.rollImage.library = Lib.Prguse; this.rollImage.index = 282; }
    else { this.rollRoot.size = [180, 130]; this.rollRoot.location = [SW / 2 - 90, SH / 2 - 65]; this.rollImage.library = Lib.Items; this.rollImage.index = 2581; }
    this.rollRoot.visible = true;
    if (autoRoll) this.doRoll(r);
  }

  /** RollDialog.Roll: the die spins 290..293 six times, the yut throws 2581..2586 once, 100 ms a frame. */
  private doRoll(r: NonNullable<HudExtras["roll"]>): void {
    r.rolling = true;
    r.frame = 0;
    r.next = 0;
    this.w.play(r.type === 0 ? 10600 : 10601);
  }

  /** Per frame: output lines, timers, compass, chat notice and the roll animation. */
  update(user: WirePoint | null): void {
    const now = this.w.now();
    // ProcessOuput
    this.outputs = this.outputs.filter((o) => now < o.expire);
    this.outputLines.forEach((l, i) => {
      const o = this.outputs[i];
      l.visible = !!o;
      if (!o) return;
      l.text = o.message;
      l.foreColour = o.type === OutputMessageType.Quest ? "#ffd700" : o.type === OutputMessageType.Guild ? "#ff1493" : "#32cd32";
    });
    this.timer.process(now);
    if (this.chatNotice.visible && now > this.chatNoticeUntil) this.chatNotice.visible = false;
    // RollDialog animation
    const r = this.roll;
    if (r?.rolling && now >= r.next) {
      r.next = now + 100;
      const die = r.type === 0, count = die ? 4 : 6, total = die ? count * 6 : count;
      if (r.frame < total) { this.rollImage.index = (die ? 290 : 2581) + (r.frame % count); r.frame++; }
      else {
        this.rollImage.index = (die ? 281 : 2587) + r.result;
        r.rolling = false;
        r.rolled = true;
        this.w.callNpcPage(r.page); // RollDialog.ReturnResult
      }
    }
    // CompassDialog.Process
    const d = this.compassTarget;
    if (!d || !user || (d.X === user.X && d.Y === user.Y)) { this.compass.visible = false; return; }
    this.compass.visible = true;
    const angle = (Math.atan2(-(user.X - d.X), user.Y - d.Y) * 180) / Math.PI;
    const degree = (angle + 360) % 360;
    this.compassImage.index = 1470 + Math.floor((40 / 360) * degree);
  }
}
