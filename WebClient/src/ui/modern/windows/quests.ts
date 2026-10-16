// Ported from play/hudQuests.ts (QuestsMock, its window part; the on-screen tracker is hud/tracker.ts): the new UI's
// quest log over the real Quests model (src/game/quests.ts). Desktop counterparts: QuestDiaryDialog + QuestDetailDialog
// + the InventoryDialog quest grid + GameScene.ShareQuest (Client/MirScenes/Dialogs/QuestDialogs.cs); the classic port is
// src/ui/questWindow.ts (its rules are kept). Changes from the mock:
//  - Active = Quests.currentEntries() (User.CurrentQuests), Available = the quests the user could take now
//    (Quests.canAccept, not completed, not taken) from NPCs the user has met this session (the NPC came into view:
//    S.ObjectNPC, or is on the current map's NPC list), Completed = User.CompletedQuests looked up in QuestInfoList;
//    each grouped by ClientQuestInfo.Type (Story / General / Daily / Repeatable) in folding groups, with a debounced
//    search (name, group, NPC, description);
//  - the detail is the QuestMessage text (Description, Tasks = TaskDescription, Return, Time limit, Progress =
//    ClientQuestProgress.TaskList) as TEXT lines with the quest markup ({text/Colour}, [ITEM|MONSTER|NPC:n(|name)] info
//    links by src/game/npcMarkup.ts parseLine): no parsed per-objective bars (TaskList is localised text). Rewards: EXP /
//    gold / credit chips, the fixed items and the choose-one items (the user's gender only, QuestRewards.FilterRewards):
//    the pick itself happens at the NPC;
//  - actions: Track / Untrack (Quests.trackQuest, at most MAX_TRACKED_QUESTS, saved per character by the model; the HUD
//    tracker follows), Share (C.ShareQuest: group members nearby; off without a group), Abandon (C.AbandonQuest after a
//    question; the row stays locked until S.ChangeQuest, 5 s fallback), Find on map (the giver / turn-in NPC's map and
//    cell: opens the big map, plus a note of where the NPC is when it is on another map);
//  - Accept and Turn in are NOT here: the server needs the quest NPC in range (AcceptQuest / FinishQuest), so the window
//    says "Speak to <NPC>" and the NPC dialogue's Quests tab (windows/npc.ts) does them;
//  - an Items tab shows User.QuestInventory (display only, item tooltips);
//  - the incoming shared-quest invitation (S.ShareQuest) is a request toast wired at UI start (the registry's watch
//    hook; Accept = C.AcceptQuest with NPCIndex 0, like the classic Yes); the same hook remembers the NPCs met.
// HIDDEN (Crystal has no data for them): the completed date, objective kinds / have-need bars, the mock's kill
// simulation, local accept / turn-in / abandon state changes and its own tracked flags.
// Narrow layout (a layer below 1040 px): a slimmer list column.

import type { ClientQuestInfo, ItemInfo, QuestItemReward, UserItem } from "../../../protocol/server.ts";
import { MirGender, QuestType, QuestTypeName, RequiredGender } from "../../../protocol/enums.generated.ts";
import {
  MAX_TRACKED_QUESTS, QUEST_INVENTORY_SIZE, entryIcon, printTimeSpanFromSeconds, questIconFor, questMiniMapMarker, sameFinishNPC,
  type QuestChange, type QuestEntry,
} from "../../../game/quests.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { parseLine, type NpcLinkKind } from "../../../game/npcMarkup.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, asButton, button, fmt, h, injectCss, later, npcName, searchBox, setText, svg, type Btn, type IconName,
} from "../core/index.ts";
import type { UiRuntime } from "../core/runtime.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.quests). */
export const QUESTS_WINDOW = "quests";

/** Seconds the shared-quest toast stays up (the desktop's box waits; the toast declines when it runs out). */
const SHARE_SECONDS = 30;
/** A refused abandon sends nothing back: the row unlocks after this. */
const PENDING_MS = 5000;
/** Share again after this (the server answers a failed share with a chat line only). */
const SHARE_REST_MS = 3000;

