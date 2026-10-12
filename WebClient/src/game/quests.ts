// The user's quest state (GameScene / UserObject quest data, DOM-free):
//   GameScene.QuestInfoList (S.NewQuestInfo, MirScene.NewQuestInfo -> UpsertQuestInfo), User.CurrentQuests (S.ChangeQuest),
//   User.CompletedQuests (S.CompleteQuest), User.QuestInventory (UserInformation + S.GainedQuestItem / S.DeleteQuestItem),
//   the tracked-quest ids (QuestTrackingDialog.TrackedQuestsIds + Settings.TrackedQuests: the desktop keeps them in
//   Data\UserData\QuestTracking.ini per character; here localStorage per character), NPCObject.GetAvailableQuests /
//   CanAccept / UpdateBestQuestIcon and ClientQuestInfo.GetQuestIcon. The windows are src/ui/questWindow.ts.
// ClientQuestProgress.QuestInfo is not stored on the progress (BindQuest): it is looked up by Id when needed, so a quest
// whose info is missing is skipped instead of throwing.

import type { ClientQuestInfo, ClientQuestProgress, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirClass, QuestIcon, QuestState, QuestType, RequiredClass } from "../protocol/enums.generated.ts";
import type { ItemInfos } from "./items.ts";

/** Globals.MaxConcurrentQuests. */
export const MAX_CONCURRENT_QUESTS = 20;
/** QuestTrackingDialog: at most 5 tracked quests (Settings.TrackedQuests = new int[5]). */
export const MAX_TRACKED_QUESTS = 5;
/** UserObject.QuestInventory = new UserItem[40]. */
export const QUEST_INVENTORY_SIZE = 40;

/** A quest with its info (ClientQuestProgress + its bound QuestInfo). */
export interface QuestEntry { info: ClientQuestInfo; progress: ClientQuestProgress }

export type QuestChange = "quests" | "info" | "completed" | "items" | "tracking" | "user";

/** ClientQuestInfo.GetQuestIcon. */
export function questIconFor(info: ClientQuestInfo, taken = false, completed = false): number {
  switch (info.Type) {
    case QuestType.General:
    case QuestType.Repeatable:
      return completed ? QuestIcon.QuestionYellow : taken ? QuestIcon.QuestionWhite : QuestIcon.ExclamationYellow;
    case QuestType.Daily:
      return completed ? QuestIcon.QuestionBlue : taken ? QuestIcon.QuestionWhite : QuestIcon.ExclamationBlue;
    case QuestType.Story:
      return completed ? QuestIcon.QuestionGreen : taken ? QuestIcon.QuestionWhite : QuestIcon.ExclamationGreen;
  }
  return QuestIcon.None;
}

/** ClientQuestProgress.Icon. */
export function entryIcon(e: QuestEntry): number {
  return questIconFor(e.info, e.progress.Taken, e.progress.Completed);
}

