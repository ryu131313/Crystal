// Small HUD packets: GameScene.SendOutputMessage (OutputMessage), ShowNotice (NoticeDialog), SetTimer / ExpireTimer
// (TimerDialog), SetCompass (CompassDialog), Roll (RollDialog), OpenBrowser and NPCRequestInput (MirInputBox).
// Differences: output messages go to the chat log (Quest = hint colour, Guild = guild colour); OpenBrowser posts a
// clickable link in the chat instead of opening a window; the NPC input box is prompt().

import type { ServerPacket } from "../protocol/server.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, OutputMessageType } from "../protocol/enums.generated.ts";
import { libIcon } from "./buffBar.ts";
import { el } from "./screens.ts";

export interface HudWorld {
  stage: HTMLElement;
  send: (f: Uint8Array) => void;
  chat: (parts: (string | Node)[], type: number) => void;
  now: () => number;
  play: (sound: number) => void;
  /** RollDialog.ReturnResult: CallNPC "[page]" on the last NPC (GameScene.NPCID), gated by NPCTime. */
  callNpcPage: (page: string) => void;
}

interface Timer { key: string; type: number; until: number }

export class HudExtras {
  private readonly w: HudWorld;
  private readonly timerBox = el("div", { class: "hud-timers" });
  private readonly compass = el("div", { class: "hud-compass", hidden: true, title: "Click to hide" });
  private compassTarget: WirePoint | null = null;
  private timers: Timer[] = [];
  private notice: HTMLElement | null = null;
  private roll: { root: HTMLElement; img: HTMLImageElement; type: number; page: string; result: number; rolling: boolean; rolled: boolean } | null = null;

  constructor(w: HudWorld) {
    this.w = w;
    this.compass.onclick = () => { this.compassTarget = null; this.compass.hidden = true; };
    w.stage.append(this.timerBox, this.compass);
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "SendOutputMessage":
        this.w.chat([p.Message], p.Type === OutputMessageType.Guild ? ChatType.Guild : ChatType.Hint);
        return true;
      case "UpdateNotice": this.showNotice(p.Notice.Title, p.Notice.Message); return true;
      case "SetTimer": {
        // TimerDialog.AddTimer: an existing key is updated
        const until = this.w.now() + p.Seconds * 1000;
        const t = this.timers.find((x) => x.key === p.Key);
        if (t) { t.until = until; t.type = p.Type; } else this.timers.push({ key: p.Key, type: p.Type, until });
        return true;
      }
      case "ExpireTimer": { const t = this.timers.find((x) => x.key === p.Key); if (t) t.until = 0; return true; }
      case "SetCompass":
        // CompassDialog.SetPoint: (0, 0) clears it
        this.compassTarget = p.Location.X === 0 && p.Location.Y === 0 ? null : { ...p.Location };
        this.compass.hidden = !this.compassTarget;
        return true;
      case "Roll": this.setupRoll(p.Type, p.Page, p.Result, p.AutoRoll); return true;
      case "OpenBrowser": {
        let url: URL | null = null;
        try { url = new URL(p.Url); } catch { /* not a URL */ }
        if (!url || (url.protocol !== "https:" && url.protocol !== "http:")) return true;
        this.w.chat(["The server opened a link: ", el("a", { href: url.href, target: "_blank", rel: "noopener noreferrer" }, url.href)], ChatType.Hint);
        return true;
      }
      case "NPCRequestInput": {
        const value = prompt("Please enter the required information.");
        if (value !== null) this.w.send(ClientPackets.NPCConfirmInput({ NPCID: p.NPCID, PageName: p.PageName, Value: value }));
        return true;
      }
      default: return false;
    }
  }

  /** NoticeDialog.Update. */
  private showNotice(title: string, message: string): void {
    this.notice?.remove();
    const root = el("div", { class: "window notice-window" },
      el("div", { class: "window-title" }, el("span", {}, title || "Notice"), el("button", { class: "window-close", onclick: () => root.remove() }, "×")),
      el("div", { class: "notice-text" }, message));
    root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.notice = root;
    this.w.stage.append(root);
  }

  /** RollDialog.Setup: type 0 = die (Prguse 281 + result, spin 290..293), 1 = yut (Items 2587 + result, throw 2581..2586). */
  private setupRoll(type: number, page: string, result: number, autoRoll: boolean): void {
    this.roll?.root.remove();
    const img = el("img", { alt: "" }) as HTMLImageElement;
    const root = el("div", { class: "roll-box" }, img);
    root.addEventListener("mousedown", (e) => e.stopPropagation());
    const r = { root, img, type, page, result, rolling: false, rolled: false };
    this.roll = r;
    this.setImage(type === 0 ? "Prguse" : "Items", type === 0 ? 282 : 2581);
    root.onclick = () => {
      if (r.rolling) return;
      if (r.rolled) { root.remove(); if (this.roll === r) this.roll = null; return; }
      this.doRoll(r);
    };
    this.w.stage.append(root);
    if (autoRoll) this.doRoll(r);
  }

  private setImage(lib: string, index: number): void {
    const r = this.roll;
    if (!r) return;
    void libIcon(lib, index).then((url) => { if (url && this.roll === r) r.img.src = url; });
  }

  /** RollDialog.Roll + AfterAnimation: the die spins 4 frames x 6 loops, the yut throws 6 frames once, 100 ms a frame. */
  private doRoll(r: NonNullable<HudExtras["roll"]>): void {
    r.rolling = true;
    const die = r.type === 0;
    const lib = die ? "Prguse" : "Items";
    const first = die ? 290 : 2581, count = die ? 4 : 6, total = die ? count * 6 : count;
    this.w.play(die ? 10600 : 10601);
    let frame = 0;
    const tick = () => {
      if (this.roll !== r) return;
      if (frame < total) { this.setImage(lib, first + (frame % count)); frame++; setTimeout(tick, 100); return; }
      this.setImage(lib, (die ? 281 : 2587) + r.result);
      r.rolling = false;
      r.rolled = true;
      this.w.callNpcPage(r.page); // RollDialog.ReturnResult
    };
    tick();
  }

  /** Per frame: timers (TimerDialog) and the compass arrow (CompassDialog) relative to the user. */
  update(user: WirePoint | null): void {
    const now = this.w.now();
    this.timers = this.timers.filter((t) => t.until > now - 2000);
    this.timerBox.replaceChildren(...this.timers.map((t) => {
      const s = Math.max(0, Math.ceil((t.until - now) / 1000));
      return el("div", { class: "hud-timer" }, `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`);
    }));
    if (!this.compassTarget || !user) return;
    const dx = this.compassTarget.X - user.X, dy = this.compassTarget.Y - user.Y;
    if (dx === 0 && dy === 0) { this.compass.textContent = "◎ here"; return; }
    const angle = (Math.atan2(dx, -dy) * 180) / Math.PI;
    this.compass.replaceChildren(el("span", { class: "hud-compass-arrow", style: `transform: rotate(${angle}deg)` }, "↑"),
      ` ${this.compassTarget.X}, ${this.compassTarget.Y} (${Math.round(Math.hypot(dx, dy))})`);
  }
}