type Tab = "active" | "available" | "completed" | "items";
const TABS: readonly [Tab, string][] = [["active", "Active"], ["available", "Available"], ["completed", "Completed"], ["items", "Items"]];
/** Display order of the groups (the mock's). */
const TYPES: readonly number[] = [QuestType.Story, QuestType.General, QuestType.Daily, QuestType.Repeatable];
const TYPE_ICON: Readonly<Record<number, IconName>> = {
  [QuestType.Story]: "star", [QuestType.General]: "scroll", [QuestType.Daily]: "sun", [QuestType.Repeatable]: "repeat",
};
/** InventoryDialog.QuestGrid: 8 x 5. */
const ITEM_COLS = 8;

const STYLE = `
.hudm .qwin { --winh: 476px; width: 660px; }
.hudm.narrow .qwin { width: 600px; }
.hudm .qwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .qwin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .qwin .left { width: 300px; flex: none; display: flex; flex-direction: column; padding: 10px 8px 8px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .qwin .left { width: 250px; }
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
.hudm .qwin .row .mk { width: 12px; flex: none; text-align: center; font-weight: 800; font-size: 12px; }
.hudm .qwin .row .mid { flex: 1; min-width: 0; }
.hudm .qwin .row .nm { font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .qwin .row.done .nm { color: var(--dim); }
.hudm .qwin .row .sub { margin-top: 3px; font-size: 10px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .qwin .row.low .nm { color: var(--dim); }
.hudm .qwin .row.busy { opacity: .6; cursor: progress; }
.hudm .qwin .pill { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; white-space: nowrap;
  color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .qwin .pill.hot { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, transparent); }
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
.hudm .qwin .txt { font-size: 12px; line-height: 1.55; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); }
.hudm .qwin .txt > div { min-height: 1.55em; overflow-wrap: anywhere; }
.hudm .qwin .txt.desc { font-style: italic; }
.hudm .qwin .txt .it { color: #4fd8e8; cursor: help; }
.hudm .qwin .txt .it:hover { color: #ffa500; }
.hudm .qwin .obj { padding: 5px 8px; margin-bottom: 4px; border-radius: calc(var(--rad) * .7); background: color-mix(in srgb, var(--frl) 6%, transparent); font-size: 12px; overflow-wrap: anywhere; }
.hudm .qwin .rew { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.hudm .qwin .chip { height: 24px; padding: 0 10px; display: inline-flex; align-items: center; gap: 6px; font-size: 12px; border-radius: 12px;
  background: color-mix(in srgb, var(--frl) 8%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .qwin .chip span { color: var(--dim); font-size: 10px; font-weight: 700; letter-spacing: .08em; }
.hudm .qwin .chip b { color: var(--tx); font-variant-numeric: tabular-nums; }
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

/** A UserItem standing for an ItemInfo (QuestCell.OnMouseEnter: MaxDura = CurrentDura = Durability). */
function displayItem(info: ItemInfo, count: number): UserItem {
  return {
    UniqueID: 0n, ItemIndex: info.Index, CurrentDura: info.Durability, MaxDura: info.Durability, Count: Math.max(1, count), SoulBoundId: -1,
    Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [], RefinedValue: 0, RefineAdded: 0,
    RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false, Sealed: null, GMMade: false,
  };
}

/** A display-only item cell (core item cell styling) for the quest items and rewards. */
interface ShowCell { el: HTMLElement; icon: HTMLElement; count: HTMLElement; img: number; item: UserItem | null; info: ItemInfo | null }


class QuestsWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: Tab = "active";
  /** The selected quest per tab (QuestInfo.Index; -1 = the first). */
  private readonly picked: Record<Exclude<Tab, "items">, number> = { active: -1, available: -1, completed: -1 };
  /** Folded groups ("tab:type"). */
  private readonly shut = new Set<string>();
  private query = "";
  /** Quests with an abandon on its way (until S.ChangeQuest or PENDING_MS). */
  private readonly pending = new Set<number>();
  /** Quests just shared (the button rests SHARE_REST_MS). */
  private readonly shared = new Set<number>();
  /** Info links the detail waits for ("ITEM:12"): an arrival redraws it. */
  private readonly waiting = new Set<string>();
  // Regions redrawn on their own (Review-Feature-Windows "Improvements": no whole-window rebuild per change).
  private listDirty = true;
  private detailDirty = true;
  private itemsDirty = true;

  // — DOM, built once —
  private readonly segBtns = new Map<Tab, { b: HTMLButtonElement; n: HTMLElement }>();
  private readonly search: ReturnType<typeof searchBox>;
  private readonly list = h("div", "list");
  private readonly foot = h("div", "foot");
  private readonly left: HTMLElement;
  private readonly detail = h("div", "detail");
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

    // Model events: mark the regions stale; render once a frame while shown, else on the next open.
    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    on(this.m.quests.onChange((c: QuestChange) => this.changed(c)));
    on(this.m.gameInfo.onChange((kind, index) => {
      const want = kind === "item" ? "ITEM" : kind === "monster" ? "MONSTER" : kind === "npc" ? "NPC" : null;
      if (kind === "item") this.itemsDirty = true;
      if ((want && this.waiting.has(`${want}:${index}`)) || kind === "map" || kind === "item") { this.detailDirty = true; this.invalidate(); }
    }));
    on(this.m.group.changed.on(() => { this.detailDirty = true; this.invalidate(); }));
    on(this.m.currentMap.changed.on((k) => { if (k === "map") { this.listDirty = this.detailDirty = true; this.invalidate(); } }));
  }

  private changed(c: QuestChange): void {
    if (c === "quests") this.pending.clear();
    if (c === "items") { this.itemsDirty = true; if (this.open) this.renderSeg(null); }
    else this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  /** A new NPC was met (the watch hook): the Available tab and the NPC lines may change. */
  npcsChanged(): void {
    this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  /** The HUD quest tracker's click (hud/tracker.ts): the Active tab with this quest (QuestInfo.Index) selected. */
  focusQuest(questIndex: number): void {
    this.setTab("active");
    if (this.query) { this.search.set(""); this.query = ""; } // a search would hide it
    else if (this.picked.active === questIndex) return;
    this.picked.active = questIndex;
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
    const met = metNpcs(this.ui.rt).get(id);
    if (met) return met;
    const cm = this.m.currentMap;
    const n = this.m.gameInfo.map(cm.index)?.NPCs.find((x) => x.ObjectID === id);
    if (n) return { name: n.Name, map: n.MapIndex, x: n.Location.X, y: n.Location.Y };
    const ob = this.ui.ctx.world.objects.get(id);
    return ob ? { name: ob.name, map: cm.index, x: ob.location.X, y: ob.location.Y } : null;
  }
  private npcLabel(id: number): string {
    const n = this.npc(id);
    return n ? npcName(n.name)[0] : "the quest giver";
  }
  private mapTitle(index: number): string {
    const cm = this.m.currentMap;
    if (index === cm.index) return cm.title;
    const rec = this.m.gameInfo.map(index);
    if (!rec && index > 0) this.m.gameInfo.requestMap(index);
    return rec?.Title ?? "";
  }

  /** The tab's quests (unbound ones skipped), before the search. */
  private entries(t: Exclude<Tab, "items">): QuestEntry[] {
    const q = this.m.quests;
    if (t === "active") return q.currentEntries();
    if (t === "completed") {
      const out: QuestEntry[] = [];
      for (const id of q.completed) {
        const info = q.info(id);
        if (info) out.push({ info, progress: { Id: id, TaskList: [], Taken: true, Completed: true, New: false } });
      }
      return out;
    }
    // Available: NPCObject.GetAvailableQuests' accept rule (CanAccept, not completed, not taken) for NPCs met.
    const out: QuestEntry[] = [];
    const taken = new Set(q.current.map((p) => p.Id)), done = new Set(q.completed);
    for (const info of this.allInfos()) {
      if (taken.has(info.Index) || done.has(info.Index) || !q.canAccept(info) || !this.npc(info.NPCIndex)) continue;
      out.push({ info, progress: { Id: info.Index, TaskList: [], Taken: false, Completed: false, New: false } });
    }
    return out;
  }

  /** The quests the NPCs met offer (NPCObject.GetAvailableQuests per NPC: QuestInfoList where NPCIndex == ObjectID). */
  private allInfos(): ClientQuestInfo[] {
    const q = this.m.quests, seen = new Set<number>(), out: ClientQuestInfo[] = [];
    const ids = new Set<number>(metNpcs(this.ui.rt).keys());
    for (const n of this.m.gameInfo.map(this.m.currentMap.index)?.NPCs ?? []) ids.add(n.ObjectID);
    for (const id of ids) {
      for (const e of q.availableQuests(id)) {
        if (seen.has(e.info.Index)) continue;
        seen.add(e.info.Index);
        out.push(e.info);
      }
    }
    return out;
  }

  private matches(e: QuestEntry): boolean {
    const s = this.query;
    if (!s) return true;
    const i = e.info;
    if (i.Name.toLowerCase().includes(s) || i.Group.toLowerCase().includes(s)) return true;
    if (this.npcLabel(i.NPCIndex).toLowerCase().includes(s) || this.npcLabel(i.FinishNPCIndex).toLowerCase().includes(s)) return true;
    for (const l of i.Description) if (l.toLowerCase().includes(s)) return true;
    for (const l of i.TaskDescription) if (l.toLowerCase().includes(s)) return true;
    return false;
  }

  private selected(t: Exclude<Tab, "items">, list: QuestEntry[]): QuestEntry | null {
    return list.find((e) => e.info.Index === this.picked[t]) ?? list[0] ?? null;
  }

  // — Render (once a frame at most, only while open; regions separately) —

  protected override render(): void {
    const available = this.entries("available");
    this.renderSeg(available.length);
    const tab = this.tab;
    this.detail.hidden = tab === "items";
    this.itemsPane.hidden = tab !== "items";
    if (tab === "items") {
      if (this.listDirty) { this.listDirty = false; this.renderItemsSide(); }
      if (this.itemsDirty) { this.itemsDirty = false; this.renderItems(); }
      return;
    }
    const list = (tab === "available" ? available : this.entries(tab)).filter((e) => this.matches(e));
    if (this.listDirty) { this.listDirty = false; this.renderList(tab, list); }
    if (this.detailDirty) { this.detailDirty = false; this.renderDetail(tab, this.selected(tab, list)); }
  }

  /** The tab strip and its counts (`available` null = keep the shown count: it costs a walk of the NPCs met). */
  private renderSeg(available: number | null): void {
    const q = this.m.quests;
    const counts: Record<Tab, number | null> = {
      active: q.current.length, available, completed: q.completed.length,
      items: q.questInventory.reduce((n, it) => n + (it ? 1 : 0), 0),
    };
    for (const [t, { b, n }] of this.segBtns) {
      b.classList.toggle("on", t === this.tab);
      b.setAttribute("aria-selected", String(t === this.tab));
      const c = counts[t];
      if (c !== null) setText(n, String(c));
    }
    this.search.el.hidden = this.tab === "items";
  }

  private renderList(t: Exclude<Tab, "items">, list: QuestEntry[]): void {
    const q = this.m.quests;
    const sel = this.selected(t, list);
    const out: HTMLElement[] = [];
    if (!list.length) {
      out.push(h("div", "empty", this.query ? "No quests match."
        : t === "active" ? "No quests taken. NPCs with a ! over their head have quests for you."
        : t === "available" ? "Nothing you can take from the NPCs you have met. Explore and look for a ! over an NPC's head."
        : "No quests completed yet."));
    }
    for (const type of TYPES) {
      const group = list.filter((e) => e.info.Type === type);
      if (!group.length) continue;
      const key = `${t}:${type}`, isShut = this.shut.has(key);
      const head = asButton(h("div", "grp" + (isShut ? " shut" : ""), h("span", "chev", "▾"), svg(TYPE_ICON[type] ?? "scroll", "qu-ic"),
        QuestTypeName[type] ?? "", h("span", "", String(group.length))), `${isShut ? "Show" : "Fold"} ${QuestTypeName[type] ?? ""} quests`);
      head.addEventListener("click", () => {
        if (this.shut.has(key)) this.shut.delete(key); else this.shut.add(key);
        this.listDirty = true; this.invalidate();
      });
      out.push(head);
      if (!isShut) for (const e of group) out.push(this.row(t, e, e === sel));
    }
    this.list.replaceChildren(...out);

    if (t === "active") {
      const ready = list.filter((e) => e.progress.Completed).length;
      this.foot.replaceChildren(h("span", "", h("b", "", String(ready)), " ready to turn in"),
        h("span", "", "Tracked ", h("b", "", `${q.trackedIds.length} / ${MAX_TRACKED_QUESTS}`)));
    } else if (t === "available") {
      this.foot.replaceChildren(h("span", "", "Take these from their NPC"));
    } else this.foot.replaceChildren(h("span", "", h("b", "", String(q.completed.length)), " completed"));
  }

  private row(t: Exclude<Tab, "items">, e: QuestEntry, isSel: boolean): HTMLElement {
    const { info, progress } = e;
    const q = this.m.quests;
    const ready = t === "active" && progress.Completed;
    const marker = questMiniMapMarker(t === "completed" ? null : t === "available" ? questIconFor(info) : entryIcon(e));
    const mk = h("span", "mk", marker?.text ?? "");
    if (marker) mk.style.color = marker.colour;
    const lv = `Lv ${info.MinLevelNeeded}`;
    const where = t === "active" ? (ready ? `Return to ${this.npcLabel(info.FinishNPCIndex)}` : progress.TaskList[0] ?? info.Group)
      : t === "available" ? this.npcLabel(info.NPCIndex) : info.Group;
    const parts: HTMLElement[] = [mk, h("div", "mid", h("div", "nm", info.Name), h("div", "sub", [lv, where].filter(Boolean).join(" · ")))];
    if (ready) parts.push(h("span", "pill hot", "Ready"));
    else if (t === "active" && progress.New) parts.push(h("span", "pill", "New"));
    if (t === "active") {
      const on = q.isTracked(info.Index), full = !on && q.trackedIds.length >= MAX_TRACKED_QUESTS;
      const pin = asButton(h("div", "pin" + (on ? " on" : "") + (full ? " full" : ""), svg("pin", "qu-ic")),
        on ? "Untrack" : full ? `You can track at most ${MAX_TRACKED_QUESTS} quests` : "Track on screen");
      pin.addEventListener("click", (ev) => { ev.stopPropagation(); this.toggleTrack(info.Index); });
      parts.push(pin);
    }
    const low = t !== "completed" && q.level - info.MinLevelNeeded > 10; // QuestSingleQuestItem: grey 10+ levels below
    const r = h("div", "row" + (isSel ? " sel" : "") + (ready ? " ready" : "") + (t === "completed" ? " done" : "") + (low ? " low" : "")
      + (this.pending.has(info.Index) ? " busy" : ""), ...parts);
    r.addEventListener("click", () => {
      if (this.picked[t] === info.Index) return;
      this.picked[t] = info.Index;
      for (const x of Array.from(this.list.querySelectorAll(".row.sel"))) x.classList.remove("sel");
      r.classList.add("sel");
      this.detailDirty = true; this.invalidate();
    });
    return r;
  }

  private renderDetail(t: Exclude<Tab, "items">, e: QuestEntry | null): void {
    this.ui.tooltip.hide();
    this.waiting.clear();
    if (!e) { this.detail.replaceChildren(h("div", "empty", "Pick a quest.")); return; }
    const { info, progress } = e;
    const q = this.m.quests;
    const ready = t === "active" && progress.Completed;

    const kind = QuestTypeName[info.Type] ?? "";
    const lv = info.MaxLevelNeeded > 0 && info.MaxLevelNeeded < 65535 && info.MaxLevelNeeded !== info.MinLevelNeeded
      ? `Level ${info.MinLevelNeeded}-${info.MaxLevelNeeded}` : `Level ${info.MinLevelNeeded}`;
    const tags = h("div", "tags", h("span", "pill", kind), h("span", "pill", lv));
    if (info.Group) tags.append(h("span", "pill", info.Group));
    if (ready) tags.append(h("span", "pill hot", "Ready to turn in"));
    else if (t === "active") tags.append(h("span", "pill", "In progress"));
    if (t === "completed") tags.append(h("span", "pill", "Completed"));
    if (t === "active" && q.isTracked(info.Index)) tags.append(h("span", "pill", "Tracked"));

    const giver = h("div", "giver", this.npcLine("From", info.NPCIndex));
    if (!sameFinishNPC(info)) giver.append(this.npcLine("Turn in to", info.FinishNPCIndex));
    const parts: HTMLElement[] = [h("div", "dhead", h("h3", "", svg(TYPE_ICON[info.Type] ?? "scroll", "qu-ic"), info.Name), tags, giver)];

    // QuestMessage.UpdateQuest's sections, as text.
    if (info.Description.length) parts.push(this.text(info.Description, "desc"));
    if (info.TaskDescription.length) parts.push(this.section("Tasks", info.TaskDescription, true));
    if (t === "active" && progress.TaskList.length) parts.push(this.section("Progress", progress.TaskList, true));
    if (info.ReturnDescription.length && t !== "completed") parts.push(this.section("Return", info.ReturnDescription, false));
    if (info.TimeLimitInSeconds > 0) parts.push(h("div", "", h("div", "lbl", "Time limit"), h("div", "txt", printTimeSpanFromSeconds(info.TimeLimitInSeconds))));
    parts.push(this.rewards(info));

    this.detail.replaceChildren(h("div", "dscroll", ...parts), this.actions(t, e));
  }

  private npcLine(label: string, id: number): HTMLElement {
    const n = this.npc(id);
    const where = n ? this.mapTitle(n.map) : "";
    return h("span", "", `${label} `, h("b", "", n ? npcName(n.name)[0] : "an NPC you have not met"),
      n && where ? ` · ${where} (${n.x}, ${n.y})` : "");
  }

  private section(title: string, lines: readonly string[], boxed: boolean): HTMLElement {
    if (!boxed) return h("div", "", h("div", "lbl", title), this.text(lines));
    const box = h("div", "");
    for (const l of lines) box.append(h("div", "obj txt", ...this.markup(l)));
    return h("div", "", h("div", "lbl", title), box);
  }

  private text(lines: readonly string[], cls = ""): HTMLElement {
    const t = h("div", "txt" + (cls ? " " + cls : ""));
    for (const l of lines) t.append(h("div", "", ...this.markup(l)));
    return t;
  }

  /** A quest text row: {text/Colour} colours and info links (named, item tooltip on hover); page links / web links as
   *  plain text (the classic QuestMessage only colours and links). */
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

  /** QuestRewards: exp / gold / credit, the fixed items, the select items (the user's gender only; picked at the NPC). */
  private rewards(info: ClientQuestInfo): HTMLElement {
    const box = h("div", "", h("div", "lbl", "Rewards"));
    const chips = h("div", "rew");
    const chip = (k: string, v: number) => h("div", "chip", h("span", "", k), h("b", "", fmt(v)));
    if (info.RewardExp > 0) chips.append(chip("EXP", info.RewardExp));
    if (info.RewardGold > 0) chips.append(chip("GOLD", info.RewardGold));
    if (info.RewardCredit > 0) chips.append(chip("CREDIT", info.RewardCredit));
    for (const r of info.RewardsFixedItem.slice(0, 5)) chips.append(this.rewardCell(r));
    if (!chips.childNodes.length) chips.append(h("span", "note", "No reward listed."));
    box.append(chips);
    const gender = this.m.quests.gender;
    const select = info.RewardsSelectItem.filter((r) => {
      const g = r.Item.RequiredGender;
      return !(gender === MirGender.Male && !(g & RequiredGender.Male)) && !(gender === MirGender.Female && !(g & RequiredGender.Female));
    }).slice(0, 5);
    if (select.length) {
      const pick = h("div", "rew");
      for (const r of select) pick.append(this.rewardCell(r));
      box.append(h("div", "lbl", "Choose one (at turn-in)"), pick);
    }
    return box;
  }

  private rewardCell(r: QuestItemReward): HTMLElement {
    const c = this.showCell(36);
    this.setCell(c, displayItem(r.Item, r.Count), r.Item, r.Count > 1);
    return c.el;
  }

  private actions(t: Exclude<Tab, "items">, e: QuestEntry): HTMLElement {
    const { info, progress } = e;
    const q = this.m.quests;
    const bar = h("div", "actions");
    const target = t === "available" ? info.NPCIndex : info.FinishNPCIndex;
    const find = button("Map", () => this.findOnMap(target), { icon: "map", title: `Find ${this.npcLabel(target)} on the map` });
    if (!this.npc(target)) find.setDisabled(true, "You have not met this NPC yet");
    if (t === "available") {
      bar.append(find.el, h("span", "speak", `Speak to ${this.npcLabel(info.NPCIndex)} to accept`));
      return bar;
    }
    if (t === "completed") {
      bar.append(h("span", "note", "Completed"));
      return bar;
    }
    const pending = this.pending.has(info.Index);
    const tracked = q.isTracked(info.Index);
    const track = button(tracked ? "Untrack" : "Track", () => this.toggleTrack(info.Index), { icon: "pin", title: tracked ? "Remove from the on-screen tracker" : "Show on the on-screen tracker" });
    if (!tracked && q.trackedIds.length >= MAX_TRACKED_QUESTS) track.setDisabled(true, `You can track at most ${MAX_TRACKED_QUESTS} quests`);
    const share = button(this.shared.has(info.Index) ? "Shared" : "Share", () => this.share(info.Index), { icon: "group", title: "Offer this quest to your group members nearby" });
    if (this.m.group.list.length < 2) share.setDisabled(true, "Share with your group: you are not in a group");
    else if (this.shared.has(info.Index)) share.setDisabled(true, "Shared: wait a moment to share again");
    const abandon: Btn = button(pending ? "Abandoning…" : "Abandon", () => void this.abandon(info), { cls: "warn", icon: "cross", title: "Abandon this quest" });
    if (pending) abandon.setDisabled(true, "Waiting for the server");
    bar.append(track.el, share.el, find.el, abandon.el,
      h("span", "speak", progress.Completed ? `Speak to ${this.npcLabel(info.FinishNPCIndex)} to turn in` : ""));
    return bar;
  }

  // — Actions —

  /** QuestDiaryDialog right-click: QuestTrackingDialog.AddQuest / RemoveQuest (the model saves it per character). */
  private toggleTrack(id: number): void {
    const q = this.m.quests;
    if (q.isTracked(id)) q.untrackQuest(id);
    else if (q.trackedIds.length < MAX_TRACKED_QUESTS) q.trackQuest(id);
  }

  /** QuestDetailDialog share button: C.ShareQuest (the server offers it to group members nearby). */
  private share(id: number): void {
    if (this.shared.has(id)) return;
    this.m.quests.share(id);
    this.shared.add(id);
    later(() => { this.shared.delete(id); this.detailDirty = true; this.invalidate(); }, SHARE_REST_MS, this.signal);
    this.detailDirty = true; this.invalidate();
  }

  /** QuestDetailDialog cancel: AskCancelQuest, then C.AbandonQuest; locked until S.ChangeQuest (Remove). */
  private async abandon(info: ClientQuestInfo): Promise<void> {
    if (this.pending.has(info.Index)) return;
    const ok = await this.ui.rt.popups.ask(`Are you sure you want to cancel the quest "${info.Name}"?`, { title: "Abandon quest", kind: "warning", okCancel: true });
    if (!ok || this.pending.has(info.Index) || !this.m.quests.current.some((p) => p.Id === info.Index)) return;
    this.pending.add(info.Index);
    this.m.quests.abandon(info.Index);
    later(() => { if (this.pending.delete(info.Index)) { this.listDirty = this.detailDirty = true; this.invalidate(); } }, PENDING_MS, this.signal);
    this.listDirty = this.detailDirty = true;
    this.invalidate();
  }

  /** Opens the big map; when the NPC is on another map, a note says where it is. */
  private findOnMap(id: number): void {
    const n = this.npc(id);
    if (!n) return;
    this.ui.openWindow("bigMap", true);
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

  /** A display-only cell (core item cell look) with the item tooltip on hover. */
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
  // At UI start: remember the NPCs that come into view (the Available tab and Find on map use them), and
  // GameScene.ShareQuest as a request toast (Accept = C.AcceptQuest with NPCIndex 0; declines when it runs out).
  watch: (host) => {
    const sig = host.rt.signal, met = metNpcs(host.rt), quests = host.ctx.models.quests;
    return host.packets.on((p) => {
      if (p.type === "ObjectNPC") {
        const map = host.ctx.models.currentMap.index, was = met.get(p.ObjectID);
        met.set(p.ObjectID, { name: p.Name, map, x: p.Location.X, y: p.Location.Y });
        if (!was || was.map !== map || was.name !== p.Name) host.rt.peekWindow<QuestsWindow>(QUESTS_WINDOW)?.npcsChanged();
        return;
      }
      if (p.type !== "ShareQuest") return;
      const info = quests.info(p.QuestIndex);
      if (!info) return;
      void host.rt.popups.request({ from: p.SharerName, tag: "Quest", text: `would like to share the quest "${info.Name}" with you.`, seconds: SHARE_SECONDS })
        .then((yes) => { if (yes && !sig.aborted) quests.accept(0, info.Index); });
    });
  },
});