/** ClientQuestInfo.SameFinishNPC. */
export function sameFinishNPC(info: ClientQuestInfo): boolean {
  return info.NPCIndex === info.FinishNPCIndex;
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

const TRACKING_KEY = (name: string) => `crystal.questTracking.${name}`;

export class Quests {
  /** GameScene.QuestInfoList (Map keeps the insertion order; an upsert replaces in place). */
  private readonly infos = new Map<number, ClientQuestInfo>();
  /** User.CurrentQuests. */
  current: ClientQuestProgress[] = [];
  /** User.CompletedQuests. */
  completed: number[] = [];
  /** User.QuestInventory. */
  questInventory: (UserItem | null)[] = new Array<UserItem | null>(QUEST_INVENTORY_SIZE).fill(null);
  /** QuestTrackingDialog.TrackedQuestsIds (display order). */
  trackedIds: number[] = [];
  /** Settings.TrackedQuests (the saved slots, -1 = empty), loaded per character on UserInformation. */
  private savedTracked: number[] = new Array<number>(MAX_TRACKED_QUESTS).fill(-1);

  // The user (UserObject) fields the quest checks read.
  userName = "";
  level = 0;
  userClass = 0;
  gender = 0;

  private readonly listeners = new Set<(c: QuestChange) => void>();

  constructor(private readonly send: (f: Uint8Array) => void, private readonly items: ItemInfos) {}

  onChange(fn: (c: QuestChange) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private emit(c: QuestChange): void {
    for (const fn of this.listeners) {
      try { fn(c); } catch (e) { console.warn("[quests] listener:", e); }
    }
  }

  info(index: number): ClientQuestInfo | undefined {
    return this.infos.get(index);
  }

  /** GameScene.BindQuest: the progress with its info, or null while the info is missing. */
  entry(progress: ClientQuestProgress): QuestEntry | null {
    const info = this.infos.get(progress.Id);
    return info ? { info, progress } : null;
  }

  /** User.CurrentQuests with their infos (unbound ones skipped). */
  currentEntries(): QuestEntry[] {
    const out: QuestEntry[] = [];
    for (const q of this.current) { const e = this.entry(q); if (e) out.push(e); }
    return out;
  }

  /** Observes every packet; returns true when it was a quest packet. Never consumes UserInformation / LevelChanged. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "UserInformation":
        // UserObject.Load: QuestInventory, and Settings.LoadTrackedQuests(info.Name)
        this.userName = p.Name; this.level = p.Level; this.userClass = p.Class; this.gender = p.Gender;
        this.questInventory = new Array<UserItem | null>(Math.max(QUEST_INVENTORY_SIZE, p.QuestInventory?.length ?? 0)).fill(null);
        p.QuestInventory?.forEach((it, i) => { this.questInventory[i] = it; });
        this.loadTracked();
        this.emit("items");
        this.emit("user");
        return false;
      case "LevelChanged": this.level = p.Level; this.emit("user"); return false;
      case "NewQuestInfo": // MirScene.NewQuestInfo -> GameScene.UpsertQuestInfo
        this.infos.set(p.Info.Index, p.Info);
        this.emit("info");
        return true;
      case "ChangeQuest": this.changeQuest(p.Quest, p.QuestState, p.TrackQuest); return true;
      case "CompleteQuest": // GameScene.CompleteQuest
        this.completed = p.CompletedQuests;
        this.emit("completed");
        return true;
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
      case "ShareQuest": return true; // the invitation box is questWindow.ts
    }
    return false;
  }

  /** GameScene.ChangeQuest (the tracking part of QuestTrackingDialog.AddQuest / RemoveQuest). */
  private changeQuest(quest: ClientQuestProgress, state: number, track: boolean): void {
    switch (state) {
      case QuestState.Add:
        this.current.push(quest);
        if (this.savedTracked.includes(quest.Id)) this.trackQuest(quest.Id, true);
        if (track) this.trackQuest(quest.Id);
        break;
      case QuestState.Update:
        for (let i = 0; i < this.current.length; i++) if (this.current[i]!.Id === quest.Id) this.current[i] = quest;
        break;
      case QuestState.Remove:
        for (let i = this.current.length - 1; i >= 0; i--) if (this.current[i]!.Id === quest.Id) this.current.splice(i, 1);
        this.untrackQuest(quest.Id);
        break;
    }
    this.emit("quests");
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
    if (this.trackedIds.includes(id) || this.trackedIds.length >= MAX_TRACKED_QUESTS) return;
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

  /** QuestTrackingDialog.DisplayQuests: the current quests that are tracked (CurrentQuests order). */
  trackedEntries(): QuestEntry[] {
    return this.currentEntries().filter((e) => this.trackedIds.includes(e.progress.Id));
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

  // — NPC quests (NPCObject) —

  /** NPCObject.CanAccept. */
  canAccept(quest: ClientQuestInfo): boolean {
    if (quest.MinLevelNeeded > this.level || quest.MaxLevelNeeded < this.level) return false;
    if ((quest.ClassNeeded & RequiredClass.None) !== RequiredClass.None) { // !ClassNeeded.HasFlag(RequiredClass.None)
      const bit = this.userClass === MirClass.Warrior ? RequiredClass.Warrior
        : this.userClass === MirClass.Wizard ? RequiredClass.Wizard
        : this.userClass === MirClass.Taoist ? RequiredClass.Taoist
        : this.userClass === MirClass.Assassin ? RequiredClass.Assassin
        : this.userClass === MirClass.Archer ? RequiredClass.Archer : 0;
      if (bit && (quest.ClassNeeded & bit) === 0) return false;
    }
    return quest.QuestNeeded <= 0 || this.completed.includes(quest.QuestNeeded);
  }

  /** NPCObject.GetAvailableQuests. NPCObject.Quests is QuestInfoList where NPCIndex == ObjectID (taken live here, the
   *  desktop snapshots it in NPCObject.Load; ObjectNPC.QuestIDs is not used by the desktop either). */
  availableQuests(npcId: number, returnFirst = false): QuestEntry[] {
    const quests: QuestEntry[] = [];
    for (const q of this.current) {
      if (this.completed.includes(q.Id)) continue;
      const info = this.infos.get(q.Id);
      if (!info) continue;
      if (info.FinishNPCIndex === npcId) quests.push({ info, progress: q });
      else if (info.NPCIndex === npcId && sameFinishNPC(info)) {
        quests.push({ info, progress: q });
        if (returnFirst) return quests;
      }
    }
    for (const info of this.infos.values()) {
      if (info.NPCIndex !== npcId) continue;
      if (quests.some((e) => e.info.Index === info.Index)) continue;
      if (!this.canAccept(info) || this.completed.includes(info.Index)) continue;
      const taken = this.current.some((p) => p.Id === info.Index);
      quests.push({ info, progress: { Id: info.Index, TaskList: [], Taken: taken, Completed: false, New: false } });
      if (returnFirst) return quests;
    }
    return quests;
  }

  /** NPCObject.UpdateBestQuestIcon: the first available quest's icon, or null (QuestIcon.None). */
  npcQuestIcon(npcId: number): number | null {
    const first = this.availableQuests(npcId, true)[0];
    const icon = first ? entryIcon(first) : QuestIcon.None;
    return icon === QuestIcon.None ? null : icon;
  }

  // — Requests —

  /** QuestListDialog accept button / the ShareQuest Yes button (NPCIndex 0). */
  accept(npcIndex: number, questIndex: number): void { this.send(ClientPackets.AcceptQuest({ NPCIndex: npcIndex, QuestIndex: questIndex })); }
  /** QuestListDialog finish button. */
  finish(questIndex: number, selectedItemIndex: number): void { this.send(ClientPackets.FinishQuest({ QuestIndex: questIndex, SelectedItemIndex: selectedItemIndex })); }
  /** QuestDetailDialog cancel button (after AskCancelQuest). */
  abandon(questIndex: number): void { this.send(ClientPackets.AbandonQuest({ QuestIndex: questIndex })); }
  /** QuestDetailDialog share button. */
  share(questIndex: number): void { this.send(ClientPackets.ShareQuest({ QuestIndex: questIndex })); }
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
