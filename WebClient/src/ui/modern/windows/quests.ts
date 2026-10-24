// Ported from play/hudQuests.ts (QuestsMock, its window part; the on-screen tracker is hud/tracker.ts): the new UI's
// quest log over the quest V2 model (src/game/quests.ts; plan Part B "Clients"). Desktop counterpart: the modern
// QuestsWindow (Client/MirScenes/Modern); the classic port is src/ui/questWindow.ts.
//  - Active = the runs (S.QuestStates: Active, Ready, Failed), Available = the quests the givers met this game offer
//    (S.QuestAvailability; the server decides, no client CanAccept) plus pending S.QuestOffer prompts, Completed = the
//    history (S.QuestHistory: times, last completion, repeat timer from AvailableAt); grouped by QuestCategory in folding
//    groups, with a debounced search (name, group, summary, NPC);
//  - the detail: summary, the step list (done / current / future, by Order), the current step's text (ReadyText while
//    Ready) and objectives with bars (text built by the model from type / target / count unless the objective has its
//    own), quest and step timers (live while shown), the pending branch choices (C.QuestChoose at the NPC whose dialogue
//    is open), the rewards (choice groups picked here or at the NPC, one pick per group, sent with C.QuestTurnIn);
//    a history quest's definition is asked with C.QuestDefinitionRequest when it is opened;
//  - actions: Track / Untrack (at most MAX_TRACKED_QUESTS, saved per character by the model), Share (C.QuestShare:
//    group members nearby), Map (the giver / turn-in NPC: opens the big map, a note when it is elsewhere), Abandon
//    (C.QuestAbandon after a question; story quests may forbid it: AbandonAllowed), Remove for a failed run (also
//    C.QuestAbandon), Accept / Turn in only while the right NPC's dialogue is open (the server needs the NPC in reach:
//    C.QuestAccept / C.QuestTurnIn with its ObjectID), else "Speak to <NPC>"; an S.QuestOffer is accepted with 0;
//  - an Items tab shows User.QuestInventory (display only, item tooltips);
//  - the watch hook (at UI start) remembers the NPCs met (S.ObjectNPC) and shows each S.QuestOffer as a request toast.
// No map markers (plan phase 5). Narrow layout (a layer below 1040 px): a slimmer list column.

import type { ItemInfo, QuestDefinitionInfo, UserItem } from "../../../protocol/server.ts";
import { QuestEndpointRole, QuestFlags, QuestRepeatPolicy, QuestRewardScope, QuestRunStatus } from "../../../protocol/enums.generated.ts";
import {
  MAX_CONCURRENT_QUESTS, MAX_TRACKED_QUESTS, QUEST_CATEGORY_LABEL, QUEST_CATEGORY_ORDER, QUEST_INVENTORY_SIZE, printTimeSpanFromSeconds,
  type QuestChange, type QuestOfferPrompt,
} from "../../../game/quests.ts";
import { parseLine, type NpcLinkKind } from "../../../game/npcMarkup.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, asButton, button, fmtAgo, fmtDuration, h, injectCss, later, npcName, searchBox, setText, svg, type Btn, type IconName,
} from "../core/index.ts";
import type { UiRuntime } from "../core/runtime.ts";
import { displayItem, objectiveRow, questMarker, rewardsBlock } from "./questParts.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.quests). */
export const QUESTS_WINDOW = "quests";

/** Seconds the offer toast stays up (declines when it runs out). */
const OFFER_SECONDS = 30;
/** A refused request sends nothing back: the row unlocks after this. */
const PENDING_MS = 5000;
/** Share again after this (the server answers a failed share with a chat line only). */
const SHARE_REST_MS = 3000;

type Tab = "active" | "available" | "completed" | "items";
const TABS: readonly [Tab, string][] = [["active", "Active"], ["available", "Available"], ["completed", "Completed"], ["items", "Items"]];
const CAT_ICON: Readonly<Record<number, IconName>> = { 0: "star", 1: "scroll", 2: "sun", 3: "calendar", 4: "repeat", 5: "spark", 6: "guild", 7: "eye" };
/** InventoryDialog.QuestGrid: 8 x 5. */
const ITEM_COLS = 8;

