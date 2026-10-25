// The user's quest state, quest V2 (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md Part B, "Wire protocol",
// "Character state", "Availability and NPC icons", "Clients"), DOM-free. Desktop counterpart: GameScene.QuestsV2.cs.
//   definitions  S.QuestDefinition, cached per connection (the server sends each once: active / failed quests at login,
//                quests offered by givers in view, a history quest on C.QuestDefinitionRequest);
//   runs         S.QuestStates (Full = replace; Status None = the run ended: turned in, abandoned, failed away);
//                Ready is the server's (derived there, never computed here);
//   progress     S.QuestProgress (objective id -> value, already read live by the server for Collect / Deliver / Flag);
//   history      S.QuestHistory (Full = replace; AvailableAt = the repeat timer);
//   NPCs         S.QuestAvailability per NPC in view (the server-chosen QuestIcon, the quests offered / finishing / with a
//                branch choice there),
//                re-sent by the server when an NPC comes into view and cleared with the map (S.MapChanged) as the server's
//                own sent set is; no client-side CanAccept;
//   offers       S.QuestOffer (a share / item / area / auto prompt: accept with NpcObjectID 0);
//   refusals     S.QuestRefused (every refused Accept / TurnIn / Abandon / Share / Choose): lifts the quest's pending lock
//                and fires `refused` (modern: a banner, classic: a system chat line; questRefusalText);

//   quest bag    User.QuestInventory (UserInformation + S.GainedQuestItem / S.DeleteQuestItem, kept from V1);
//   tracking     the tracked quest ids (QuestTrackingDialog + Settings.TrackedQuests: localStorage per character here);
//                TrackHint on accept tracks the quest (the client decides, as before).
// Objective text is built here from (Type, target, Count, value) with the English ClientTextKeys-style templates below,
// unless the objective carries its own Text (the web client localises with English constants, as the other windows do).

import type {
  QuestDefBranch, QuestDefinitionInfo, QuestDefObjective, QuestDefReward, QuestDefStep, QuestStateEntry, ServerPacket, UserItem,
} from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import {
  QuestCategory, QuestEndpointKind, QuestEndpointRole, QuestIcon, QuestObjectiveType, QuestRefusal, QuestRequestKind, QuestRewardKind,
  QuestRewardScope, QuestRunStatus, QuestShareMode, QuestStepCompletion, QuestTargetKind, SpellName,
} from "../protocol/enums.generated.ts";
import type { ItemInfos } from "./items.ts";
import { Emitter } from "./emitter.ts";
import { dateTimeBinaryToMs } from "./accountDetails.ts";

/** Globals.MaxConcurrentQuests. */
export const MAX_CONCURRENT_QUESTS = 20;
/** QuestTrackingDialog: at most 5 tracked quests (Settings.TrackedQuests = new int[5]). */
export const MAX_TRACKED_QUESTS = 5;
/** UserObject.QuestInventory = new UserItem[40]. */
export const QUEST_INVENTORY_SIZE = 40;
/** A request with no answer unlocks its quest's buttons after this (the server answers S.QuestStates or S.QuestRefused). */
export const QUEST_PENDING_MS = 5000;

/** A quest run (S.QuestStates entry). Times are Unix ms (0 = no limit). */
export interface QuestRun {
  readonly quest: number;
  /** QuestRunStatus: Active, Ready or Failed (None removes the run). */
  readonly status: number;
  /** Current step id, -1 = turn-in pending. */
  readonly step: number;
  readonly expiresAt: number;
  readonly stepExpiresAt: number;
}

/** A completed quest (S.QuestHistory entry). Times are Unix ms; availableAt 0 = no repeat timer. */
export interface QuestHistoryRow {
  readonly quest: number;
  readonly name: string;
  readonly times: number;
  readonly lastAt: number;
  readonly availableAt: number;
}

/** An NPC's quests for this player (S.QuestAvailability entry). */
export interface NpcQuests {
  readonly icon: number;
  readonly offered: readonly number[];
  readonly finishing: readonly number[];
  /** Running quests whose current step's branches can be chosen here (C.QuestChoose; listed met or not). */
  readonly choosing: readonly number[];
}

/** A quest offered by a giver met this game (the Available tab). */
export interface QuestOfferSeen { readonly quest: number; readonly npc: number; readonly map: number }

/** A pending S.QuestOffer (accept with NpcObjectID 0). Kind = QuestEndpointKind; sharedBy "" = not a share. */
export interface QuestOfferPrompt { readonly quest: number; readonly kind: number; readonly sharedBy: string }

/** One objective value change (S.QuestProgress); `prev` undefined = the first value seen for it (no toast). */
export interface QuestProgressChange { readonly quest: number; readonly objective: QuestDefObjective | null; readonly value: number; readonly prev: number | undefined }

/** One run change (S.QuestStates, not Full): began (prev null), ended (run null: turned in, abandoned, failed away) or
 *  changed status / step. A turn-in is told apart by the history row (`completed`). */
export interface QuestRunChange { readonly quest: number; readonly prev: QuestRun | null; readonly run: QuestRun | null }

/** S.QuestRefused: a request (QuestRequestKind) refused for a reason (QuestRefusal). */
export interface QuestRefusedEvent { readonly quest: number; readonly request: number; readonly reason: number }

export type QuestChange = "quests" | "progress" | "defs" | "history" | "npcs" | "offers" | "items" | "tracking" | "user" | "names" | "pending";

/** Names of things an objective / reward points at; game.ts wires them to GameInfo (missing ones are requested there). */
export interface QuestNamer {
  item(index: number): string | null;
  monster(index: number): string | null;
  npc(index: number): string | null;
  map(index: number): string | null;
}

