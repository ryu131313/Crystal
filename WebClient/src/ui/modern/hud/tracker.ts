// Ported from play/hudQuests.ts (its on-screen tracker, QuestsMock.renderTracker / marquee): the tracked quests on the
// right of the screen, the desktop's QuestTrackingDialog / modern TrackerHud (classic port in src/ui/questWindow.ts
// renderTracking), over the quest V2 model (src/game/quests.ts: trackedRuns(), at most MAX_TRACKED_QUESTS = 5, saved per
// character by the model like Settings.SaveTrackedQuests). Quest names in gold, the current step's objectives under it
// (text built by the model; counted ones show "n/m" and a thin bar, a met one is ticked), "Ready: return to <NPC>" once
// the server says Ready (the Finish endpoints' names, Start when none), "Failed" for a failed run, and a step / quest
// timer. Lines wider than the box scroll back and forth (measured once per render). The header folds the list (a
// per-viewer convenience in localStorage 'crystal.ui.tracker'); a quest click opens the "quests" window on that quest
// (its focusQuest). Hidden while nothing is tracked.
// Progress toasts (plan Part B "Clients"): a short card with the objective and its bar on each objective increase
// (S.QuestProgress, one card per objective, updated in place), and a banner when a quest is accepted, becomes Ready,
// moves to a new step, fails or is turned in (S.QuestStates / S.QuestHistory). Both live in this HUD part, top centre,
// and fade on their own.

import { QuestEndpointRole, QuestRunStatus } from "../../../protocol/enums.generated.ts";
import type { QuestChange, QuestRun } from "../../../game/quests.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { h, injectCss, later, rafCoalesce, setText, asButton } from "../core/dom.ts";
import { npcName } from "../core/format.ts";
import type { UiRuntime } from "../core/runtime.ts";
import type { UiWindow } from "../core/wm.ts";

/** windows/quests.ts's focus hook, typed structurally (the HUD does not import the window modules). */
type FocusingQuests = UiWindow & { focusQuest?: (questIndex: number) => void };

const STORE = "crystal.ui.tracker";
/** How long a progress toast / a banner stays (ms). */
const TOAST_MS = 2600;
const BANNER_MS = 3600;
/** At most this many progress toasts at once (the oldest goes). */
const TOAST_MAX = 3;