const STYLE = `
.hudm .qwin { --winh: 476px; width: 680px; }
.hudm.narrow .qwin { width: 600px; }
.hudm .qwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .qwin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .qwin .left { width: 290px; flex: none; display: flex; flex-direction: column; padding: 10px 8px 8px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .qwin .left { width: 240px; }
.hudm .qwin .qu-ic { width: 13px; height: 13px; flex: none; }
.hudm .qwin .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .qwin .seg button { flex: 1; min-width: 0; height: 26px; padding: 0 2px; display: flex; align-items: center; justify-content: center; gap: 4px; cursor: pointer; user-select: none;
  font: inherit; font-size: 11px; font-weight: 600; color: var(--dim); border: 0; background: color-mix(in srgb, var(--pb) 60%, #000); white-space: nowrap; }
.hudm .qwin .seg button + button { border-left: 1px solid var(--fr); }
.hudm .qwin .seg button i { font-style: normal; font-size: 10px; padding: 0 5px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .qwin .seg button:hover { color: var(--tx); }
.hudm .qwin .seg button.on { color: #fff; background: color-mix(in srgb, var(--ac) 24%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm.narrow .qwin .seg button i { display: none; }
.hudm .qwin .left .ui-search { margin: 8px 0 6px; }
.hudm .qwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .qwin .grp { display: flex; align-items: center; gap: 6px; height: 24px; margin-top: 4px; padding: 0 4px; cursor: pointer; user-select: none;
  font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .qwin .grp .chev { transition: transform .15s; font-size: 10px; width: 10px; }
.hudm .qwin .grp.shut .chev { transform: rotate(-90deg); }
.hudm .qwin .grp > span:last-child { margin-left: auto; letter-spacing: 0; color: var(--dim); }
.hudm .qwin .row { position: relative; display: flex; align-items: center; gap: 8px; padding: 6px 6px 6px 10px; margin-bottom: 3px; cursor: pointer;
  border: 1px solid transparent; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .qwin .row::before { content: ""; position: absolute; left: 3px; top: 7px; bottom: 7px; width: 2px; border-radius: 1px; background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .qwin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .qwin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .qwin .row.sel::before, .hudm .qwin .row.ready::before { background: var(--ac); }
.hudm .qwin .row.failed::before { background: #ff5a4a; }
.hudm .qwin .row .mk { width: 12px; flex: none; text-align: center; font-weight: 800; font-size: 12px; }
.hudm .qwin .row .mid { flex: 1; min-width: 0; }
.hudm .qwin .row .nm { font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .qwin .row.done .nm, .hudm .qwin .row.low .nm { color: var(--dim); }
.hudm .qwin .row .sub { margin-top: 3px; font-size: 10px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .qwin .row.busy { opacity: .6; cursor: progress; }
.hudm .qwin .pill { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; white-space: nowrap;
  color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .qwin .pill.hot { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .qwin .pill.bad { color: #fff; border-color: #ff5a4a; background: color-mix(in srgb, #ff5a4a 35%, transparent); }
.hudm .qwin .pin { width: 22px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: calc(var(--rad) * .7);
  color: color-mix(in srgb, var(--dim) 70%, transparent); border: 1px solid transparent; }
.hudm .qwin .pin:hover { color: var(--tx); border-color: var(--fr); }
.hudm .qwin .pin.on { color: var(--ac); }
.hudm .qwin .pin.full { opacity: .35; cursor: not-allowed; }
.hudm .qwin .empty { padding: 30px 10px; text-align: center; color: var(--dim); font-size: 12px; line-height: 1.5; }
.hudm .qwin .foot { padding-top: 6px; font-size: 11px; color: var(--dim); display: flex; justify-content: space-between; gap: 6px; }
.hudm .qwin .foot b { color: var(--tx); font-weight: 600; }
.hudm .qwin .detail { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .qwin .dscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 12px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .qwin .dscroll > * { flex-shrink: 0; }
.hudm .qwin .dhead h3 { margin: 0 0 6px; font-size: 17px; color: #fff; display: flex; align-items: center; gap: 8px; }
.hudm .qwin .dhead h3 .qu-ic { width: 16px; height: 16px; color: var(--ac); }
.hudm .qwin .dhead .tags { display: flex; flex-wrap: wrap; gap: 5px; }
.hudm .qwin .giver { display: flex; flex-wrap: wrap; gap: 4px 14px; margin-top: 8px; font-size: 11px; color: var(--dim); }
.hudm .qwin .giver b { color: var(--tx); font-weight: 600; }
.hudm .qwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; }
.hudm .qwin .txt { font-size: 12px; line-height: 1.55; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); white-space: pre-wrap; overflow-wrap: anywhere; }
.hudm .qwin .txt.desc { font-style: italic; }
.hudm .qwin .txt .it { color: #4fd8e8; cursor: help; }
.hudm .qwin .txt .it:hover { color: #ffa500; }
.hudm .qwin .steps { display: flex; flex-direction: column; gap: 2px; }
.hudm .qwin .step { display: flex; align-items: baseline; gap: 6px; font-size: 12px; color: var(--dim); }
.hudm .qwin .step b { width: 14px; flex: none; text-align: center; font-weight: 700; }
.hudm .qwin .step.done { color: color-mix(in srgb, #5fe07a 75%, var(--dim)); }
.hudm .qwin .step.cur { color: #fff; font-weight: 600; }
.hudm .qwin .timer { font-size: 12px; font-variant-numeric: tabular-nums; color: var(--tx); }
.hudm .qwin .timer.low { color: #ff5a4a; }
.hudm .qwin .branches { display: flex; flex-wrap: wrap; gap: 6px; }
.hudm .qwin .note { font-size: 11px; color: var(--dim); }
.hudm .qwin .actions { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .qwin .actions .btn { height: 26px; padding: 0 9px; white-space: nowrap; }
.hudm .qwin .actions .speak { margin-left: auto; font-size: 11px; color: var(--ac); font-weight: 600; text-align: right; }
.hudm .qwin .igrid { display: grid; grid-template-columns: repeat(${ITEM_COLS}, auto); gap: 4px; justify-content: start; padding: 14px 16px; }
.hudm.narrow .qwin .igrid { grid-template-columns: repeat(6, auto); }
`;

// — The NPCs met this game (per UI runtime, filled by the watch hook from S.ObjectNPC) —

/** Where an NPC was seen: its name, map (MapIndex) and cell. */
interface MetNpc { readonly name: string; readonly map: number; readonly x: number; readonly y: number }
const metNpcs = (rt: UiRuntime): Map<number, MetNpc> => rt.service("quests.metNpcs", () => new Map<number, MetNpc>());

/** A list row: a quest id and what it is on this tab. */
interface Entry { readonly quest: number; readonly npc: number; readonly offer: QuestOfferPrompt | null }

/** A display-only item cell (core item cell styling) for the quest items. */
interface ShowCell { el: HTMLElement; icon: HTMLElement; count: HTMLElement; img: number; item: UserItem | null; info: ItemInfo | null }

class QuestsWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: Tab = "active";
  /** The selected quest per tab (-1 = the first). */
  private readonly picked: Record<Exclude<Tab, "items">, number> = { active: -1, available: -1, completed: -1 };
  /** Folded groups ("tab:category"). */
  private readonly shut = new Set<string>();
  private query = "";
  /** Quests with a request on its way (until the server's state answer or PENDING_MS). */
  private readonly pending = new Set<number>();
  private readonly shared = new Set<number>();
  /** Info links the detail waits for ("ITEM:12"): an arrival redraws it. */
  private readonly waiting = new Set<string>();
  private listDirty = true;
  private detailDirty = true;
  private itemsDirty = true;
  /** Timer labels of the shown detail, updated every second while shown: [element, ends at (ms), prefix]. */
  private timers: [HTMLElement, number, string][] = [];

  private readonly segBtns = new Map<Tab, { b: HTMLButtonElement; n: HTMLElement }>();
  private readonly search: ReturnType<typeof searchBox>;
  private readonly list = h("div", "list");
  private readonly foot = h("div", "foot");
  private readonly left: HTMLElement;
  private readonly detail = h("div", "detail qparts");
  private readonly itemsPane = h("div", "detail");
  private readonly igrid = h("div", "igrid");
  private readonly inote = h("div", "note");
  private readonly icells: ShowCell[] = [];

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "qwin", 100, 50);
    injectCss("win-quests", STYLE);
    this.m = ui.ctx.models;

    const seg = h("div", "seg");
    seg.setAttribute("role", "tablist");
    for (const [t, label] of TABS) {
      const n = h("i");
      const b = h("button", "", label, n);
      b.type = "button";
      b.setAttribute("role", "tab");
      b.addEventListener("click", () => this.setTab(t), { signal: this.signal });
      seg.append(b);
      this.segBtns.set(t, { b, n });
    }
    this.search = searchBox("Search quests, places, people…", (q) => { this.query = q.toLowerCase(); this.listDirty = true; this.invalidate(); }, { signal: this.signal });
    this.left = h("div", "left", seg, this.search.el, this.list, this.foot);
    this.itemsPane.append(h("div", "dscroll", h("div", "lbl", "Quest items"), this.inote, this.igrid));
    this.itemsPane.hidden = true;
    this.el.append(h("div", "panel", this.titleBar("QUESTS", "Close (Q)"), h("div", "cols", this.left, this.detail, this.itemsPane)));

    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    on(this.m.quests.onChange((c: QuestChange) => this.changed(c)));
    on(this.m.gameInfo.onChange((kind, index) => {
      const want = kind === "item" ? "ITEM" : kind === "monster" ? "MONSTER" : kind === "npc" ? "NPC" : null;
      if (kind === "item") this.itemsDirty = true;
      if ((want && this.waiting.has(`${want}:${index}`)) || kind === "map" || kind === "item" || kind === "npc") { this.detailDirty = true; this.invalidate(); }
    }));
    on(this.m.group.changed.on(() => { this.detailDirty = true; this.invalidate(); }));
    on(this.m.npcSession.opened.on(() => { this.detailDirty = true; this.invalidate(); }));
    on(this.m.npcSession.closed.on(() => { this.detailDirty = true; this.invalidate(); }));
    on(this.m.currentMap.changed.on((k) => { if (k === "map") { this.listDirty = this.detailDirty = true; this.invalidate(); } }));
    this.everyShown(1000, () => this.tickTimers());
  }

  private changed(c: QuestChange): void {
    if (c === "quests") this.pending.clear();
    if (c === "items") { this.itemsDirty = true; if (this.open) this.renderSeg(); }
    else if (c === "progress" || c === "names") this.detailDirty = this.listDirty = true;
    else if (c !== "user") this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  /** A new NPC was met (the watch hook): NPC lines may change. */
  npcsChanged(): void {
    this.detailDirty = true;
    this.invalidate();
  }

  /** The HUD tracker's click / a toast: the Active tab with this quest selected. */
  focusQuest(quest: number): void {
    this.setTab(this.m.quests.run(quest) ? "active" : this.m.quests.history(quest) ? "completed" : "available");
    if (this.query) { this.search.set(""); this.query = ""; }
    const t = this.tab === "items" ? "active" : this.tab;
    this.picked[t] = quest;
    this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  protected override closed(): void { this.ui.tooltip.hide(); }

  private setTab(t: Tab): void {
    if (t === this.tab) return;
    this.tab = t;
    this.ui.tooltip.hide();
    this.listDirty = this.detailDirty = this.itemsDirty = true;
    this.invalidate();
  }

  // — Data —

  /** The NPC with this ObjectID: met this game (S.ObjectNPC), on the current map's NPC list, or in view. */
  private npc(id: number): MetNpc | null {
    if (!id) return null;
    const met = metNpcs(this.ui.rt).get(id);
    if (met) return met;
    const cm = this.m.currentMap;
    const n = this.m.gameInfo.map(cm.index)?.NPCs.find((x) => x.ObjectID === id);
    if (n) return { name: n.Name, map: n.MapIndex, x: n.Location.X, y: n.Location.Y };
    const ob = this.ui.ctx.world.objects.get(id);
    return ob ? { name: ob.name, map: cm.index, x: ob.location.X, y: ob.location.Y } : null;
  }
  /** An endpoint NPC (NPCInfo index): its info (asked once when missing). */
  private endpointNpc(index: number): MetNpc | null {
    const gi = this.m.gameInfo, n = gi.npc(index);
    if (!n) { gi.requestNPC(index); return null; }
    return { name: n.Name, map: n.MapIndex, x: n.Location.X, y: n.Location.Y };
  }
  private mapTitle(index: number): string {
    const cm = this.m.currentMap;
    if (index === cm.index) return cm.title;
    const rec = this.m.gameInfo.map(index);
    if (!rec && index > 0) this.m.gameInfo.requestMap(index);
    return rec?.Title ?? "";
  }
  /** "Speak to" names: the Finish endpoints (Start when none) for a run, the Start endpoints for an offer. */
  private who(def: QuestDefinitionInfo | undefined, role: number): string {
    const names = def ? this.m.quests.endpointNames(def, role) : [];
    return names.length ? names.map((n) => npcName(n)[0]).join(" or ") : role === QuestEndpointRole.Finish ? "the quest giver" : "the quest giver";
  }

  /** The tab's rows before the search. */
  private entries(t: Exclude<Tab, "items">): Entry[] {
    const q = this.m.quests;
    if (t === "active") {
      const out: Entry[] = [];
      for (const r of q.runs()) {
        const def = q.def(r.quest);
        if (def && (def.Flags & QuestFlags.HiddenInLog) !== 0) continue;
        out.push({ quest: r.quest, npc: 0, offer: null });
      }
      return out;
    }
    if (t === "completed") return q.historyRows().map((r) => ({ quest: r.quest, npc: 0, offer: null }));
    const out: Entry[] = q.offers.map((o) => ({ quest: o.quest, npc: 0, offer: o }));
    for (const o of q.availableOffers()) if (!out.some((e) => e.quest === o.quest)) out.push({ quest: o.quest, npc: o.npc, offer: null });
    return out;
  }

  private matches(e: Entry): boolean {
    const s = this.query;
    if (!s) return true;
    const q = this.m.quests, def = q.def(e.quest);
    if (q.name(e.quest).toLowerCase().includes(s)) return true;
    if (!def) return false;
    if (def.GroupName.toLowerCase().includes(s) || def.Summary.toLowerCase().includes(s)) return true;
    return def.Endpoints.some((p) => p.Name.toLowerCase().includes(s));
  }

  private category(quest: number): number {
    return this.m.quests.def(quest)?.Category ?? 1;
  }

  private selected(t: Exclude<Tab, "items">, list: Entry[]): Entry | null {
    return list.find((e) => e.quest === this.picked[t]) ?? list[0] ?? null;
  }

  // — Render (once a frame at most, only while open; regions separately) —

  protected override render(): void {
    this.renderSeg();
    const tab = this.tab;
    this.detail.hidden = tab === "items";
    this.itemsPane.hidden = tab !== "items";
    if (tab === "items") {
      if (this.listDirty) { this.listDirty = false; this.renderItemsSide(); }
      if (this.itemsDirty) { this.itemsDirty = false; this.renderItems(); }
      return;
    }
    const list = this.entries(tab).filter((e) => this.matches(e));
    if (this.listDirty) { this.listDirty = false; this.renderList(tab, list); }
    if (this.detailDirty) { this.detailDirty = false; this.renderDetail(tab, this.selected(tab, list)); }
  }

  private renderSeg(): void {
    const q = this.m.quests;
    const counts: Record<Tab, number> = {
      active: this.entries("active").length, available: this.entries("available").length, completed: q.historyRows().length,
      items: q.questInventory.reduce((n, it) => n + (it ? 1 : 0), 0),
    };
    for (const [t, { b, n }] of this.segBtns) {
      b.classList.toggle("on", t === this.tab);
      b.setAttribute("aria-selected", String(t === this.tab));
      setText(n, String(counts[t]));
    }
    this.search.el.hidden = this.tab === "items";
  }

  private renderList(t: Exclude<Tab, "items">, list: Entry[]): void {
    const q = this.m.quests;
    const sel = this.selected(t, list);
    const out: HTMLElement[] = [];
    if (!list.length) {
      out.push(h("div", "empty", this.query ? "No quests match."
        : t === "active" ? "No quests taken. NPCs with a ! over their head have quests for you."
        : t === "available" ? "No quests offered by the NPCs you have met. Look for a ! over an NPC's head."
        : "No quests completed yet."));
    }
    for (const cat of QUEST_CATEGORY_ORDER) {
      const group = list.filter((e) => this.category(e.quest) === cat);
      if (!group.length) continue;
      const label = QUEST_CATEGORY_LABEL[cat] ?? "";
      const key = `${t}:${cat}`, isShut = this.shut.has(key);
      const head = asButton(h("div", "grp" + (isShut ? " shut" : ""), h("span", "chev", "▾"), svg(CAT_ICON[cat] ?? "scroll", "qu-ic"),
        label, h("span", "", String(group.length))), `${isShut ? "Show" : "Fold"} ${label} quests`);
      head.addEventListener("click", () => {
        if (this.shut.has(key)) this.shut.delete(key); else this.shut.add(key);
        this.listDirty = true; this.invalidate();
      });
      out.push(head);
      if (!isShut) for (const e of group) out.push(this.row(t, e, e === sel));
    }
    this.list.replaceChildren(...out);

    if (t === "active") {
      const ready = q.runs().filter((r) => r.status === QuestRunStatus.Ready).length;
      this.foot.replaceChildren(h("span", "", h("b", "", String(ready)), " ready · ", h("b", "", `${q.activeCount}/${MAX_CONCURRENT_QUESTS}`)),
        h("span", "", "Tracked ", h("b", "", `${q.trackedIds.length} / ${MAX_TRACKED_QUESTS}`)));
    } else if (t === "available") {
      this.foot.replaceChildren(h("span", "", "Take these from their NPC"));
    } else this.foot.replaceChildren(h("span", "", h("b", "", String(q.historyRows().length)), " completed"));
  }

  private row(t: Exclude<Tab, "items">, e: Entry, isSel: boolean): HTMLElement {
    const q = this.m.quests, quest = e.quest, def = q.def(quest), run = q.run(quest), hist = q.history(quest);
    const ready = run?.status === QuestRunStatus.Ready, failed = run?.status === QuestRunStatus.Failed;
    const marker = t === "completed" ? null : questMarker(q, quest);
    const mk = h("span", "mk", marker?.text ?? "");
    if (marker) mk.style.color = marker.colour;
    let sub = "";
    if (t === "active") {
      if (failed) sub = "Failed";
      else if (ready) sub = `Return to ${this.who(def, QuestEndpointRole.Finish)}`;
      else {
        const step = q.currentStep(quest), objs = def && step ? q.shownObjectives(def, step.Index) : [];
        const open = objs.find((o) => !o.Optional && !q.done(o)) ?? objs[0];
        sub = step?.Title || (open ? q.objectiveText(open) : def?.GroupName ?? "");
      }
    } else if (t === "available") sub = e.offer ? (e.offer.sharedBy ? `Shared by ${e.offer.sharedBy}` : "Offered to you") : npcName(this.npc(e.npc)?.name ?? "")[0];
    else if (hist) sub = hist.availableAt > Date.now() ? `Again in ${fmtDuration((hist.availableAt - Date.now()) / 1000)}` : hist.times > 1 ? `Completed ${hist.times} times` : `Completed ${fmtAgo(hist.lastAt)}`;
    const lv = def && def.LevelHint > 0 ? `Lv ${def.LevelHint}` : "";
    const parts: HTMLElement[] = [mk, h("div", "mid", h("div", "nm", q.name(quest)), h("div", "sub", [lv, sub].filter(Boolean).join(" · ")))];
    if (ready) parts.push(h("span", "pill hot", "Ready"));
    else if (failed) parts.push(h("span", "pill bad", "Failed"));
    if (t === "active" && !failed) {
      const on = q.isTracked(quest), full = !on && q.trackedIds.length >= MAX_TRACKED_QUESTS;
      const pin = asButton(h("div", "pin" + (on ? " on" : "") + (full ? " full" : ""), svg("pin", "qu-ic")),
        on ? "Untrack" : full ? `You can track at most ${MAX_TRACKED_QUESTS} quests` : "Track on screen");
      pin.addEventListener("click", (ev) => { ev.stopPropagation(); this.toggleTrack(quest); });
      parts.push(pin);
    }
    const low = t !== "completed" && !!def && def.LevelHint > 0 && q.level - def.LevelHint > 10;
    const r = h("div", "row" + (isSel ? " sel" : "") + (ready ? " ready" : "") + (failed ? " failed" : "") + (t === "completed" ? " done" : "")
      + (low ? " low" : "") + (this.pending.has(quest) ? " busy" : ""), ...parts);
    r.addEventListener("click", () => {
      if (this.picked[t] === quest) return;
      this.picked[t] = quest;
      for (const x of Array.from(this.list.querySelectorAll(".row.sel"))) x.classList.remove("sel");
      r.classList.add("sel");
      this.detailDirty = true; this.invalidate();
    });
    return r;
  }

  private renderDetail(t: Exclude<Tab, "items">, e: Entry | null): void {
    this.ui.tooltip.hide();
    this.waiting.clear();
    this.timers = [];
    if (!e) { this.detail.replaceChildren(h("div", "empty", "Pick a quest.")); return; }
    const q = this.m.quests, quest = e.quest, def = q.def(quest), run = q.run(quest), hist = q.history(quest);
    if (!def) {
      q.requestDefinition(quest); // a history quest: C.QuestDefinitionRequest (answered for runs, history and offers)
      this.detail.replaceChildren(h("div", "dscroll", h("div", "dhead", h("h3", "", q.name(quest))),
        hist ? this.historyLine(hist.times, hist.lastAt, hist.availableAt) : h("div", "note", "Loading the quest…")), h("div", "actions"));
      return;
    }
    const ready = run?.status === QuestRunStatus.Ready, failed = run?.status === QuestRunStatus.Failed;

    const tags = h("div", "tags", h("span", "pill", QUEST_CATEGORY_LABEL[def.Category] ?? ""));
    if (def.LevelHint > 0) tags.append(h("span", "pill", `Level ${def.LevelHint}`));
    if (def.GroupName) tags.append(h("span", "pill", def.GroupName));
    if (def.RepeatPolicy !== QuestRepeatPolicy.Once) tags.append(h("span", "pill", ["", "Daily", "Weekly", "Cooldown", "Repeatable"][def.RepeatPolicy] ?? ""));
    if (ready) tags.append(h("span", "pill hot", "Ready to turn in"));
    else if (failed) tags.append(h("span", "pill bad", "Failed"));
    else if (run) tags.append(h("span", "pill", "In progress"));
    if (t === "completed") tags.append(h("span", "pill", "Completed"));
    if (run && q.isTracked(quest)) tags.append(h("span", "pill", "Tracked"));

    const giver = h("div", "giver", this.npcLine("From", def, QuestEndpointRole.Start));
    const finish = q.endpointNpcs(def, QuestEndpointRole.Finish), start = q.endpointNpcs(def, QuestEndpointRole.Start);
    if (finish.join() !== start.join()) giver.append(this.npcLine("Turn in to", def, QuestEndpointRole.Finish));
    const parts: HTMLElement[] = [h("div", "dhead", h("h3", "", svg(CAT_ICON[def.Category] ?? "scroll", "qu-ic"), def.Name), tags, giver)];

    if (def.Summary) parts.push(this.text(def.Summary, "desc"));
    if (t === "completed") {
      if (def.CompleteText) parts.push(this.text(def.CompleteText));
      if (hist) parts.push(this.historyLine(hist.times, hist.lastAt, hist.availableAt));
    } else if (run) parts.push(...this.runParts(def, run.step, ready, failed));
    else {
      const first = def.Steps[0];
      if (first) parts.push(this.stepBlock(def, first.Index, false));
      if (def.TimeLimitSeconds > 0) parts.push(h("div", "", h("div", "lbl", "Time limit"), h("div", "timer", printTimeSpanFromSeconds(def.TimeLimitSeconds))));
    }
    const carried = rewardsBlock(this.ui, q, def, { scope: QuestRewardScope.Accept, title: "You will carry" });
    if (carried && !run && t !== "completed") parts.push(carried);
    const rewards = rewardsBlock(this.ui, q, def, { pick: !!run && !failed && t === "active" });
    parts.push(rewards ?? h("div", "", h("div", "lbl", "Rewards"), h("div", "note", "No reward listed.")));
    this.detail.replaceChildren(h("div", "dscroll", ...parts), this.actions(t, e, def));
    this.tickTimers();
  }

  /** The run's step list, current step (text, objectives, timers, branches). */
  private runParts(def: QuestDefinitionInfo, stepId: number, ready: boolean, failed: boolean): HTMLElement[] {
    const q = this.m.quests, run = q.run(def.Quest)!;
    const out: HTMLElement[] = [];
    const cur = stepId >= 0 ? q.step(def, stepId) : undefined;
    if (def.Steps.length > 1) {
      const list = h("div", "steps");
      for (const s of def.Steps) {
        const done = stepId < 0 || (cur ? s.Order < cur.Order : false), isCur = s === cur;
        list.append(h("div", "step" + (done ? " done" : "") + (isCur ? " cur" : ""), h("b", "", done ? "✓" : isCur ? "▸" : "·"), s.Title || `Step ${s.Order}`));
      }
      out.push(h("div", "", h("div", "lbl", "Steps"), list));
    }
    if (failed) out.push(h("div", "note", "This quest failed. Remove it to take it again (if it can be repeated)."));
    if (cur) {
      const text = ready && cur.ReadyText ? cur.ReadyText : cur.Text;
      if (text) out.push(this.text(text));
      out.push(this.stepBlock(def, cur.Index, true));
    } else if (ready) {
      const last = def.Steps[def.Steps.length - 1];
      if (last?.ReadyText) out.push(this.text(last.ReadyText));
    }
    if (run.expiresAt || run.stepExpiresAt) {
      const box = h("div", "", h("div", "lbl", "Time left"));
      if (run.expiresAt) { const el = h("div", "timer"); this.timers.push([el, run.expiresAt, run.stepExpiresAt ? "Quest: " : ""]); box.append(el); }
      if (run.stepExpiresAt) { const el = h("div", "timer"); this.timers.push([el, run.stepExpiresAt, run.expiresAt ? "Step: " : ""]); box.append(el); }
      out.push(box);
    }
    const branches = q.pendingBranches(def.Quest);
    if (branches.length) {
      const s = this.m.npcSession;
      const atNpc = s.open && (q.npcQuests(s.npcId)?.finishing.includes(def.Quest) ?? false);
      const row = h("div", "branches");
      for (const b of branches) {
        const btn = button(b.Text || b.ChoiceKey, () => this.choose(def.Quest, b.ChoiceKey), { cls: "main" });
        if (!atNpc) btn.setDisabled(true, `Speak to ${this.who(def, QuestEndpointRole.Finish)} to choose`);
        else if (this.pending.has(def.Quest)) btn.setDisabled(true, "Waiting for the server");
        row.append(btn.el);
      }
      out.push(h("div", "", h("div", "lbl", "Choose"), row));
    }
    return out;
  }

  /** A step's objectives (live values for the current step). */
  private stepBlock(def: QuestDefinitionInfo, stepId: number, live: boolean): HTMLElement {
    const q = this.m.quests;
    const box = h("div", "", h("div", "lbl", live ? "Objectives" : "Tasks"));
    const objs = live ? q.shownObjectives(def, stepId) : q.objectives(def, stepId).filter((o) => !o.Hidden);
    for (const o of objs) box.append(objectiveRow(q, o, live));
    if (!objs.length) box.append(h("div", "note", live ? "Nothing more to do on this step." : "Speak to the quest giver to learn more."));
    const step = q.step(def, stepId);
    if (step && step.TimeLimitSeconds > 0 && !live) box.append(h("div", "note", `Step time limit: ${printTimeSpanFromSeconds(step.TimeLimitSeconds)}`));
    return box;
  }

  private historyLine(times: number, lastAt: number, availableAt: number): HTMLElement {
    const box = h("div", "", h("div", "lbl", "History"), h("div", "txt", `Completed ${times === 1 ? "once" : `${times} times`}, last ${fmtAgo(lastAt)}.`));
    if (availableAt > 0) { const el = h("div", "timer"); this.timers.push([el, availableAt, "Available again in "]); box.append(el); }
    return box;
  }

  /** Every second while shown: the timer labels of the detail (no rebuild). */
  private tickTimers(): void {
    const now = Date.now();
    for (const [el, end, prefix] of this.timers) {
      const left = Math.max(0, (end - now) / 1000);
      setText(el, left > 0 ? prefix + printTimeSpanFromSeconds(left) : prefix ? prefix + "0s" : "0s");
      el.classList.toggle("low", left > 0 && left < 60);
    }
  }

  private npcLine(label: string, def: QuestDefinitionInfo, role: number): HTMLElement {
    const ids = this.m.quests.endpointNpcs(def, role);
    const names = this.m.quests.endpointNames(def, role);
    const n = ids.length ? this.endpointNpc(ids[0]!) : null;
    const where = n ? this.mapTitle(n.map) : "";
    return h("span", "", `${label} `, h("b", "", names.length ? names.map((x) => npcName(x)[0]).join(" or ") : "—"),
      n && where ? ` · ${where} (${n.x}, ${n.y})` : "");
  }

  private text(body: string, cls = ""): HTMLElement {
    const t = h("div", "txt" + (cls ? " " + cls : ""));
    const lines = body.split(/\r?\n/);
    lines.forEach((l, i) => { if (i) t.append("\n"); t.append(...this.markup(l)); });
    return t;
  }

  /** A quest text row: {text/Colour} colours and info links (item tooltip on hover); page / web links as plain text. */
  private markup(line: string): Node[] {
    const out: Node[] = [];
    for (const seg of parseLine(line)) {
      switch (seg.kind) {
        case "colour": {
          const span = h("span", "", seg.text);
          span.style.color = CSS.supports("color", seg.colour) ? seg.colour : "transparent";
          out.push(span);
          break;
        }
        case "info": {
          const span = h("span", "it", this.linkName(seg.link, seg.index, seg.name));
          if (seg.link === "ITEM") {
            const idx = seg.index;
            span.addEventListener("pointerenter", () => {
              const info = this.m.gameInfo.item(idx);
              if (!info) { this.m.gameInfo.requestItem(idx); return; }
              this.ui.tooltip.show(span, displayItem(info, 1), this.ui.labelContext(), { info });
            });
            span.addEventListener("pointerleave", () => this.ui.tooltip.hide());
          }
          out.push(span);
          break;
        }
        default: out.push(document.createTextNode(seg.text));
      }
    }
    return out;
  }

  /** NPCDialog.GetDisplayNameForLink (asks for a missing info once; its arrival redraws the detail). */
  private linkName(kind: NpcLinkKind, idx: number, provided: string | undefined): string {
    if (provided) return provided;
    const gi = this.m.gameInfo;
    switch (kind) {
      case "ITEM": { const i = gi.item(idx); if (i) return itemFriendlyName(i) || i.Name; this.waiting.add(`ITEM:${idx}`); gi.requestItem(idx); return `Item ${idx}`; }
      case "MONSTER": { const m = gi.monster(idx); if (m) return m.GameName || m.Name; this.waiting.add(`MONSTER:${idx}`); gi.requestMonster(idx); return `Monster ${idx}`; }
      case "NPC": { const n = gi.npc(idx); if (n) return npcName(n.Name)[0]; this.waiting.add(`NPC:${idx}`); gi.requestNPC(idx); return `NPC ${idx}`; }
    }
  }

  private actions(t: Exclude<Tab, "items">, e: Entry, def: QuestDefinitionInfo): HTMLElement {
    const q = this.m.quests, quest = e.quest, run = q.run(quest);
    const bar = h("div", "actions");
    const s = this.m.npcSession;
    const pending = this.pending.has(quest);
    const role = run ? QuestEndpointRole.Finish : QuestEndpointRole.Start;
    const find = button("Map", () => this.findOnMap(def, role, e.npc), { icon: "map", title: `Find ${this.who(def, role)} on the map` });

    if (t === "completed") {
      bar.append(find.el, h("span", "note", "Completed"));
      return bar;
    }
    if (t === "available") {
      if (e.offer) {
        const offer = e.offer;
        const acc = button(pending ? "Accepting…" : "Accept", () => this.accept(quest, 0), { cls: "main", icon: "check" });
        if (pending) acc.setDisabled(true, "Waiting for the server");
        else if (q.activeCount >= MAX_CONCURRENT_QUESTS) acc.setDisabled(true, `You can carry at most ${MAX_CONCURRENT_QUESTS} quests`);
        const dec = button("Decline", () => q.dropOffer(offer.quest), { icon: "cross" });
        bar.append(acc.el, dec.el);
        return bar;
      }
      const here = s.open && s.npcId === e.npc && (q.npcQuests(e.npc)?.offered.includes(quest) ?? false);
      if (here) {
        const acc = button(pending ? "Accepting…" : "Accept", () => this.accept(quest, e.npc), { cls: "main", icon: "check" });
        if (pending) acc.setDisabled(true, "Waiting for the server");
        else if (q.activeCount >= MAX_CONCURRENT_QUESTS) acc.setDisabled(true, `You can carry at most ${MAX_CONCURRENT_QUESTS} quests`);
        bar.append(acc.el, find.el);
      } else bar.append(find.el, h("span", "speak", `Speak to ${npcName(this.npc(e.npc)?.name ?? this.who(def, QuestEndpointRole.Start))[0]} to accept`));
      return bar;
    }
    if (!run) return bar;
    const ready = run.status === QuestRunStatus.Ready, failed = run.status === QuestRunStatus.Failed;
    if (failed) {
      const rm: Btn = button(pending ? "Removing…" : "Remove", () => void this.abandon(quest, true), { cls: "warn", icon: "cross", title: "Remove the failed quest from your log" });
      if (pending) rm.setDisabled(true, "Waiting for the server");
      bar.append(rm.el);
      return bar;
    }
    const tracked = q.isTracked(quest);
    const track = button(tracked ? "Untrack" : "Track", () => this.toggleTrack(quest), { icon: "pin", title: tracked ? "Remove from the on-screen tracker" : "Show on the on-screen tracker" });
    if (!tracked && q.trackedIds.length >= MAX_TRACKED_QUESTS) track.setDisabled(true, `You can track at most ${MAX_TRACKED_QUESTS} quests`);
    const share = button(this.shared.has(quest) ? "Shared" : "Share", () => this.share(quest), { icon: "group", title: "Offer this quest to your group members nearby" });
    if (this.m.group.list.length < 2) share.setDisabled(true, "Share with your group: you are not in a group");
    else if (this.shared.has(quest)) share.setDisabled(true, "Shared: wait a moment to share again");
    const abandon: Btn = button(pending ? "Abandoning…" : "Abandon", () => void this.abandon(quest, false), { cls: "warn", icon: "cross", title: "Abandon this quest" });
    if (pending) abandon.setDisabled(true, "Waiting for the server");
    else if (!def.AbandonAllowed) abandon.setDisabled(true, "This quest cannot be abandoned");
    bar.append(track.el, share.el, find.el, abandon.el);
    if (ready) {
      const npc = s.open && (q.npcQuests(s.npcId)?.finishing.includes(quest) ?? false) ? s.npcId : 0;
      if (npc) {
        const picks = q.picksFor(def);
        const turn = button(pending ? "Turning in…" : "Turn in", () => this.turnIn(def, npc), { cls: "main", icon: "check" });
        if (pending) turn.setDisabled(true, "Waiting for the server");
        else if (!picks) turn.setDisabled(true, "Choose a reward first");
        bar.append(turn.el);
      } else bar.append(h("span", "speak", `Speak to ${this.who(def, QuestEndpointRole.Finish)} to turn in`));
    }
    return bar;
  }

  // — Actions (the server answers with S.QuestStates; nothing changes here first) —

  private lock(quest: number): void {
    this.pending.add(quest);
    later(() => { if (this.pending.delete(quest)) { this.listDirty = this.detailDirty = true; this.invalidate(); } }, PENDING_MS, this.signal);
    this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  private accept(quest: number, npc: number): void {
    if (this.pending.has(quest)) return;
    this.lock(quest);
    this.m.quests.accept(quest, npc);
  }

  private turnIn(def: QuestDefinitionInfo, npc: number): void {
    const picks = this.m.quests.picksFor(def);
    if (!picks || this.pending.has(def.Quest)) return;
    this.lock(def.Quest);
    this.m.quests.turnIn(def.Quest, npc, picks);
  }

  private choose(quest: number, key: string): void {
    const s = this.m.npcSession;
    if (!s.open || this.pending.has(quest)) return;
    this.lock(quest);
    this.m.quests.choose(quest, s.npcId, key);
  }

  private toggleTrack(id: number): void {
    const q = this.m.quests;
    if (q.isTracked(id)) q.untrackQuest(id);
    else if (q.trackedIds.length < MAX_TRACKED_QUESTS) q.trackQuest(id);
  }

  private share(id: number): void {
    if (this.shared.has(id)) return;
    this.m.quests.share(id);
    this.shared.add(id);
    later(() => { this.shared.delete(id); this.detailDirty = true; this.invalidate(); }, SHARE_REST_MS, this.signal);
    this.detailDirty = true; this.invalidate();
  }

  private async abandon(quest: number, failed: boolean): Promise<void> {
    if (this.pending.has(quest)) return;
    const name = this.m.quests.name(quest);
    const ok = failed || await this.ui.rt.popups.ask(`Are you sure you want to cancel the quest "${name}"?`, { title: "Abandon quest", kind: "warning", okCancel: true });
    if (!ok || this.pending.has(quest) || !this.m.quests.run(quest)) return;
    this.lock(quest);
    this.m.quests.abandon(quest);
  }

  /** Opens the big map; when the NPC is on another map (or unknown), a note says where it is. */
  private findOnMap(def: QuestDefinitionInfo, role: number, objectId: number): void {
    const ids = this.m.quests.endpointNpcs(def, role);
    const n = (objectId ? this.npc(objectId) : null) ?? (ids.length ? this.endpointNpc(ids[0]!) : null);
    this.ui.openWindow("bigMap", true);
    if (!n) return;
    if (n.map !== this.m.currentMap.index) {
      const where = this.mapTitle(n.map) || "another map";
      void this.ui.rt.popups.confirm({ title: "Find on map", text: `${npcName(n.name)[0]} is in ${where} at (${n.x}, ${n.y}).`, buttons: "ok", kind: "info" });
    }
  }

  // — Quest items (InventoryDialog.QuestGrid, display only) —

  private renderItemsSide(): void {
    this.list.replaceChildren(h("div", "empty", "Items for your quests. They cannot be moved, sold or dropped; they go when the quest ends."));
    this.foot.replaceChildren();
  }

  private renderItems(): void {
    const qi = this.m.quests.questInventory;
    const n = Math.max(QUEST_INVENTORY_SIZE, qi.length);
    while (this.icells.length < n) { const c = this.showCell(40); this.icells.push(c); this.igrid.append(c.el); }
    let filled = 0;
    for (let i = 0; i < this.icells.length; i++) {
      const c = this.icells[i]!, item = i < n ? qi[i] ?? null : null;
      c.el.hidden = i >= n;
      const info = item ? this.m.itemInfos.get(item.ItemIndex) ?? null : null;
      if (item && !info) this.m.gameInfo.requestItem(item.ItemIndex);
      if (item) filled++;
      this.setCell(c, item, info, !!item && !!info && info.StackSize > 1);
    }
    setText(this.inote, filled ? `${filled} item${filled === 1 ? "" : "s"}` : "No quest items.");
  }

  private showCell(size: number): ShowCell {
    const icon = h("i"), count = h("b");
    count.hidden = true;
    const el = h("div", "icell ro", icon, count);
    el.style.setProperty("--cs", size + "px");
    const c: ShowCell = { el, icon, count, img: -2, item: null, info: null };
    el.addEventListener("pointerenter", () => { if (c.item) this.ui.tooltip.show(el, c.item, this.ui.labelContext(), c.info ? { info: c.info } : {}); }, { signal: this.signal });
    el.addEventListener("pointerleave", () => this.ui.tooltip.hide(), { signal: this.signal });
    return c;
  }

  private setCell(c: ShowCell, item: UserItem | null, info: ItemInfo | null, stack: boolean): void {
    c.item = item;
    c.info = info;
    c.el.classList.toggle("has", !!item);
    c.count.hidden = !stack;
    if (stack && item) setText(c.count, String(item.Count));
    if (item && info) c.el.setAttribute("aria-label", info.Name); else c.el.removeAttribute("aria-label");
    const image = item && info ? info.Image : -1;
    if (image === c.img) return;
    c.img = image;
    if (image < 0) { c.icon.style.backgroundImage = ""; return; }
    void libPicture(Lib.Items, image).then((pic) => { if (c.img === image && !this.signal.aborted) c.icon.style.backgroundImage = pic ? `url(${pic.url})` : ""; });
  }
}

registerWindow(QUESTS_WINDOW, (host) => new QuestsWindow(host), {
  // At UI start: remember the NPCs that come into view (NPC lines and Find on map use them), and each S.QuestOffer as a
  // request toast (Accept = C.QuestAccept with NpcObjectID 0; Decline / timeout forgets it).
  watch: (host) => {
    const sig = host.rt.signal, met = metNpcs(host.rt), quests = host.ctx.models.quests;
    const offOffer = quests.offered.on((o) => {
      const name = quests.name(o.quest);
      const from = o.sharedBy || "Quest";
      const text = o.sharedBy ? `would like to share the quest "${name}" with you.` : `New quest: "${name}". Accept it?`;
      void host.rt.popups.request({ from, tag: "Quest", text, seconds: OFFER_SECONDS })
        .then((yes) => {
          if (sig.aborted || !quests.offers.some((x) => x.quest === o.quest)) return;
          if (yes) quests.accept(o.quest, 0); else quests.dropOffer(o.quest);
        });
    });
    const offPackets = host.packets.on((p) => {
      if (p.type !== "ObjectNPC") return;
      const map = host.ctx.models.currentMap.index, was = met.get(p.ObjectID);
      met.set(p.ObjectID, { name: p.Name, map, x: p.Location.X, y: p.Location.Y });
      if (!was || was.map !== map || was.name !== p.Name) host.rt.peekWindow<QuestsWindow>(QUESTS_WINDOW)?.npcsChanged();
    });
    return () => { offOffer(); offPackets(); };
  },
});