// — Text (English, ClientTextKeys-style: the desktop's keys are QuestObj*) —
const OBJ_TEXT: Readonly<Record<number, string>> = {
  [QuestObjectiveType.Kill]: "Kill {0}: {1}/{2}",
  [QuestObjectiveType.Collect]: "Collect {0}: {1}/{2}",
  [QuestObjectiveType.Deliver]: "Bring {0}: {1}/{2}",
  [QuestObjectiveType.Talk]: "Speak to {0}",
  [QuestObjectiveType.ReachArea]: "Go to {0}",
  [QuestObjectiveType.UseItem]: "Use {0}: {1}/{2}",
  [QuestObjectiveType.Craft]: "Craft {0}: {1}/{2}",
  [QuestObjectiveType.Gather]: "Gather {0}: {1}/{2}",
  [QuestObjectiveType.ReachLevel]: "Reach level {2}",
  [QuestObjectiveType.UseSkill]: "Use {0}: {1}/{2}",
  [QuestObjectiveType.Counter]: "{0}: {1}/{2}",
  [QuestObjectiveType.Flag]: "{0}",
};
/** Objective types that are done-or-not (no count or bar shown). */
const BINARY = new Set<number>([QuestObjectiveType.Talk, QuestObjectiveType.ReachArea, QuestObjectiveType.Flag, QuestObjectiveType.ReachLevel]);

const format = (t: string, ...a: (string | number)[]) => t.replace(/\{(\d)\}/g, (_, i: string) => String(a[+i] ?? ""));
const spaced = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2");

// S.QuestRefused text (English constants, as the other quest texts; {0} = the quest's name)
const REFUSED_TITLE: Readonly<Record<number, string>> = {
  [QuestRequestKind.Accept]: "Cannot accept {0}", [QuestRequestKind.TurnIn]: "Cannot hand in {0}", [QuestRequestKind.Abandon]: "Cannot abandon {0}",
  [QuestRequestKind.Share]: "Cannot share {0}", [QuestRequestKind.Choose]: "Cannot choose",
};
const REFUSED_REASON: Readonly<Record<number, string>> = {
  [QuestRefusal.NotAvailable]: "This quest is not available.",
  [QuestRefusal.NotAtNpc]: "You are too far from the quest giver.",
  [QuestRefusal.QuestLogFull]: "Your quest log is full.",
  [QuestRefusal.BagFull]: "Your bag is full.",
  [QuestRefusal.NotReady]: "The quest is not ready to hand in.",
  [QuestRefusal.InvalidPick]: "That choice is not available.",
  [QuestRefusal.AbandonNotAllowed]: "This quest cannot be abandoned.",
  [QuestRefusal.NotInGroup]: "You are not in a group.",
  [QuestRefusal.ShareNotAllowed]: "This quest cannot be shared.",
  [QuestRefusal.UnknownQuest]: "This quest is not available.",
  [QuestRefusal.ConditionNotMet]: "You do not meet the quest's requirements.",
  [QuestRefusal.AlreadyActive]: "You already have this quest.",
  [QuestRefusal.Other]: "The request was refused.",
  [QuestRefusal.Dead]: "You cannot do that while dead.",
  [QuestRefusal.AlreadyCompleted]: "You have already completed this quest.",
  [QuestRefusal.QuestBagFull]: "Your quest bag is full.",
  [QuestRefusal.NoMemberCouldTake]: "No group member nearby can take this quest.",
};

/** A refusal's title (per request kind, with the quest's name) and reason line. */
export function questRefusalText(e: QuestRefusedEvent, questName: string): { title: string; reason: string } {
  return {
    title: format(REFUSED_TITLE[e.request] ?? "Cannot do that: {0}", questName),
    reason: REFUSED_REASON[e.reason] ?? REFUSED_REASON[QuestRefusal.Other]!,
  };
}

/** Names listed for a group target (TargetMembers) before "…". */
const GROUP_NAMES_MAX = 3;

export const QUEST_CATEGORY_LABEL: Readonly<Record<number, string>> = {
  [QuestCategory.Story]: "Story", [QuestCategory.Side]: "Side", [QuestCategory.Daily]: "Daily", [QuestCategory.Weekly]: "Weekly",
  [QuestCategory.Repeatable]: "Repeatable", [QuestCategory.Event]: "Event", [QuestCategory.Guild]: "Guild", [QuestCategory.Hidden]: "Hidden",
};
/** Display order of the category groups. */
export const QUEST_CATEGORY_ORDER: readonly number[] = [
  QuestCategory.Story, QuestCategory.Side, QuestCategory.Daily, QuestCategory.Weekly, QuestCategory.Repeatable, QuestCategory.Event,
  QuestCategory.Guild, QuestCategory.Hidden,
];
export const QUEST_REWARD_LABEL: Readonly<Record<number, string>> = {
  [QuestRewardKind.Exp]: "EXP", [QuestRewardKind.Gold]: "Gold", [QuestRewardKind.Credit]: "Credit", [QuestRewardKind.Item]: "Item",
  [QuestRewardKind.Reputation]: "Reputation", [QuestRewardKind.Title]: "Title", [QuestRewardKind.Spell]: "Skill",
  [QuestRewardKind.Unlock]: "Unlock", [QuestRewardKind.Buff]: "Buff", [QuestRewardKind.Currency]: "Currency", [QuestRewardKind.ScriptPage]: "Bonus",
};