const CSS = `
.hudm .qtrack { position: absolute; z-index: 2; right: 10px; top: 50%; transform: translateY(-50%); width: 220px; pointer-events: none; color: #e8edf5; font-size: 11.5px;
  text-shadow: 0 0 2px #000, 0 1px 2px #000, 0 0 6px rgba(0,0,0,.8); }
.hudm .qtrack .th { display: flex; align-items: center; gap: 6px; height: 20px; cursor: pointer; user-select: none; font-size: 10px; font-weight: 700; letter-spacing: .18em;
  color: rgba(232, 237, 245, .75); pointer-events: auto; }
.hudm .qtrack .th::after { content: ""; flex: 1; height: 1px; background: linear-gradient(90deg, rgba(240, 200, 110, .55), transparent); }
.hudm .qtrack .th span { order: 2; letter-spacing: 0; color: rgba(232, 237, 245, .6); }
.hudm .qtrack.shut .tb { display: none; }
.hudm .qtrack .tq { padding: 5px 0 3px; cursor: pointer; pointer-events: auto; }
.hudm .qtrack .clip { overflow: hidden; white-space: nowrap; }
.hudm .qtrack .clip > span { display: inline-block; }
.hudm .qtrack .clip.marq > span { animation: qtrack-marq var(--dur, 6s) ease-in-out infinite alternate; }
@keyframes qtrack-marq { 0%, 18% { transform: translateX(0); } 82%, 100% { transform: translateX(var(--dx)); } }
/* Gold title: a gradient clipped to the text; the shadow is a copy of the text underneath (.sh). */
.hudm .qtrack .tn { font-size: 12.5px; font-weight: 700; letter-spacing: .02em; text-shadow: none; }
.hudm .qtrack .tn > span { position: relative; }
.hudm .qtrack .tn .sh { position: absolute; left: 0; top: 0; color: transparent; pointer-events: none; user-select: none; text-shadow: 0 1px 1px #000, 0 0 3px rgba(0,0,0,.9); }
.hudm .qtrack .tn .g { position: relative; background: linear-gradient(100deg, #a8741c 0%, #e8b84a 30%, #fff4c2 45%, #f0c860 55%, #b8862b 75%, #e8b84a 100%);
  -webkit-background-clip: text; background-clip: text; color: transparent; }
.hudm .qtrack .to { padding-left: 12px; line-height: 1.55; position: relative; }
.hudm .qtrack .to .tl { display: flex; align-items: baseline; gap: 6px; }
.hudm .qtrack .to::before { content: ""; position: absolute; left: 3px; top: .72em; width: 4px; height: 4px; border-radius: 1px; background: rgba(232, 237, 245, .7); transform: rotate(45deg); }
.hudm .qtrack .to .clip { flex: 1; min-width: 0; }
.hudm .qtrack .to b { flex: none; font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .qtrack .to .bar { height: 2px; margin: 1px 0 2px; background: rgba(232, 237, 245, .18); border-radius: 1px; overflow: hidden; }
.hudm .qtrack .to .bar i { display: block; height: 100%; transform-origin: 0 50%; background: rgba(240, 200, 110, .9); }
.hudm .qtrack .to.ok { color: #5fe07a; }
.hudm .qtrack .to.ok .bar i { background: #5fe07a; }
.hudm .qtrack .to.ok::before { content: "\\2713"; width: auto; height: auto; background: none; transform: none; top: 0; left: 0; font-size: 10px; font-weight: 700; color: #5fe07a; }
.hudm .qtrack .tr { display: flex; align-items: baseline; gap: 6px; padding-left: 12px; position: relative; line-height: 1.55; font-weight: 700; color: #5fe07a; }
.hudm .qtrack .tr::before { content: "\\2713"; position: absolute; left: 0; top: 0; font-size: 10px; }
.hudm .qtrack .tr.bad { color: #ff5a4a; }
.hudm .qtrack .tr.bad::before { content: "\\00d7"; }
.hudm .qtrack .tr .clip { flex: 1; min-width: 0; }
.hudm .qtrack .tt { padding-left: 12px; font-size: 10.5px; color: rgba(232, 237, 245, .7); font-variant-numeric: tabular-nums; }
.hudm .qtoasts { position: absolute; z-index: 3; left: 50%; top: 92px; transform: translateX(-50%); display: flex; flex-direction: column; align-items: center; gap: 6px; pointer-events: none; }
.hudm .qtoasts .qban { padding: 6px 22px; font-size: 15px; font-weight: 700; letter-spacing: .04em; color: #fff4c2; text-align: center;
  text-shadow: 0 1px 2px #000, 0 0 8px rgba(0,0,0,.9); background: linear-gradient(90deg, transparent, rgba(20, 14, 6, .72) 20%, rgba(20, 14, 6, .72) 80%, transparent);
  animation: qtoast-in .25s ease-out, qtoast-out .4s ease-in var(--out) forwards; }
.hudm .qtoasts .qban small { display: block; font-size: 10px; font-weight: 700; letter-spacing: .2em; color: rgba(232, 237, 245, .7); text-transform: uppercase; }
.hudm .qtoasts .qban.bad { color: #ff8a7a; }
.hudm .qtoasts .qtoast { min-width: 200px; max-width: 320px; padding: 5px 10px 6px; font-size: 12px; color: #e8edf5; border-radius: 6px;
  background: rgba(10, 12, 18, .72); border: 1px solid rgba(240, 200, 110, .35); text-shadow: 0 1px 1px #000; animation: qtoast-in .2s ease-out, qtoast-out .4s ease-in var(--out) forwards; }
.hudm .qtoasts .qtoast .tl { display: flex; gap: 8px; } .hudm .qtoasts .qtoast .tl span { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .qtoasts .qtoast .tl b { font-variant-numeric: tabular-nums; }
.hudm .qtoasts .qtoast .bar { margin-top: 4px; height: 3px; background: rgba(232, 237, 245, .18); border-radius: 2px; overflow: hidden; }
.hudm .qtoasts .qtoast .bar i { display: block; height: 100%; transform-origin: 0 50%; background: rgba(240, 200, 110, .95); transition: transform .25s; }
.hudm .qtoasts .qtoast.ok { border-color: rgba(95, 224, 122, .55); } .hudm .qtoasts .qtoast.ok .bar i { background: #5fe07a; }
@keyframes qtoast-in { from { opacity: 0; transform: translateY(-6px); } to { opacity: 1; transform: none; } }
@keyframes qtoast-out { to { opacity: 0; } }
@media (prefers-reduced-motion: reduce) { .hudm .qtoasts .qban, .hudm .qtoasts .qtoast { animation: qtoast-out .01s linear var(--out) forwards; } }
`;

