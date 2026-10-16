// Ported from play/hudExtras.ts: the new UI's small HUD extras, a view over the HudState model (src/game/hudState.ts),
// the poison timers (src/game/poisons.ts) and the worn gear. The mock's random poisons, wear, demo timers, lab map
// data and fake HP ticks are gone; the classic counterpart is src/ui/hudExtras.ts + src/ui/duraPanel.ts.
//   Output lines   GameScene.OutputMessage (HudState.outputs: up to 10, 5 s each; Quest gold, Guild pink, else green).
//   Notice board   S.UpdateNotice -> a "Notice" window (NoticeDialog: {text/Colour} and (text/link) markup; a link
//                  opens a new tab after a confirm, http / https only).
//   Timer          TimerDialog: the nearest S.SetTimer counting down (HudState.timer), a ring for the time left, red in
//                  the last 10 s. S.SetTimer has no label: the pill shows the countdown only.
//   Compass        CompassDialog: an arrow around the character at the S.SetCompass cell, with the NPC there (from the
//                  map's ClientMapInfo) or the cell, and the steps left; hidden on the target cell, the target kept
//                  (CompassDialog.Process).
//   Roll           RollDialog (S.Roll die / yut): click to roll unless it rolls itself (HudState.startRoll), the result
//                  shown when the model's animation ends, then RollDialog.ReturnResult calls the NPC page
//                  (HudState.rollDone -> NpcSession.callPage); click again to close.
//   OpenBrowser    S.OpenBrowser: a confirm, then a new tab (HudState passes http / https URLs only).
//   NPC input      S.NPCRequestInput: an input popup -> C.NPCConfirmInput (HudState.confirmInput).
//   Poisons        a status line while frozen / stunned / paralysed (time left from S.PoisonStatus where the server
//                  sent one) and soft screen tints (blind, frost, paralysis, daze, slow, green): gradients only, no
//                  backdrop-filter over the live canvas, created only while on. The poison icon row and the durability
//                  strip are the bottom HUD's (hud.ts, inside the rig's right wing): one view each.
// One frame subscription runs HudState.process(now) (the model's per-frame part: output expiry, timers, chat notice
// timeout, roll animation); every DOM write happens only when what it shows changed.

import { OutputMessageType, PoisonType } from "../../../protocol/enums.generated.ts";
import type { RollState } from "../../../game/hudState.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, setText, reducedMotion } from "../core/dom.ts";
import { svg } from "../core/icons.ts";
import { fmtClock, npcName } from "../core/format.ts";
import { progressRing } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import type { UiRuntime } from "../core/runtime.ts";

