// Ported from play/hudQuests.ts (its on-screen tracker, QuestsMock.renderTracker / marquee): the tracked quests on the
// right of the screen, the desktop's QuestTrackingDialog (classic port in src/ui/questWindow.ts renderTracking), over
// the Quests model (src/game/quests.ts: trackedEntries(), at most MAX_TRACKED_QUESTS = 5, saved per character by the
// model like Settings.SaveTrackedQuests). Quest names in gold, each ClientQuestProgress.TaskList line under it (the
// server sends them as localised text: a trailing "n/m" is shown as the count and ticks the line when complete), and
// "Ready: return to <NPC>" once Completed (FinishNPCIndex = the NPC's ObjectID: its name from the map objects or the
// map's NPC list). Lines wider than the box scroll back and forth (measured once per render). The header folds the list
// (a per-viewer convenience in localStorage 'crystal.ui.tracker'); a quest click opens the "quests" window on that quest
// (its focusQuest). Hidden while nothing is tracked. The mock's kill simulation and its own tracked flags are gone.

import type { QuestChange, QuestEntry } from "../../../game/quests.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { h, injectCss, rafCoalesce, setText, asButton } from "../core/dom.ts";
import { npcName } from "../core/format.ts";
import type { UiRuntime } from "../core/runtime.ts";
import type { UiWindow } from "../core/wm.ts";

/** windows/quests.ts's focus hook, typed structurally (the HUD does not import the window modules). */
type FocusingQuests = UiWindow & { focusQuest?: (questIndex: number) => void };

const STORE = "crystal.ui.tracker";

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
.hudm .qtrack .to { display: flex; align-items: baseline; gap: 6px; padding-left: 12px; line-height: 1.55; position: relative; }
.hudm .qtrack .to::before { content: ""; position: absolute; left: 3px; top: .72em; width: 4px; height: 4px; border-radius: 1px; background: rgba(232, 237, 245, .7); transform: rotate(45deg); }
.hudm .qtrack .to .clip { flex: 1; min-width: 0; }
.hudm .qtrack .to b { flex: none; font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .qtrack .to.ok { color: #ff5a4a; }
.hudm .qtrack .to.ok::before { content: "\\2713"; width: auto; height: auto; background: none; transform: none; top: 0; left: 0; font-size: 10px; font-weight: 700; color: #ff5a4a; }
.hudm .qtrack .tr { display: flex; align-items: baseline; gap: 6px; padding-left: 12px; position: relative; line-height: 1.55; font-weight: 700; color: #5fe07a; }
.hudm .qtrack .tr::before { content: "\\2713"; position: absolute; left: 0; top: 0; font-size: 10px; }
.hudm .qtrack .tr .clip { flex: 1; min-width: 0; }
`;

/** A trailing "n/m" count in a TaskList line (the localised KillCountProgress / CollectProgress text). */
const COUNT = /^(.*?)[\s:]*\(?(\d+)\s*\/\s*(\d+)\)?\s*$/;

export class QuestTracker {
  readonly el: HTMLElement;
  private readonly head: HTMLElement;
  private readonly count = h("span");
  private readonly body = h("div", "tb");
  private shut = false;
  private key = "";
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
    this.refresh = rafCoalesce(() => this.render(), rt.signal);
    this.fit = rafCoalesce(() => this.marquee(), rt.signal);
    const offs = [
      ctx.models.quests.onChange((c: QuestChange) => { if (c !== "items") this.refresh(); }),
      rt.layoutChanged.on(() => this.fit()),
    ];
    rt.signal.addEventListener("abort", () => { for (const off of offs) off(); }, { once: true });
    this.render();
  }

  dispose(): void { this.el.remove(); }

  private tracked(): QuestEntry[] { return this.ctx.models.quests.trackedEntries(); }

  private renderCount(): void {
    const n = this.tracked().length;
    setText(this.count, `${n} ${this.shut ? "▸" : "▾"}`);
  }

  /** The finishing NPC's name (FinishNPCIndex is its ObjectID). */
  private npcName(id: number): string {
    const ob = this.ctx.world.objects.get(id);
    if (ob) return npcName(ob.name)[0];
    const info = this.ctx.models.gameInfo.map(this.ctx.models.currentMap.index);
    const npc = info?.NPCs.find((n) => n.ObjectID === id);
    return npc ? npcName(npc.Name)[0] : "the quest giver";
  }

  private render(): void {
    const list = this.tracked();
    this.el.hidden = !list.length;
    this.renderCount();
    if (!list.length) { this.key = ""; this.body.replaceChildren(); return; }
    const key = list.map((e) => `${e.info.Index}|${e.progress.Completed}|${e.progress.TaskList.join("\u0001")}`).join("\u0002");
    if (key === this.key) return;
    this.key = key;
    const clip = (text: string) => h("div", "clip", h("span", "", text));
    const blocks: HTMLElement[] = [];
    for (const e of list) {
      const sh = h("span", "sh", e.info.Name);
      sh.setAttribute("aria-hidden", "true");
      const block = h("div", "tq", h("div", "clip tn", h("span", "", sh, h("span", "g", e.info.Name))));
      if (e.progress.Completed) block.append(h("div", "tr", clip(`Ready: return to ${this.npcName(e.info.FinishNPCIndex)}`)));
      else for (const task of e.progress.TaskList) {
        const m = COUNT.exec(task);
        const have = m ? +m[2]! : 0, need = m ? +m[3]! : 0;
        const line = h("div", "to" + (m && need > 0 && have >= need ? " ok" : ""), clip(m && m[1] ? m[1] : task));
        if (m && need > 0) line.append(h("b", "", `${Math.min(have, need)}/${need}`));
        block.append(line);
      }
      block.title = "Open the quest log";
      const quest = e.info.Index;
      block.addEventListener("click", () => {
        if (this.rt.toggleWindow("quests", true)) this.rt.peekWindow<FocusingQuests>("quests")?.focusQuest?.(quest);
      });
      blocks.push(block);
    }
    this.body.replaceChildren(...blocks);
    this.fit();
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