/** The icon for one quest, as the server picks it for an NPC (PlayerQuests OfferIcon / ReadyIcon; in progress = white "?"). */
export function questIconFor(category: number, state: "offer" | "ready" | "active"): number {
  if (state === "active") return QuestIcon.QuestionWhite;
  const daily = category === QuestCategory.Daily || category === QuestCategory.Weekly;
  if (state === "offer") return category === QuestCategory.Story ? QuestIcon.ExclamationGreen : daily ? QuestIcon.ExclamationBlue : QuestIcon.ExclamationYellow;
  return category === QuestCategory.Story ? QuestIcon.QuestionGreen : daily ? QuestIcon.QuestionBlue : QuestIcon.QuestionYellow;
}

/** Functions.PrintTimeSpanFromSeconds (accurate = true). */
export function printTimeSpanFromSeconds(secs: number): string {
  const total = Math.max(0, Math.floor(secs));
  const d = Math.floor(total / 86400), h = Math.floor(total / 3600) % 24, m = Math.floor(total / 60) % 60, s = total % 60;
  const p2 = (n: number) => String(n).padStart(2, "0");
  if (total < 60) return `${s}s`;
  if (total < 3600) return `${m}m ${p2(s)}s`;
  if (total < 86400) return `${h}h ${p2(m)}m ${p2(s)}s`;
  return `${d}d ${p2(h)}h ${p2(m)}m ${p2(s)}s`;
}

/** DateTime.ToBinary (bigint) to Unix ms; 0n = 0. */
function ms(binary: bigint): number {
  return binary === 0n ? 0 : dateTimeBinaryToMs(binary);
}

const TRACKING_KEY = (name: string) => `crystal.questTracking.${name}`;

export class Quests {
  /** S.QuestDefinition by quest id. */
  private readonly defs = new Map<number, QuestDefinitionInfo>();
  /** Objective id -> its definition (ids are global and never reused). */
  private readonly objectiveDefs = new Map<number, QuestDefObjective>();
  /** Definitions asked with C.QuestDefinitionRequest this connection. */
  private readonly asked = new Set<number>();
  /** Runs (insertion order = accept order). */
  private readonly _runs = new Map<number, QuestRun>();
  /** Objective id -> value (S.QuestProgress). */
  private readonly values = new Map<number, number>();
  private readonly _history = new Map<number, QuestHistoryRow>();
  /** NPC ObjectID -> S.QuestAvailability entry (cleared with the map). */
  private readonly _npcs = new Map<number, NpcQuests>();
  /** Offers seen from givers this game (quest -> where), dropped when taken or no longer offered by that NPC. */
  private readonly seenOffers = new Map<number, QuestOfferSeen>();
  private readonly _offers: QuestOfferPrompt[] = [];
  private mapIndex = 0;
  private readonly waitingNames = new Set<string>();

  /** User.QuestInventory. */
  questInventory: (UserItem | null)[] = new Array<UserItem | null>(QUEST_INVENTORY_SIZE).fill(null);
  /** QuestTrackingDialog.TrackedQuestsIds (display order). */
  trackedIds: number[] = [];
  /** Settings.TrackedQuests (the saved slots, -1 = empty), loaded per character on UserInformation. */
  private savedTracked: number[] = new Array<number>(MAX_TRACKED_QUESTS).fill(-1);

  userName = "";
  level = 0;
  userClass = 0;
  gender = 0;

  /** Names for objective / reward targets (set by game.ts). */
  namer: QuestNamer | null = null;

  /** An objective value changed (toasts). */
  readonly progressed = new Emitter<QuestProgressChange>();
  /** A run began, ended or changed status / step (banners). Not fired for a Full list. */
  readonly runChanged = new Emitter<QuestRunChange>();
  /** A quest was turned in (its history row arrived with more completions; not for a Full list). */
  readonly completed = new Emitter<QuestHistoryRow>();
  /** A new S.QuestOffer prompt. */
  readonly offered = new Emitter<QuestOfferPrompt>();
  /** S.QuestRefused (the quest's pending lock is already lifted when this fires). */
  readonly refused = new Emitter<QuestRefusedEvent>();

  /** Quests with a request on its way (quest -> Date.now() when sent), until S.QuestStates / S.QuestRefused or QUEST_PENDING_MS. */
  private readonly pendingAt = new Map<number, number>();

  private readonly listeners = new Set<(c: QuestChange) => void>();

  private readonly send: (f: Uint8Array) => void;
  private readonly items: ItemInfos;

  // Plain fields (no parameter properties), so tools/quests-test.ts runs under node --experimental-strip-types.
  constructor(send: (f: Uint8Array) => void, items: ItemInfos) {
    this.send = send;
    this.items = items;
  }