/** PoisonType bits the status line names: [flag, name, colour (DrawPoison's, lifted for a dark HUD)]. */
const POISONS: readonly [number, string, string][] = [
  [PoisonType.Green, "Green poison", "#3fd86b"],
  [PoisonType.Red, "Red poison", "#ff5050"],
  [PoisonType.Bleeding, "Bleeding", "#e0404a"],
  [PoisonType.Slow, "Slow", "#b070ff"],
  [PoisonType.Stun, "Stunned", "#ffe14a"],
  [PoisonType.Dazed, "Dazed", "#ffe14a"],
  [PoisonType.Blindness, "Blinded", "#ff5cb0"],
  [PoisonType.Frozen, "Frozen", "#5aa0ff"],
  [PoisonType.Paralysis, "Paralysed", "#c4c4c4"],
  [PoisonType.LRParalysis, "Paralysed", "#c4c4c4"],
  [PoisonType.DelayedExplosion, "Delayed explosion", "#ffa53a"],
];
const LOCKS = PoisonType.Frozen | PoisonType.Stun | PoisonType.Paralysis | PoisonType.LRParalysis;
/** Screen tints: [class, the PoisonType bits that show it]. */
const TINTS: readonly [string, number][] = [
  ["green", PoisonType.Green], ["slow", PoisonType.Slow], ["blind", PoisonType.Blindness], ["frost", PoisonType.Frozen],
  ["para", PoisonType.Paralysis | PoisonType.LRParalysis | PoisonType.Stun], ["daze", PoisonType.Dazed],
];
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
/** RollDialog: the frames of the model's animation (die 4 x 6 spins, yut 6 throws) at 100 ms each. */
const ROLL_MS = (type: number) => (type === 0 ? 24 : 6) * 100;
/** Pips per face (cells 0-8 of a 3 x 3 grid). */
const PIPS: Readonly<Record<number, readonly number[]>> = { 1: [4], 2: [2, 6], 3: [2, 4, 6], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
/** The cube rotation that shows face n at the front (faces: 1 front, 2 right, 3 top, 4 bottom, 5 left, 6 back). */
const SHOW: Readonly<Record<number, readonly [number, number]>> = { 1: [0, 0], 2: [0, -90], 3: [-90, 0], 4: [90, 0], 5: [0, 90], 6: [0, 180] };
const FACE_AT: Readonly<Record<number, string>> = {
  1: "translateZ(32px)", 2: "rotateY(90deg) translateZ(32px)", 3: "rotateX(90deg) translateZ(32px)", 4: "rotateX(-90deg) translateZ(32px)",
  5: "rotateY(-90deg) translateZ(32px)", 6: "rotateY(180deg) translateZ(32px)",
};

const STYLE = `
/* Output lines (GameScene.OutputMessage) */
.hudm .houts { position: absolute; z-index: 2; left: 210px; top: 14px; display: flex; flex-direction: column; gap: 1px; pointer-events: none; font-size: 12px; font-weight: 600;
  text-shadow: 0 1px 2px #000, 0 0 3px #000; }
.hudm .houts .o0 { color: #6fe08a; } .hudm .houts .o1 { color: #ffd84a; } .hudm .houts .o2 { color: #ff6fc0; }
.hudm .houts div { animation: hx-in .2s ease-out; }
@keyframes hx-in { from { opacity: 0; transform: translateY(-4px); } }
/* Screen tints (behind the HUD parts, gradients only) */
.hudm .hfx { position: absolute; z-index: 0; inset: 0; pointer-events: none; animation: hx-fade .5s ease-out; }
@keyframes hx-fade { from { opacity: 0; } }
.hudm .hfx.blind { background: radial-gradient(circle at 50% 46%, transparent 0 70px, rgba(0,0,0,.92) 230px); }
.hudm .hfx.frost { background: radial-gradient(ellipse 70% 65% at 50% 50%, transparent 55%, rgba(120,180,255,.42)); box-shadow: inset 0 0 90px rgba(170,215,255,.55); }
.hudm .hfx.para { background: rgba(110,110,118,.28); }
.hudm .hfx.daze { background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 55%, rgba(255,225,74,.18)); }
.hudm .hfx.slow { background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 60%, rgba(140,70,220,.3)); }
.hudm .hfx.green { background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 62%, rgba(40,200,90,.22)); }
.hudm .hstat { position: absolute; z-index: 2; left: 50%; top: 52%; translate: -50% 0; padding: 3px 12px; border-radius: 12px; font-size: 12px; font-weight: 700; letter-spacing: .08em; color: #fff;
  background: rgba(8,10,16,.75); border: 1px solid var(--pz, var(--fr)); pointer-events: none; font-variant-numeric: tabular-nums; }
/* Compass */
.hudm .hcomp { position: absolute; z-index: 2; left: 50%; top: 47%; width: 0; height: 0; pointer-events: none; }
.hudm .hcomp .arm { position: absolute; left: 0; top: 0; width: 0; height: 0; transition: transform .25s ease-out; }
.hudm .hcomp .arr { position: absolute; left: -15px; top: -104px; width: 30px; height: 30px; animation: hx-bob 1.2s ease-in-out infinite; }
.hudm .hcomp .arr svg { width: 100%; height: 100%; fill: var(--ac); stroke: #fff; stroke-width: 1; }
@keyframes hx-bob { 50% { translate: 0 -5px; } }
.hudm .hcomp .lbl { position: absolute; left: 0; top: 66px; translate: -50% 0; white-space: nowrap; padding: 2px 10px; border-radius: 10px; font-size: 11px; color: var(--tx);
  background: rgba(8,10,16,.7); border: 1px solid color-mix(in srgb, var(--ac) 50%, var(--fr)); }
.hudm .hcomp .lbl b { color: #fff; } .hudm .hcomp .lbl span { color: var(--ac); margin-left: 6px; font-weight: 700; }
/* Timer */
.hudm .htm { position: absolute; z-index: 2; left: 50%; top: 44px; translate: -50% 0; display: flex; align-items: center; gap: 8px; padding: 4px 14px 4px 5px; border-radius: 20px; color: var(--tx);
  pointer-events: none; background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 80%, transparent), color-mix(in srgb, var(--pb) 88%, transparent)); border: 1px solid var(--fr);
  box-shadow: 0 0 0 1px #000, 0 6px 16px rgba(0,0,0,.55); }
.hudm .htm .ui-ring b, .hudm .htm .ui-ring small { display: none; }
.hudm .htm .t { font-size: 15px; font-weight: 800; color: #fff; font-variant-numeric: tabular-nums; letter-spacing: .02em; }
.hudm .htm.hot { border-color: var(--bad); --ring: var(--bad); } .hudm .htm.hot .t { color: #ffb3a6; }
/* Roll */
.hudm .hdice { position: absolute; z-index: 6; left: 50%; top: 40%; translate: -50% -50%; width: 260px; padding: 16px 16px 14px; text-align: center; border-radius: 14px; pointer-events: auto; color: var(--tx);
  background: radial-gradient(ellipse 80% 60% at 50% 40%, color-mix(in srgb, var(--ac) 14%, var(--pb)), color-mix(in srgb, var(--pb) 92%, #000)); border: 1px solid color-mix(in srgb, var(--ac) 50%, var(--fr));
  box-shadow: 0 0 0 1px #000, 0 16px 40px rgba(0,0,0,.75); animation: hx-in .2s ease-out; }
.hudm .hdice h3 { margin: 0; font-size: 13px; letter-spacing: .24em; color: var(--ac); }
.hudm .hdice .stage { height: 120px; display: flex; align-items: center; justify-content: center; perspective: 600px; cursor: pointer; margin-top: 8px; }
.hudm .hdice .cube { position: relative; width: 64px; height: 64px; transform-style: preserve-3d; transform: rotateX(-25deg) rotateY(35deg); }
.hudm .hdice .face { position: absolute; inset: 0; border-radius: 12px; display: grid; grid-template: repeat(3, 1fr) / repeat(3, 1fr); padding: 9px;
  background: radial-gradient(circle at 35% 30%, #f6f8fc, #c9d2e2); border: 1px solid #8592aa; box-shadow: inset 0 0 10px rgba(0,0,0,.25); backface-visibility: hidden; }
.hudm .hdice .face i { width: 11px; height: 11px; margin: auto; border-radius: 50%; background: radial-gradient(circle at 40% 35%, #3a4660, #0b1020); visibility: hidden; }
.hudm .hdice .face.one i { background: radial-gradient(circle at 40% 35%, #ff7a6a, #8a1010); width: 15px; height: 15px; }
.hudm .hdice .face i.on { visibility: visible; }
.hudm .hdice .sticks { display: flex; gap: 10px; align-items: center; height: 110px; }
.hudm .hdice .stick { width: 16px; height: 92px; border-radius: 8px; background: linear-gradient(90deg, #e8d3a8, #f6e7c6, #e8d3a8); border: 1px solid #8a6a3a; }
.hudm .hdice .yut { width: 120px; height: 100px; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .hdice .res { height: 26px; font-size: 18px; font-weight: 800; color: #fff; }
.hudm .hdice .hint { font-size: 11px; color: var(--dim); }
/* Notice window (NoticeDialog) */
.hudm .hnotice { width: 420px; }
.hudm .hnotice .nb { max-height: 420px; overflow-y: auto; padding: 10px 14px 12px; font-size: 12px; line-height: 1.55; }
.hudm .hnotice .nb div { min-height: 1.2em; overflow-wrap: anywhere; }
.hudm .hnotice .nb .nl { color: var(--gold); cursor: pointer; text-decoration: underline; }
.hudm.narrow .hnotice { width: 360px; }
`;

/** CSS colour from a NoticeDialog colour name (Color.FromName; unknown names draw in the text colour). */
const cssColour = (name: string) => (CSS.supports("color", name) ? name : "");

/** S.UpdateNotice: NoticeDialog as a window (built on the first notice). */
class NoticeWindow extends UiWindow {
  private readonly head: HTMLElement;
  private readonly body = h("div", "nb");
  constructor(rt: UiRuntime, private readonly openLink: (href: string) => void) {
    super(rt, "hnotice", null, 90);
    this.head = this.titleBar("NOTICE", "Close");
    this.el.append(h("div", "panel", this.head, this.body));
  }

  /** NoticeDialog.Update: an empty message is ignored; the title only replaces the old one when set. */
  update(title: string, message: string): void {
    if (!message.trim()) return;
    if (title) setText(this.head.firstChild!, title);
    const lines: HTMLElement[] = [];
    for (const line of message.split(/\r?\n/)) {
      const row = h("div");
      // NewText: {text/Colour} and (text/link) become inline spans.
      const re = /\{((.*?)\/(.*?))\}|\(((.*?)\/(.*?))\)/g;
      let last = 0;
      for (const m of line.matchAll(re)) {
        if (m.index! > last) row.append(line.slice(last, m.index));
        if (m[1] !== undefined) {
          const s = h("span", "", m[2]!);
          const c = cssColour(m[3]!);
          if (c) s.style.color = c;
          row.append(s);
        } else {
          const link = m[6]!;
          const s = h("span", "nl", m[5]!);
          if (/^https?:\/\//i.test(link)) { s.title = link; s.addEventListener("click", () => this.openLink(link)); }
          row.append(s);
        }
        last = m.index! + m[0].length;
      }
      if (last < line.length) row.append(line.slice(last));
      lines.push(row);
    }
    this.body.replaceChildren(...lines);
    this.body.scrollTop = 0;
    this.toggle(true);
  }
}

export class HudExtras {
  private readonly outs = h("div", "houts");
  private readonly status = h("div", "hstat");
  private readonly compass = h("div", "hcomp");
  private readonly arm = h("div", "arm");
  private readonly compName = h("b");
  private readonly compDist = h("span");
  private readonly timerEl = h("div", "htm");
  private readonly timerText = h("div", "t");
  private readonly ring = progressRing(30, 3);
  private dice: HTMLElement | null = null;
  private shownRoll: RollState | null = null;
  private rollPhase: "idle" | "rolling" | "done" = "idle";
  private notice: NoticeWindow | null = null;
  private readonly tints = new Map<string, HTMLElement>();
  private flags = -1;
  private lockFlag = 0;
  private compAng = "";
  private timerMax = 0;
  private lastCounter = -1;
  private readonly poisonSub: { on(): void; off(): void; readonly running: boolean };

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext) {
    injectCss("hudm-extras-css", STYLE);
    const m = ctx.models;
    this.status.hidden = true;
    this.compass.hidden = true;
    this.timerEl.hidden = true;
    const arr = h("div", "arr", svg("M12 2l7 14-7-4-7 4z", ""));
    this.arm.append(arr);
    this.compass.append(this.arm, h("div", "lbl", this.compName, this.compDist));
    this.timerEl.append(this.ring.el, this.timerText);
    rt.layers.wins.append(this.outs, this.status, this.compass, this.timerEl);

    // HudState.process + the compass every frame; the poison status line at 4 Hz while frozen / stunned / paralysed.
    const frame = rt.ticker.frame((now) => this.frame(now));
    frame.on();
    this.poisonSub = rt.ticker.every(250, (now) => this.renderPoisons(now));
    const offs = [
      m.hudState.changed.on((part) => {
        if (part === "outputs") this.renderOutputs();
        else if (part === "timer") this.renderTimer();
        else if (part === "roll") this.renderRoll();
        else if (part === "compass") this.compAng = "";
      }),
      m.hudState.notice.on(({ title, message }) => {
        this.notice ??= new NoticeWindow(rt, (href) => this.openLink(href));
        this.notice.update(title, message);
      }),
      m.hudState.openBrowser.on((href) => this.openLink(href)),
      m.hudState.requestInput.on(({ npcId, pageName }) => {
        void rt.popups.input({ title: "Input", text: "Please enter the required information.", allowEmpty: true })
          .then((v) => { if (v !== null) m.hudState.confirmInput(npcId, pageName, v); });
      }),
      // RollDialog.ReturnResult: C.CallNPC "[page]" on the last NPC.
      m.hudState.rollDone.on(({ page }) => m.npcSession.callPage(page)),
      m.poisons.changed.on(() => this.renderPoisons(ctx.now())),
    ];
    rt.signal.addEventListener("abort", () => { for (const off of offs) off(); frame.off(); this.poisonSub.off(); }, { once: true });
    this.renderOutputs();
    this.renderTimer();
    this.renderRoll();
  }

  /** The user's PoisonType flags now (the HP orb's green tint, the belt's lock). */
  get poisonFlags(): number { return this.ctx.player.user?.poison ?? 0; }

  dispose(): void {
    for (const el of [this.outs, this.status, this.compass, this.timerEl, this.dice]) el?.remove();
    for (const t of this.tints.values()) t.remove();
    this.notice?.dispose();
  }

  // — Per frame —

  private frame(now: number): void {
    this.ctx.models.hudState.process(now);
    // Poison flags change with S.Poisoned (user.poison): pick them up at once, then the 4 Hz timer keeps the status time.
    const flags = this.poisonFlags;
    if (flags !== this.flags) this.renderPoisons(now);
    this.renderCompass();
  }

  // — Output lines / timer —

  private renderOutputs(): void {
    const outs = this.ctx.models.hudState.outputs;
    const nodes: HTMLElement[] = [];
    for (const o of outs) nodes.push(h("div", o.type === OutputMessageType.Quest ? "o1" : o.type === OutputMessageType.Guild ? "o2" : "o0", o.message));
    this.outs.replaceChildren(...nodes);
  }

  private renderTimer(): void {
    const t = this.ctx.models.hudState.timer;
    this.timerEl.hidden = !t || t.counter < 0;
    if (!t || t.counter < 0) { this.lastCounter = -1; return; }
    // TimerDialog has no length: the ring runs from the counter seen when the timer started (or was refreshed).
    if (t.counter > this.lastCounter) this.timerMax = Math.max(1, t.counter);
    this.lastCounter = t.counter;
    setText(this.timerText, fmtClock(t.counter));
    this.ring.set(t.counter / this.timerMax);
    this.timerEl.classList.toggle("hot", t.counter <= 10 || t.type === 2);
  }

  // — Compass —

  private renderCompass(): void {
    const d = this.ctx.models.hudState.compassTarget;
    const user = this.ctx.player.user;
    // CompassDialog.Process: hidden without a target, and on the target cell (the target is kept).
    const show = !!d && !!user && !(d.X === user.location.X && d.Y === user.location.Y);
    if (this.compass.hidden === show) this.compass.hidden = !show;
    if (!show) return;
    const dx = d!.X - user!.location.X, dy = d!.Y - user!.location.Y;
    const ang = Math.atan2(dx * 48, -dy * 32) * 180 / Math.PI; // cells are 48 x 32 px on screen
    const rot = `rotate(${ang.toFixed(1)}deg)`;
    if (rot === this.compAng) return;
    this.compAng = rot;
    this.arm.style.transform = rot;
    const steps = Math.max(Math.abs(dx), Math.abs(dy));
    const dir = DIRS[Math.round(((ang + 360) % 360) / 45) % 8]!;
    const info = this.ctx.models.gameInfo.map(this.ctx.models.currentMap.index);
    const npc = info?.NPCs.find((n) => Math.abs(n.Location.X - d!.X) <= 1 && Math.abs(n.Location.Y - d!.Y) <= 1);
    setText(this.compName, npc ? npcName(npc.Name)[0] : `${d!.X}, ${d!.Y}`);
    setText(this.compDist, `${steps} step${steps === 1 ? "" : "s"} ${dir}`);
  }

  // — Roll (RollDialog) —

  private renderRoll(): void {
    const r = this.ctx.models.hudState.roll;
    if (!r) { this.dice?.remove(); this.dice = null; this.shownRoll = null; this.rollPhase = "idle"; return; }
    if (r !== this.shownRoll || !this.dice) this.buildRoll(r);
    const phase = r.rolled ? "done" : r.rolling ? "rolling" : "idle";
    if (phase === this.rollPhase) return;
    this.rollPhase = phase;
    const d = this.dice!;
    const hint = d.querySelector<HTMLElement>(".hint")!, res = d.querySelector<HTMLElement>(".res")!;
    if (phase === "rolling") {
      setText(hint, "Rolling…");
      if (reducedMotion()) return;
      const ms = ROLL_MS(r.type);
      if (r.type === 0) {
        d.querySelector<HTMLElement>(".cube")!.animate([{ transform: "rotateX(-25deg) rotateY(35deg) translateY(0)" }, { transform: "rotateX(400deg) rotateY(300deg) translateY(-30px)", offset: 0.45 },
          { transform: "rotateX(720deg) rotateY(720deg) translateY(0)" }], { duration: ms, easing: "cubic-bezier(.2,.7,.25,1)" });
      } else {
        d.querySelectorAll<HTMLElement>(".stick").forEach((s, k) => s.animate([{ transform: "translateY(0) rotateZ(0)" },
          { transform: `translateY(-50px) rotateZ(${(k - 1.5) * 40 + 180}deg)`, offset: 0.5 }, { transform: `translateY(0) rotateZ(${(k - 1.5) * 8 + 360}deg)` }], { duration: ms + k * 60, easing: "ease-out" }));
      }
    } else if (phase === "done") {
      if (r.type === 0) {
        const [x, y] = SHOW[r.result] ?? [0, 0];
        d.querySelector<HTMLElement>(".cube")!.style.transform = `rotateX(${x}deg) rotateY(${y}deg)`;
        setText(res, `You rolled ${r.result}`);
      } else {
        // The desktop's yut result picture (Items 2587 + Result).
        const stage = d.querySelector<HTMLElement>(".stage")!;
        const pic = h("div", "yut");
        stage.replaceChildren(pic);
        void libPicture(Lib.Items, 2587 + r.result).then((p) => { if (p) pic.style.backgroundImage = `url(${p.url})`; });
        setText(res, `Result ${r.result}`);
      }
      setText(hint, "Click to close");
    }
  }

  private buildRoll(r: RollState): void {
    this.dice?.remove();
    this.shownRoll = r;
    this.rollPhase = "idle";
    const stage = h("div", "stage");
    if (r.type === 0) {
      const cube = h("div", "cube");
      for (let n = 1; n <= 6; n++) {
        const f = h("div", "face" + (n === 1 ? " one" : ""));
        for (let c = 0; c < 9; c++) f.append(h("i", PIPS[n]!.includes(c) ? "on" : ""));
        f.style.transform = FACE_AT[n]!;
        cube.append(f);
      }
      stage.append(cube);
    } else {
      const wrap = h("div", "sticks");
      for (let k = 0; k < 4; k++) wrap.append(h("div", "stick"));
      stage.append(wrap);
    }
    const hint = h("div", "hint", r.rolling ? "Rolling…" : "Click to roll");
    const d = h("div", "hdice", h("h3", "", r.type === 0 ? "ROLL THE DIE" : "YUT NORI"), stage, h("div", "res"), hint);
    d.setAttribute("role", "dialog");
    // RollDialog click: roll before the result, close after it.
    d.addEventListener("click", () => {
      const s = this.ctx.models.hudState;
      const cur = s.roll;
      if (!cur || cur.rolling) return;
      if (cur.rolled) s.closeRoll(); else s.startRoll();
    }, { signal: this.rt.signal });
    this.rt.layers.wins.append(d);
    this.dice = d;
  }

  // — OpenBrowser / notice links —

  private openLink(href: string): void {
    void this.rt.popups.ask(`Open this link in a new tab?\n${href}`, { title: "Open link", kind: "question" })
      .then((yes) => { if (yes) window.open(href, "_blank", "noopener,noreferrer"); });
  }

  // — Poisons —

  private renderPoisons(now: number): void {
    const flags = this.poisonFlags;
    const timers = this.ctx.models.poisons;
    if (flags !== this.flags) {
      this.flags = flags;
      for (const [cls, bits] of TINTS) {
        const on = (flags & bits) !== 0;
        const had = this.tints.get(cls);
        if (on && !had) { const el = h("div", "hfx " + cls); this.rt.layers.wins.prepend(el); this.tints.set(cls, el); }
        else if (!on && had) { had.remove(); this.tints.delete(cls); }
      }
      if (flags & LOCKS) this.poisonSub.on(); else this.poisonSub.off(); // only the status line counts down
    }
    // Status line while frozen / stunned / paralysed.
    let lock = 0;
    for (const [flag] of POISONS) if (flags & flag & LOCKS) { lock = flag; break; }
    if (this.status.hidden === !!lock) this.status.hidden = !lock;
    if (!lock) return;
    const def = POISONS.find((p) => p[0] === lock)!;
    const pt = timers.get(lock);
    setText(this.status, pt ? `${def[1].toUpperCase()} · ${(timers.timeLeft(pt, now) / 1000).toFixed(1)}s` : def[1].toUpperCase());
    if (lock !== this.lockFlag) { this.lockFlag = lock; this.status.style.setProperty("--pz", def[2]); }
  }
}