interface LiveToast { el: HTMLElement; text: HTMLElement; n: HTMLElement; bar: HTMLElement; stop: () => void }

export class QuestTracker {
  readonly el: HTMLElement;
  private readonly head: HTMLElement;
  private readonly count = h("span");
  private readonly body = h("div", "tb");
  private readonly toasts = h("div", "qtoasts");
  /** Progress toasts on screen, by objective id. */
  private readonly live = new Map<number, LiveToast>();
  private shut = false;
  private key = "";
  /** Timer labels of the shown list: [element, ends at (ms)]; ticked each second while any exists. */
  private timers: [HTMLElement, number][] = [];
  private timerStop: (() => void) | null = null;
  private readonly refresh: () => void;
  private readonly fit: () => void;

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext) {
    injectCss("hudm-tracker-css", CSS);
    try { this.shut = localStorage.getItem(STORE) === "shut"; } catch { /* default open */ }
    this.head = asButton(h("div", "th", "QUESTS", this.count), "Fold the quest tracker");
    this.head.addEventListener("click", () => {
      this.shut = !this.shut;
      try { localStorage.setItem(STORE, this.shut ? "shut" : "open"); } catch { /* not kept */ }
      this.el.classList.toggle("shut", this.shut);
      this.renderCount();
      if (!this.shut) this.fit();
    }, { signal: rt.signal });
    this.el = h("div", "qtrack" + (this.shut ? " shut" : ""), this.head, this.body);
    this.el.hidden = true;
    rt.layers.wins.append(this.el);
    rt.layers.guides.append(this.toasts); // above the windows (pointer-events none), so a banner shows over an open NPC dialogue
    this.refresh = rafCoalesce(() => this.render(), rt.signal);
    this.fit = rafCoalesce(() => this.marquee(), rt.signal);
    const q = ctx.models.quests;
    const offs = [
      q.onChange((c: QuestChange) => { if (c !== "items" && c !== "offers" && c !== "history" && c !== "npcs") this.refresh(); }),
      q.progressed.on((p) => { if (p.objective && p.prev !== undefined && p.value > p.prev) this.progressToast(p.quest, p.objective.Index); }),
      q.runChanged.on((c) => this.runBanner(c.quest, c.prev, c.run)),
      q.completed.on((row) => this.banner("Quest complete", row.name || q.name(row.quest), false)),
      rt.layoutChanged.on(() => this.fit()),
    ];
    rt.signal.addEventListener("abort", () => { for (const off of offs) off(); this.timerStop?.(); }, { once: true });
    this.render();
  }

  dispose(): void { this.el.remove(); this.toasts.remove(); this.timerStop?.(); }

  private renderCount(): void {
    const n = this.ctx.models.quests.trackedRuns().length;
    setText(this.count, `${n} ${this.shut ? "▸" : "▾"}`);
  }

  /** The turn-in NPC names (Finish endpoints, Start when none). */
  private finishName(quest: number): string {
    const q = this.ctx.models.quests, def = q.def(quest);
    const names = def ? q.endpointNames(def, QuestEndpointRole.Finish) : [];
    return names.length ? names.map((n) => npcName(n)[0]).join(" or ") : "the quest giver";
  }

  private render(): void {
    const q = this.ctx.models.quests;
    const list = q.trackedRuns();
    this.el.hidden = !list.length;
    this.renderCount();
    if (!list.length) { this.key = ""; this.body.replaceChildren(); this.setTimers([]); return; }
    const key = list.map((r) => this.runKey(r)).join("\u0002");
    if (key === this.key) return;
    this.key = key;
    const clip = (text: string) => h("div", "clip", h("span", "", text));
    const blocks: HTMLElement[] = [];
    const timers: [HTMLElement, number][] = [];
    for (const run of list) {
      const def = q.def(run.quest)!;
      const sh = h("span", "sh", def.Name);
      sh.setAttribute("aria-hidden", "true");
      const block = h("div", "tq", h("div", "clip tn", h("span", "", sh, h("span", "g", def.Name))));
      if (run.status === QuestRunStatus.Failed) block.append(h("div", "tr bad", clip("Failed")));
      else if (run.status === QuestRunStatus.Ready) block.append(h("div", "tr", clip(`Ready: return to ${this.finishName(run.quest)}`)));
      else {
        const step = q.currentStep(run.quest);
        const objs = step ? q.shownObjectives(def, step.Index) : [];
        if (q.pendingBranches(run.quest).length) block.append(h("div", "tr", clip(`Choose: speak to ${this.finishName(run.quest)}`)));
        for (const o of objs) {
          const counted = !q.binary(o) && Math.max(1, o.Count) > 1;
          const text = counted ? q.objectiveText(o).replace(/:\s*\d+\/\d+$/, "") : q.objectiveText(o);
          const tl = h("div", "tl", clip(text));
          if (counted) tl.append(h("b", "", `${q.value(o)}/${Math.max(1, o.Count)}`));
          const line = h("div", "to" + (q.done(o) ? " ok" : ""), tl);
          if (counted) { const fill = h("i"); fill.style.transform = `scaleX(${q.fraction(o).toFixed(3)})`; line.append(h("div", "bar", fill)); }
          block.append(line);
        }
        let end = 0;
        for (const t of [run.expiresAt, run.stepExpiresAt]) if (t > 0 && (end === 0 || t < end)) end = t;
        if (end) { const t = h("div", "tt"); timers.push([t, end]); block.append(t); }
      }
      block.title = "Open the quest log";
      const quest = run.quest;
      block.addEventListener("click", () => {
        if (this.rt.toggleWindow("quests", true)) this.rt.peekWindow<FocusingQuests>("quests")?.focusQuest?.(quest);
      });
      blocks.push(block);
    }
    this.body.replaceChildren(...blocks);
    this.setTimers(timers);
    this.fit();
  }

  /** What the tracker shows of a run (the render key). */
  private runKey(r: QuestRun): string {
    const q = this.ctx.models.quests, def = q.def(r.quest);
    let k = `${r.quest}|${r.status}|${r.step}|${r.expiresAt}|${r.stepExpiresAt}`;
    if (def && r.step >= 0) for (const o of def.Objectives) if (o.Step === r.step) k += `|${o.Index}:${q.value(o)}:${q.objectiveText(o)}`;
    return k + `|${q.pendingBranches(r.quest).length}`;
  }

  /** The tracker's timers tick once a second while any is shown. */
  private setTimers(t: [HTMLElement, number][]): void {
    this.timers = t;
    this.tickTimers();
    if (t.length && !this.timerStop) {
      const id = window.setInterval(() => this.tickTimers(), 1000);
      this.timerStop = () => { window.clearInterval(id); this.timerStop = null; };
    } else if (!t.length) this.timerStop?.();
  }

  private tickTimers(): void {
    const now = Date.now();
    for (const [el, end] of this.timers) {
      const s = Math.max(0, Math.ceil((end - now) / 1000));
      const m = Math.floor(s / 60), hours = Math.floor(m / 60);
      setText(el, `Time left ${hours ? `${hours}:${String(m % 60).padStart(2, "0")}` : m}:${String(s % 60).padStart(2, "0")}`);
    }
  }

  // — Toasts —

  /** A progress card for an objective (one per objective, updated in place while it shows). */
  private progressToast(quest: number, objectiveId: number): void {
    const q = this.ctx.models.quests;
    const def = q.def(quest), o = def?.Objectives.find((x) => x.Index === objectiveId);
    if (!def || !o || !q.run(quest)) return;
    const counted = !q.binary(o) && Math.max(1, o.Count) > 1;
    const text = counted ? q.objectiveText(o).replace(/:\s*\d+\/\d+$/, "") : q.objectiveText(o);
    const count = counted ? `${q.value(o)}/${Math.max(1, o.Count)}` : q.done(o) ? "Done" : "";
    let t = this.live.get(objectiveId);
    if (t) {
      t.stop();
      setText(t.text, text);
      setText(t.n, count);
      t.bar.style.transform = `scaleX(${q.fraction(o).toFixed(3)})`;
      t.el.classList.toggle("ok", q.done(o));
      t.el.getAnimations().forEach((a) => { a.cancel(); a.play(); });
    } else {
      while (this.live.size >= TOAST_MAX) {
        const first = this.live.entries().next().value;
        if (!first) break;
        first[1].stop(); first[1].el.remove(); this.live.delete(first[0]);
      }
      const textEl = h("span", "", text), n = h("b", "", count), fill = h("i");
      fill.style.transform = `scaleX(${q.fraction(o).toFixed(3)})`;
      const el = h("div", "qtoast" + (q.done(o) ? " ok" : ""), h("div", "tl", textEl, n), counted ? h("div", "bar", fill) : null);
      el.style.setProperty("--out", `${TOAST_MS - 400}ms`);
      el.title = def.Name;
      this.toasts.append(el);
      t = { el, text: textEl, n, bar: fill, stop: () => {} };
      this.live.set(objectiveId, t);
    }
    const entry = t;
    entry.stop = later(() => { entry.el.remove(); if (this.live.get(objectiveId) === entry) this.live.delete(objectiveId); }, TOAST_MS, this.rt.signal);
  }

  /** Banners for run changes: accepted, Ready, a new step, failed (turn-ins come from the history row). */
  private runBanner(quest: number, prev: QuestRun | null, run: QuestRun | null): void {
    const q = this.ctx.models.quests, name = q.name(quest);
    if (!run) return; // ended: a turn-in banners from `completed`, an abandon needs none
    if (!prev) { this.banner("Quest accepted", name, false); return; }
    if (run.status === QuestRunStatus.Ready && prev.status !== QuestRunStatus.Ready) this.banner("Ready to turn in", name, false);
    else if (run.status === QuestRunStatus.Failed && prev.status !== QuestRunStatus.Failed) this.banner("Quest failed", name, true);
    else if (run.step !== prev.step && run.step >= 0 && run.status === QuestRunStatus.Active) {
      const step = q.currentStep(quest);
      this.banner("Quest updated", step?.Title ? `${name}: ${step.Title}` : name, false);
    }
  }

  private banner(kind: string, text: string, bad: boolean): void {
    const el = h("div", "qban" + (bad ? " bad" : ""), h("small", "", kind), text);
    el.style.setProperty("--out", `${BANNER_MS - 400}ms`);
    this.toasts.prepend(el);
    later(() => el.remove(), BANNER_MS, this.rt.signal);
  }

  /** Lines wider than their box scroll to their end and back, about 25 px a second (measured once per render). */
  private marquee(): void {
    if (this.el.hidden || this.shut) return;
    const clips = this.el.querySelectorAll<HTMLElement>(".clip");
    const over: number[] = [];
    for (const c of clips) over.push((c.firstElementChild as HTMLElement).scrollWidth - c.clientWidth); // reads first
    clips.forEach((c, i) => {
      const o = over[i]!;
      c.classList.toggle("marq", o > 1);
      if (o > 1) { c.style.setProperty("--dx", -o - 2 + "px"); c.style.setProperty("--dur", (2.5 + o / 25).toFixed(1) + "s"); }
    });
  }
}