  onChange(fn: (c: QuestChange) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private emit(c: QuestChange): void {
    for (const fn of this.listeners) {
      try { fn(c); } catch (e) { console.warn("[quests] listener:", e); }
    }
  }

  // — Reads —

  def(quest: number): QuestDefinitionInfo | undefined { return this.defs.get(quest); }
  run(quest: number): QuestRun | undefined { return this._runs.get(quest); }
  history(quest: number): QuestHistoryRow | undefined { return this._history.get(quest); }
  /** The runs in accept order (Active, Ready and Failed). */
  runs(): QuestRun[] { return [...this._runs.values()]; }
  /** Runs counted against MAX_CONCURRENT_QUESTS (Active and Ready, as the server's ActiveCount). */
  get activeCount(): number {
    let n = 0;
    for (const r of this._runs.values()) if (r.status !== QuestRunStatus.Failed) n++;
    return n;
  }
  /** The history, most recent first. */
  historyRows(): QuestHistoryRow[] { return [...this._history.values()].sort((a, b) => b.lastAt - a.lastAt); }
  /** The quest's name: its definition, else its history row, else "Quest n". */
  name(quest: number): string {
    return this.defs.get(quest)?.Name || this._history.get(quest)?.name || `Quest ${quest}`;
  }
  get offers(): readonly QuestOfferPrompt[] { return this._offers; }

  /** The quests offered by the givers met this game that are not taken (the Available tab), oldest first. */
  availableOffers(): QuestOfferSeen[] {
    const out: QuestOfferSeen[] = [];
    for (const o of this.seenOffers.values()) if (!this._runs.has(o.quest)) out.push(o);
    return out;
  }

  /** The NPC's S.QuestAvailability entry, or undefined (no quests, or not seen on this map). */
  npcQuests(npcObjectId: number): NpcQuests | undefined { return this._npcs.get(npcObjectId); }
  /** True when the NPC offers, finishes or has a branch choice of a quest for the user. */
  npcHasQuests(npcObjectId: number): boolean {
    const e = this._npcs.get(npcObjectId);
    return !!e && (e.offered.length > 0 || e.finishing.length > 0 || e.choosing.length > 0);
  }
  /** The running quests whose branch can be chosen at this NPC (S.QuestAvailability Choosing). */
  npcChoosing(npcObjectId: number): readonly number[] { return this._npcs.get(npcObjectId)?.choosing ?? []; }
  /** The NPC's quests to list: finishing here, then branch choices, then offered (no duplicates). */
  npcQuestList(npcObjectId: number): number[] {
    const e = this._npcs.get(npcObjectId);
    if (!e) return [];
    const out = [...e.finishing];
    for (const q of e.choosing) if (!out.includes(q)) out.push(q);
    for (const q of e.offered) if (!out.includes(q)) out.push(q);
    return out;
  }
  /** True when a request for the quest is on its way (buttons locked). */
  isPending(quest: number, now = Date.now()): boolean {
    const t = this.pendingAt.get(quest);
    return t !== undefined && now - t < QUEST_PENDING_MS;
  }
  /** True when the quest may be offered to the group (ShareMode not None and a group of two or more). */
  canShare(quest: number, groupSize: number): boolean {
    const def = this.defs.get(quest);
    return !!def && def.ShareMode !== QuestShareMode.None && groupSize >= 2;
  }
  /** The server-chosen QuestIcon above the NPC, or null (QuestIcon.None / unknown). */
  npcQuestIcon(npcObjectId: number): number | null {
    const icon = this._npcs.get(npcObjectId)?.icon ?? QuestIcon.None;
    return icon === QuestIcon.None ? null : icon;
  }
  /** The NPC seen on this map that finishes this quest (its ObjectID), or 0. */
  finishingNpc(quest: number): number {
    for (const [id, e] of this._npcs) if (e.finishing.includes(quest)) return id;
    return 0;
  }

  /** The step with this id. */
  step(def: QuestDefinitionInfo, stepId: number): QuestDefStep | undefined {
    for (const s of def.Steps) if (s.Index === stepId) return s;
    return undefined;
  }
  /** The run's current step (undefined while turn-in is pending or the run is unknown). */
  currentStep(quest: number): QuestDefStep | undefined {
    const def = this.defs.get(quest), run = this._runs.get(quest);
    return def && run && run.step >= 0 ? this.step(def, run.step) : undefined;
  }
  /** A step's objectives in their order (the server sends them so). */
  objectives(def: QuestDefinitionInfo, stepId: number): QuestDefObjective[] {
    return def.Objectives.filter((o) => o.Step === stepId);
  }
  /** The objectives to show for a step: Hidden ones only once they have progress. */
  shownObjectives(def: QuestDefinitionInfo, stepId: number): QuestDefObjective[] {
    return def.Objectives.filter((o) => o.Step === stepId && (!o.Hidden || (this.values.get(o.Index) ?? 0) > 0));
  }
  /** The objective's current value (0 without a progress row). */
  value(o: QuestDefObjective): number { return Math.min(this.values.get(o.Index) ?? 0, Math.max(1, o.Count)); }
  done(o: QuestDefObjective): boolean { return this.value(o) >= Math.max(1, o.Count); }
  /** 0..1 for an objective bar. */
  fraction(o: QuestDefObjective): number { return this.value(o) / Math.max(1, o.Count); }
  /** True when the objective is done-or-not (no count or bar). */
  binary(o: QuestDefObjective): boolean { return BINARY.has(o.Type); }

  /** The step's required objectives are met (StepMet on the server; optional objectives never block). */
  stepMet(def: QuestDefinitionInfo, step: QuestDefStep): boolean {
    let required = 0, met = 0;
    for (const o of def.Objectives) {
      if (o.Step !== step.Index || o.Optional) continue;
      required++;
      if (this.done(o)) met++;
    }
    if (required === 0) return true;
    if (step.CompletionMode === QuestStepCompletion.Any) return met > 0;
    if (step.CompletionMode === QuestStepCompletion.NOf) return met >= Math.min(Math.max(step.NRequired, 1), required);
    return met === required;
  }

  /** The branch choices waiting on the run's current step (its objectives met), else []. */
  pendingBranches(quest: number): QuestDefBranch[] {
    const list = this.stepBranches(quest);
    return list.length && this.stepMet(this.defs.get(quest)!, this.currentStep(quest)!) ? list : [];
  }
  /** The branches of the run's current step, met or not (an Active run only), else []. */
  stepBranches(quest: number): QuestDefBranch[] {
    const def = this.defs.get(quest), run = this._runs.get(quest);
    if (!def || !run || run.status !== QuestRunStatus.Active || run.step < 0) return [];
    const step = this.step(def, run.step);
    return step ? def.Branches.filter((b) => b.Step === step.Index) : [];
  }

  /** The objective's text: its own Text, else built from type / target / count / value. */
  objectiveText(o: QuestDefObjective): string {
    if (o.Text) return o.Text;
    const count = Math.max(1, o.Count), value = this.value(o);
    return format(OBJ_TEXT[o.Type] ?? "{0}: {1}/{2}", this.targetName(o), value, count);
  }

  /** The objective's target, named: the server's TargetName (groups, flags, maps), else the item / monster / NPC info. A
   *  group with more than one member is named by its members when their names are known (at most 3, then "…"). */
  targetName(o: QuestDefObjective): string {
    const group = o.TargetKind === QuestTargetKind.MonsterGroup || o.TargetKind === QuestTargetKind.ItemGroup;
    if (group && o.TargetMembers.length > 1) {
      const names = this.memberNames(o);
      if (names) return names;
    }
    if (o.TargetName) {
      if (group) return `${o.TargetName} (any)`;
      if (o.TargetKind === QuestTargetKind.Flag) return o.Type === QuestObjectiveType.Flag ? "Complete the task" : `Task ${o.TargetName}`;
      return o.TargetName;
    }
    const n = this.namer;
    switch (o.TargetKind) {
      case QuestTargetKind.Monster: return this.named("MONSTER", o.TargetId, n?.monster(o.TargetId)) ?? `Monster ${o.TargetId}`;
      case QuestTargetKind.Item: return this.named("ITEM", o.TargetId, n?.item(o.TargetId)) ?? `Item ${o.TargetId}`;
      case QuestTargetKind.Npc: return this.named("NPC", o.TargetId, n?.npc(o.TargetId)) ?? `NPC ${o.TargetId}`;
      case QuestTargetKind.Spell: return spaced(SpellName[o.TargetId] ?? `Skill ${o.TargetId}`);
      case QuestTargetKind.Map: return this.named("MAP", o.TargetId, n?.map(o.TargetId)) ?? `Map ${o.TargetId}`;
    }
    if (o.Type === QuestObjectiveType.ReachArea && o.Map > 0) {
      const map = this.named("MAP", o.Map, n?.map(o.Map)) ?? `Map ${o.Map}`;
      return o.X || o.Y ? `${map} (${o.X}, ${o.Y})` : map;
    }
    if (o.Type === QuestObjectiveType.Flag) return "Complete the task";
    if (o.Type === QuestObjectiveType.UseSkill) return "a skill";
    return "Progress";
  }

  /** A group target's member names ("Wolf, WolfKing", at most GROUP_NAMES_MAX then "…"), or null while one is unknown. */
  private memberNames(o: QuestDefObjective): string | null {
    const monster = o.TargetKind === QuestTargetKind.MonsterGroup, n = this.namer;
    const names: string[] = [];
    let missing = false;
    for (const id of o.TargetMembers.slice(0, GROUP_NAMES_MAX)) {
      const name = monster ? this.named("MONSTER", id, n?.monster(id)) : this.named("ITEM", id, n?.item(id));
      if (name) names.push(name); else missing = true;
    }
    if (missing) return null;
    return names.join(", ") + (o.TargetMembers.length > GROUP_NAMES_MAX ? ", …" : "");
  }

  /** A name from the namer, or null (remembered: its arrival fires "names" through `nameArrived`). */
  private named(kind: string, index: number, name: string | null | undefined): string | null {
    if (name) return name;
    this.waitingNames.add(`${kind}:${index}`);
    return null;
  }

  /** game.ts: an item / monster / NPC / map info arrived; redraws when a quest text waited for it. */
  nameArrived(kind: "ITEM" | "MONSTER" | "NPC" | "MAP", index: number): void {
    if (this.waitingNames.delete(`${kind}:${index}`)) this.emit("names");
  }

  /** The names of the quest's NPC endpoints with this role (Finish falls back to Start: a quest with no Finish endpoint
   *  is turned in at its Start NPC). */
  endpointNames(def: QuestDefinitionInfo, role: number): string[] {
    const pick = (r: number) => def.Endpoints.filter((e) => e.Role === r && e.Kind === QuestEndpointKind.Npc && e.Name).map((e) => e.Name);
    const list = pick(role);
    return list.length || role !== QuestEndpointRole.Finish ? list : pick(QuestEndpointRole.Start);
  }
  /** The NPC endpoints (NPCInfo indexes) with this role, Finish falling back to Start. */
  endpointNpcs(def: QuestDefinitionInfo, role: number): number[] {
    const pick = (r: number) => def.Endpoints.filter((e) => e.Role === r && e.Kind === QuestEndpointKind.Npc).map((e) => e.TargetId);
    const list = pick(role);
    return list.length || role !== QuestEndpointRole.Finish ? list : pick(QuestEndpointRole.Start);
  }

  /** The rewards of a scope (Quest = turn-in) with their positions in def.Rewards (what C.QuestTurnIn picks); first-time
   *  rewards hidden after the first completion. */
  rewards(def: QuestDefinitionInfo, scope: number = QuestRewardScope.Quest, scopeId = 0): { reward: QuestDefReward; position: number }[] {
    const again = (this._history.get(def.Quest)?.times ?? 0) > 0;
    const out: { reward: QuestDefReward; position: number }[] = [];
    def.Rewards.forEach((reward, position) => {
      if (reward.Scope !== scope || (scope !== QuestRewardScope.Quest && reward.ScopeId !== scopeId)) return;
      if (reward.FirstTimeOnly && again) return;
      out.push({ reward, position });
    });
    return out;
  }
  /** The choice groups (> 0) among the turn-in rewards, in order. */
  choiceGroups(def: QuestDefinitionInfo): number[] {
    const out: number[] = [];
    for (const { reward } of this.rewards(def)) if (reward.ChoiceGroup > 0 && !out.includes(reward.ChoiceGroup)) out.push(reward.ChoiceGroup);
    return out;
  }
  /** A reward as text (for kinds without an item cell, and the classic adapter). */
  rewardText(r: QuestDefReward): string {
    const amount = Number(r.Amount);
    switch (r.Kind) {
      case QuestRewardKind.Exp: case QuestRewardKind.Gold: case QuestRewardKind.Credit:
        return `${amount.toLocaleString("en-US")} ${QUEST_REWARD_LABEL[r.Kind]}`;
      case QuestRewardKind.Item: {
        const name = this.named("ITEM", r.TargetId, this.namer?.item(r.TargetId)) ?? `Item ${r.TargetId}`;
        return amount > 1 ? `${name} x${amount}` : name;
      }
      case QuestRewardKind.Spell: return `Skill: ${spaced(SpellName[r.TargetId] ?? String(r.TargetId))}`;
    }
    return `${QUEST_REWARD_LABEL[r.Kind] ?? "Reward"}${amount > 1 ? ` x${amount}` : ""}`;
  }

  // — Reward picks (one per choice group; shared by the quest window and the NPC dialogue, sent with C.QuestTurnIn) —

  private readonly picks = new Map<number, Map<number, number>>();
  /** The picked reward position of a choice group, or -1. */
  pickOf(quest: number, group: number): number { return this.picks.get(quest)?.get(group) ?? -1; }
  pick(quest: number, group: number, position: number): void {
    let m = this.picks.get(quest);
    if (!m) { m = new Map(); this.picks.set(quest, m); }
    m.set(group, position);
    this.emit("progress");
  }
  /** The picks to send (one position per choice group), or null while a group has none. */
  picksFor(def: QuestDefinitionInfo): number[] | null {
    const out: number[] = [];
    for (const g of this.choiceGroups(def)) {
      const p = this.pickOf(def.Quest, g);
      if (p < 0 || def.Rewards[p]?.ChoiceGroup !== g) return null;
      out.push(p);
    }
    return out;
  }

  /** Ms left on the run's quest or step limit (the earlier), or null without a limit. */
  timeLeft(run: QuestRun, now = Date.now()): number | null {
    let end = 0;
    for (const t of [run.expiresAt, run.stepExpiresAt]) if (t > 0 && (end === 0 || t < end)) end = t;
    return end ? Math.max(0, end - now) : null;
  }

  // — Packets —

  /** Observes every packet; returns true when it was a quest packet. Never consumes UserInformation / LevelChanged / maps. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "UserInformation":
        // UserObject.Load: QuestInventory, and Settings.LoadTrackedQuests(info.Name)
        this.userName = p.Name; this.level = p.Level; this.userClass = p.Class; this.gender = p.Gender;
        this.questInventory = new Array<UserItem | null>(Math.max(QUEST_INVENTORY_SIZE, p.QuestInventory?.length ?? 0)).fill(null);
        p.QuestInventory?.forEach((it, i) => { this.questInventory[i] = it; });
        this.loadTracked();
        for (const id of this._runs.keys()) if (this.savedTracked.includes(id)) this.trackQuest(id, true);
        this.emit("items");
        this.emit("user");
        return false;
      case "LevelChanged": this.level = p.Level; this.emit("user"); return false;
      case "MapInformation":
      case "MapChanged": // the server drops its availability sent set with the map (PlayerQuests.MapChanged)
        this.mapIndex = p.MapIndex;
        if (this._npcs.size) { this._npcs.clear(); this.emit("npcs"); }
        return false;
      case "QuestDefinition": this.addDefinition(p); return true;
      case "QuestStates": this.states(p.Full, p.States); return true;
      case "QuestRefused":
        this.pendingAt.delete(p.Quest);
        this.emit("pending");
        this.refused.emit({ quest: p.Quest, request: p.Request, reason: p.Reason });
        return true;
      case "QuestProgress":
        for (const e of p.Progress) {
          const prev = this.values.get(e.Objective);
          this.values.set(e.Objective, e.Value);
          if (prev !== e.Value) this.progressed.emit({ quest: e.Quest, objective: this.objectiveDefs.get(e.Objective) ?? null, value: e.Value, prev });
        }
        this.emit("progress");
        return true;
      case "QuestHistory": {
        if (p.Full) this._history.clear();
        for (const e of p.History) {
          const row: QuestHistoryRow = { quest: e.Quest, name: e.Name, times: e.Times, lastAt: ms(e.LastAt), availableAt: ms(e.AvailableAt) };
          const was = this._history.get(e.Quest);
          this._history.set(e.Quest, row);
          if (!p.Full && (!was || was.times < row.times)) this.completed.emit(row);
        }
        this.emit("history");
        return true;
      }
      case "QuestAvailability":
        for (const n of p.Npcs) this.npcEntry(n.ObjectID, { icon: n.Icon, offered: n.Offered, finishing: n.Finishing, choosing: n.Choosing });
        this.emit("npcs");
        return true;
      case "QuestOffer": {
        const o: QuestOfferPrompt = { quest: p.Quest, kind: p.Kind, sharedBy: p.SharedBy };
        for (let i = this._offers.length - 1; i >= 0; i--) if (this._offers[i]!.quest === o.quest) this._offers.splice(i, 1);
        this._offers.push(o);
        this.offered.emit(o);
        this.emit("offers");
        return true;
      }
      case "GainedQuestItem": this.addQuestItem(p.Item); this.emit("items"); return true; // GameScene.GainedQuestItem
      case "DeleteQuestItem": // GameScene.DeleteQuestItem
        for (let i = 0; i < this.questInventory.length; i++) {
          const item = this.questInventory[i];
          if (!item || item.UniqueID !== p.UniqueID) continue;
          if (item.Count === p.Count) this.questInventory[i] = null;
          else item.Count -= p.Count;
          break;
        }
        this.emit("items");
        return true;
    }
    return false;
  }

  private addDefinition(def: QuestDefinitionInfo): void {
    const old = this.defs.get(def.Quest);
    if (old) for (const o of old.Objectives) this.objectiveDefs.delete(o.Index);
    this.defs.set(def.Quest, def);
    for (const o of def.Objectives) this.objectiveDefs.set(o.Index, o);
    this.emit("defs");
  }

  private states(full: boolean, list: readonly QuestStateEntry[]): void {
    this.pendingAt.clear(); // the server's answer: every lock lifts (as the windows did on "quests")
    if (full) {
      const keep = new Set<number>();
      for (const e of list) if (e.Status !== QuestRunStatus.None && e.Status !== QuestRunStatus.Completed) keep.add(e.Quest);
      for (const q of [...this._runs.keys()]) if (!keep.has(q)) this.dropRun(q);
    }
    let trackingChanged = false, saveTracking = false;
    for (const e of list) {
      const prev = this._runs.get(e.Quest) ?? null;
      if (e.Status === QuestRunStatus.None || e.Status === QuestRunStatus.Completed) {
        if (!prev) continue;
        this.dropRun(e.Quest);
        const t = this.trackedIds.indexOf(e.Quest);
        if (t >= 0) { this.trackedIds.splice(t, 1); trackingChanged = saveTracking = true; }
        if (!full) this.runChanged.emit({ quest: e.Quest, prev, run: null });
        continue;
      }
      const run: QuestRun = { quest: e.Quest, status: e.Status, step: e.Step, expiresAt: ms(e.ExpiresAt), stepExpiresAt: ms(e.StepExpiresAt) };
      if (prev && prev.step !== run.step) this.clearStepValues(e.Quest, prev.step);
      this._runs.set(e.Quest, run);
      this.seenOffers.delete(e.Quest);
      for (let i = this._offers.length - 1; i >= 0; i--) if (this._offers[i]!.quest === e.Quest) this._offers.splice(i, 1);
      if (!this.defs.has(e.Quest)) this.requestDefinition(e.Quest); // the server sends it first; this only fills a gap
      const restore = full && this.savedTracked.includes(e.Quest);
      if ((e.TrackHint || restore) && !this.trackedIds.includes(e.Quest) && this.trackedIds.length < MAX_TRACKED_QUESTS) {
        this.trackedIds.push(e.Quest);
        trackingChanged = true;
        if (e.TrackHint) saveTracking = true;
      }
      if (!full && (!prev || prev.status !== run.status || prev.step !== run.step)) this.runChanged.emit({ quest: e.Quest, prev, run });
    }
    if (saveTracking) this.saveTracked();
    if (trackingChanged) this.emit("tracking");
    this.emit("quests");
  }

  private dropRun(quest: number): void {
    this._runs.delete(quest);
    this.picks.delete(quest);
    const def = this.defs.get(quest);
    if (def) for (const o of def.Objectives) this.values.delete(o.Index);
  }

  /** A step change: the old step's stored values go (the server sends the new step's objectives). */
  private clearStepValues(quest: number, stepId: number): void {
    const def = this.defs.get(quest);
    if (def) for (const o of def.Objectives) if (o.Step === stepId) this.values.delete(o.Index);
  }

  private npcEntry(id: number, e: NpcQuests): void {
    const old = this._npcs.get(id);
    if (old) for (const q of old.offered) {
      const seen = this.seenOffers.get(q);
      if (seen && seen.npc === id && !e.offered.includes(q)) this.seenOffers.delete(q);
    }
    for (const q of e.offered) {
      if (this._runs.has(q)) continue;
      const seen = this.seenOffers.get(q);
      if (!seen || seen.npc !== id) this.seenOffers.set(q, { quest: q, npc: id, map: this.mapIndex });
    }
    if (e.icon === QuestIcon.None && !e.offered.length && !e.finishing.length && !e.choosing.length) this._npcs.delete(id);
    else this._npcs.set(id, e);
  }

  /** GameScene.AddQuestItem: stack onto matching stacks first, then the first free slot. */
  private addQuestItem(item: UserItem): void {
    const info = this.items.get(item.ItemIndex);
    if (info && info.StackSize > 1) {
      for (const temp of this.questInventory) {
        if (!temp || temp.ItemIndex !== item.ItemIndex || temp.Count >= info.StackSize) continue;
        if (item.Count + temp.Count <= info.StackSize) { temp.Count += item.Count; return; }
        item.Count -= info.StackSize - temp.Count;
        temp.Count = info.StackSize;
      }
    }
    const free = this.questInventory.indexOf(null);
    if (free >= 0) this.questInventory[free] = item;
  }

  // — Tracking (QuestTrackingDialog.AddQuest / RemoveQuest / UpdateTrackedQuests, Settings.Load/SaveTrackedQuests) —

  isTracked(id: number): boolean {
    return this.trackedIds.includes(id);
  }

  /** QuestTrackingDialog.AddQuest(quest, New): `fromSaved` = New (restored at login, not saved again). */
  trackQuest(id: number, fromSaved = false): void {
    if (this.trackedIds.includes(id) || this.trackedIds.length >= MAX_TRACKED_QUESTS || !this._runs.has(id)) return;
    this.trackedIds.push(id);
    if (!fromSaved) this.saveTracked();
    this.emit("tracking");
  }

  /** QuestTrackingDialog.RemoveQuest. */
  untrackQuest(id: number): void {
    const i = this.trackedIds.indexOf(id);
    if (i >= 0) this.trackedIds.splice(i, 1);
    this.saveTracked();
    this.emit("tracking");
  }

  /** The tracked runs (tracking order) whose definition is here. */
  trackedRuns(): QuestRun[] {
    const out: QuestRun[] = [];
    for (const id of this.trackedIds) { const r = this._runs.get(id); if (r && this.defs.has(id)) out.push(r); }
    return out;
  }

  /** Settings.LoadTrackedQuests(charName). */
  private loadTracked(): void {
    this.savedTracked = new Array<number>(MAX_TRACKED_QUESTS).fill(-1);
    try {
      const raw = localStorage.getItem(TRACKING_KEY(this.userName));
      const ids = raw ? (JSON.parse(raw) as unknown) : null;
      if (Array.isArray(ids)) ids.slice(0, MAX_TRACKED_QUESTS).forEach((v, i) => { this.savedTracked[i] = typeof v === "number" ? v : -1; });
    } catch { /* storage blocked or bad data: nothing tracked */ }
  }

  /** QuestTrackingDialog.UpdateTrackedQuests -> Settings.SaveTrackedQuests(User.Name). */
  private saveTracked(): void {
    for (let j = 0; j < MAX_TRACKED_QUESTS; j++) this.savedTracked[j] = j < this.trackedIds.length ? this.trackedIds[j]! : -1;
    if (!this.userName) return;
    try { localStorage.setItem(TRACKING_KEY(this.userName), JSON.stringify(this.savedTracked)); } catch { /* storage blocked */ }
  }

  // — Requests (the server validates everything; nothing changes here until it answers). Accept / TurnIn / Abandon /
  //   Choose lock the quest (isPending) until S.QuestStates, S.QuestRefused or QUEST_PENDING_MS —

  private markPending(quest: number): void {
    this.pendingAt.set(quest, Date.now());
    this.emit("pending");
  }

  /** C.QuestAccept: at a giver (its ObjectID), or 0 for an S.QuestOffer prompt. */
  accept(quest: number, npcObjectId: number): void {
    if (npcObjectId === 0) this.dropOffer(quest);
    this.markPending(quest);
    this.send(ClientPackets.QuestAccept({ Quest: quest, NpcObjectID: npcObjectId }));
  }
  /** C.QuestTurnIn at the finishing NPC; `picks` = positions in def.Rewards, one per choice group. */
  turnIn(quest: number, npcObjectId: number, picks: number[]): void {
    this.markPending(quest);
    this.send(ClientPackets.QuestTurnIn({ Quest: quest, NpcObjectID: npcObjectId, RewardPicks: picks }));
  }
  abandon(quest: number): void {
    this.markPending(quest);
    this.send(ClientPackets.QuestAbandon({ Quest: quest }));
  }
  /** C.QuestShare (no lock: the run does not change; a refusal comes back as S.QuestRefused). */
  share(quest: number): void { this.send(ClientPackets.QuestShare({ Quest: quest })); }
  /** C.QuestChoose: a branch ChoiceKey at an NPC that starts or finishes the quest. */
  choose(quest: number, npcObjectId: number, choiceKey: string): void {
    this.markPending(quest);
    this.send(ClientPackets.QuestChoose({ Quest: quest, NpcObjectID: npcObjectId, ChoiceKey: choiceKey }));
  }
  /** C.QuestDefinitionRequest once per quest (the server answers for runs, history and offers only). */
  requestDefinition(quest: number): void {
    if (this.defs.has(quest) || this.asked.has(quest)) return;
    this.asked.add(quest);
    this.send(ClientPackets.QuestDefinitionRequest({ Quest: quest }));
  }
  /** Declines (forgets) an S.QuestOffer prompt; the server sends nothing for a decline. */
  dropOffer(quest: number): void {
    const before = this._offers.length;
    for (let i = this._offers.length - 1; i >= 0; i--) if (this._offers[i]!.quest === quest) this._offers.splice(i, 1);
    if (this._offers.length !== before) this.emit("offers");
  }
}

/** NPCObject.DrawBody quest icon: Prguse image 981 + QuestIcon * 2 + QuestIndex (QuestIndex blinks 0/1 every 500 ms). */
export function questIconImage(icon: number, nowMs: number): number {
  return 981 + icon * 2 + (Math.floor(nowMs / 500) % 2);
}

/** MiniMapDialog NPC quest marker: "!" / "?" and its colour for the NPC's QuestIcon, or null. */
export function questMiniMapMarker(icon: number | null): { text: string; colour: string } | null {
  switch (icon) {
    case QuestIcon.ExclamationBlue: return { text: "!", colour: "dodgerblue" };
    case QuestIcon.ExclamationYellow: return { text: "!", colour: "yellow" };
    case QuestIcon.ExclamationGreen: return { text: "!", colour: "green" };
    case QuestIcon.QuestionBlue: return { text: "?", colour: "dodgerblue" };
    case QuestIcon.QuestionWhite: return { text: "?", colour: "white" };
    case QuestIcon.QuestionYellow: return { text: "?", colour: "yellow" };
    case QuestIcon.QuestionGreen: return { text: "?", colour: "green" };
  }
  return null;
}
