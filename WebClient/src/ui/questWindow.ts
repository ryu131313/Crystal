// Quest windows (Client/MirScenes/Dialogs/QuestDialogs.cs, DOM edition) over the state in src/game/quests.ts:
//   QuestListDialog     the NPC's available quests (NPCDialog.QuestButton): rows with the quest icon, message, rewards with
//                       the select-reward cells, Accept (C.AcceptQuest) / Finish (C.FinishQuest) / Leave.
//   QuestDetailDialog   a current quest: message with progress, rewards, Share (C.ShareQuest), Cancel (C.AbandonQuest).
//   QuestDiaryDialog    the quest log (Q, KeybindOptions.Quests): current quests grouped by Group (expand / collapse),
//                       left-click = details, right-click = track / untrack (at most 5).
//   QuestTrackingDialog the tracked quests' names and task lines (top left).
//   Quest items         InventoryDialog's quest tab (User.QuestInventory, read-only cells): its own small window, opened by
//                       the "Quest" button this module adds to the inventory window's title bar.
//   GameScene.ShareQuest the "X would like to share a quest with you" box (Yes = C.AcceptQuest with NPCIndex 0).
// QuestMessage markup: {text/Colour} and the [MONSTER|NPC|ITEM:idx(|name)] / <$KIND:idx> links (NPCDialog patterns; the
// names come from GameInfo, the desktop hover tooltip is a title attribute here).

import type { ItemInfo, QuestItemReward, ServerPacket, UserItem } from "../protocol/server.ts";
import { MirGender, RequiredGender } from "../protocol/enums.generated.ts";
import { MAX_CONCURRENT_QUESTS, MAX_TRACKED_QUESTS, entryIcon, printTimeSpanFromSeconds, sameFinishNPC, type QuestEntry, type Quests } from "../game/quests.ts";
import type { ItemInfos } from "../game/items.ts";
import { type GameInfo, itemFriendlyName } from "../game/gameInfo.ts";
import type { NpcWindow } from "./npcWindow.ts";
import { itemIcon, type InventoryWindow } from "./inventoryWindow.ts";
import { libIcon } from "./buffBar.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

// ClientTextKeys (Client/Localization/English.json)
const TASK_TITLE = "Tasks", PROGRESS_TITLE = "Progress", RETURN_TITLE = "Quest Return", TIME_LIMIT_TITLE = "Time Limit";
const MARKUP = /\{(.*?)\/(.*?)\}|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>/gi;

function prguseIcon(index: number): HTMLElement {
  const img = el("img", { class: "quest-icon", alt: "", draggable: false, hidden: true });
  void libIcon("Prguse", index).then((url) => { if (url) { img.src = url; img.hidden = false; } });
  return img;
}

function windowFrame(cls: string, title: HTMLElement, onClose: () => void): HTMLElement {
  const w = el("div", { class: `window ${cls}`, hidden: true },
    el("div", { class: "window-title" }, title, el("button", { class: "window-close", onclick: onClose }, "×")));
  w.addEventListener("mousedown", (e) => e.stopPropagation());
  w.addEventListener("contextmenu", (e) => e.preventDefault());
  return w;
}

export class QuestWindow {
  private readonly stage: HTMLElement;
  private readonly quests: Quests;
  private readonly npc: NpcWindow;
  private readonly invWindow: InventoryWindow;
  private readonly items: ItemInfos;
  private readonly gameInfo: GameInfo;

  // QuestListDialog
  private readonly list = windowFrame("quest-list", el("span", {}, "Quests"), () => this.hideList());
  private readonly listCount = el("span", { class: "quest-count" });
  private readonly listRows = el("div", { class: "quest-rows" });
  private readonly listMessage = el("div", { class: "quest-message" });
  private readonly listRewards = el("div", { class: "quest-rewards" });
  private readonly acceptButton = el("button", { class: "primary", onclick: () => this.acceptClick() }, "Accept");
  private readonly finishButton = el("button", { class: "primary", onclick: () => this.finishClick() }, "Finish");
  /** QuestListDialog.CurrentNPCID / Quests / SelectedQuest; QuestRewards.SelectedItemIndex. */
  private currentNpcId = 0;
  private listQuests: QuestEntry[] = [];
  private selected: QuestEntry | null = null;
  private selectedItemIndex = -1;

  // QuestDetailDialog
  private readonly detail = windowFrame("quest-detail", el("span", {}, "Quest"), () => (this.detail.hidden = true));
  private readonly detailMessage = el("div", { class: "quest-message quest-message-long" });
  private readonly detailRewards = el("div", { class: "quest-rewards" });
  private detailQuestId = -1;

  // QuestDiaryDialog
  private readonly diary = windowFrame("quest-diary", el("span", {}, "Quest Log"), () => (this.diary.hidden = true));
  private readonly diaryCount = el("span", { class: "quest-count" });
  private readonly diaryBody = el("div", { class: "quest-groups" });
  /** QuestDiaryDialog.ExpandedGroups (empty = all expanded). */
  private readonly expandedGroups = new Set<string>();
  private diarySelected = -1;

  // QuestTrackingDialog
  private readonly tracking = el("div", { class: "quest-tracking", hidden: true });

  // Quest items (InventoryDialog quest tab)
  private readonly itemsWindow = windowFrame("quest-items", el("span", {}, "Quest Items"), () => (this.itemsWindow.hidden = true));
  private readonly itemsGrid = el("div", { class: "inv-grid quest-items-grid" });

  constructor(stage: HTMLElement, quests: Quests, npc: NpcWindow, invWindow: InventoryWindow, items: ItemInfos, gameInfo: GameInfo) {
    this.stage = stage;
    this.quests = quests;
    this.npc = npc;
    this.invWindow = invWindow;
    this.items = items;
    this.gameInfo = gameInfo;

    this.list.querySelector(".window-title")!.insertBefore(this.listCount, this.list.querySelector(".window-close"));
    this.list.append(this.listRows, this.listMessage, this.listRewards,
      el("div", { class: "row quest-buttons" }, this.acceptButton, this.finishButton, el("button", { onclick: () => this.hideList() }, "Leave")));
    this.detail.append(this.detailMessage, this.detailRewards,
      el("div", { class: "row quest-buttons" },
        el("button", { onclick: () => { if (this.detailQuestId >= 0) quests.share(this.detailQuestId); } }, "Share"),
        el("button", { onclick: () => this.cancelClick() }, "Cancel quest")));
    this.diary.querySelector(".window-title")!.insertBefore(this.diaryCount, this.diary.querySelector(".window-close"));
    this.diary.append(this.diaryBody, el("div", { class: "quest-hint" }, "Left-click: details. Right-click: track (max 5)."),
      el("div", { class: "row quest-buttons" }, el("button", { onclick: () => this.toggleItems(true) }, "Quest items"), el("button", { onclick: () => (this.diary.hidden = true) }, "Close")));
    this.itemsWindow.append(this.itemsGrid);
    this.tracking.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.list, this.detail, this.diary, this.tracking, this.itemsWindow);

    // InventoryDialog.QuestButton: a title-bar button on the inventory window opens the quest items
    const invTitle = invWindow.root.querySelector(".window-title");
    invTitle?.insertBefore(el("button", { class: "quest-inv-button", title: "Quest items", onclick: () => this.toggleItems() }, "Quest"), invTitle.querySelector(".window-close"));

    // NPCDialog.QuestButton -> QuestListDialog.Toggle; NPCDialog.Hide hides QuestListDialog
    npc.questsAvailable = (id) => quests.availableQuests(id).length > 0;
    npc.onQuestButton = () => this.toggleList();
    const prevClose = npc.onClose;
    npc.onClose = () => { prevClose(); this.hideList(); };

    quests.onChange((c) => this.changed(c));
    gameInfo.onChange(() => this.refreshMessages()); // link names follow their info
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.Quests: Q without Alt (RequireAlt = 0) toggles QuestDiaryDialog
      if (matches(e, KeybindOptions.Quests)) this.toggleDiary();
      // KeybindOptions.Closeall hides QuestListDialog, QuestDetailDialog, QuestLogDialog
      else if (matches(e, KeybindOptions.Closeall)) { this.hideList(); this.detail.hidden = true; this.diary.hidden = true; }
    }, { signal: gameSignal() });
  }

  /** Server packets (after Quests.handle has updated the state). GameScene.ShareQuest. */
  handle(p: ServerPacket): void {
    if (p.type !== "ShareQuest") return;
    const quest = this.quests.info(p.QuestIndex);
    if (!quest) return;
    messageBox(this.stage, `${p.SharerName} would like to share a quest with you. Do you accept?`, [
      { label: "Yes", primary: true, onClick: () => this.quests.accept(0, quest.Index) },
      { label: "No" },
    ]);
  }

  /** MainDialog.QuestButton / KeybindOptions.Quests: QuestDiaryDialog.Toggle. */
  toggleDiary(show = this.diary.hidden): void {
    this.diary.hidden = !show;
    if (show) this.renderDiary();
  }

  // — State changes (GameScene.ChangeQuest's refresh tail, plus the windows that read the other lists) —

  private changed(c: string): void {
    // QuestTrackingDialog.DisplayQuests; if (QuestListDialog.Visible) DisplayInfo(); if (QuestLogDialog.Visible) DisplayQuests()
    if (c === "quests" || c === "tracking" || c === "info") this.renderTracking();
    if (c === "quests" || c === "completed" || c === "user" || c === "info") {
      if (!this.list.hidden) this.displayListInfo();
      this.npc.refreshQuestButton();
    }
    if ((c === "quests" || c === "tracking" || c === "info" || c === "user") && !this.diary.hidden) this.renderDiary();
    if (c === "quests" && !this.detail.hidden) {
      // the detail dialog holds the replaced progress object on the desktop; here it follows the update
      const q = this.quests.current.find((x) => x.Id === this.detailQuestId);
      const e = q && this.quests.entry(q);
      if (e) this.showDetail(e);
    }
    if (c === "items" && !this.itemsWindow.hidden) this.renderItems();
  }

  private refreshMessages(): void {
    if (!this.list.hidden && this.selected) this.listMessage.replaceChildren(...this.messageLines(this.selected, false));
    if (!this.detail.hidden) {
      const q = this.quests.current.find((x) => x.Id === this.detailQuestId);
      const e = q && this.quests.entry(q);
      if (e) this.detailMessage.replaceChildren(...this.messageLines(e, true));
    }
    if (!this.itemsWindow.hidden) this.renderItems();
  }

  // — QuestListDialog —

  private toggleList(): void {
    if (this.list.hidden) this.showList();
    else this.hideList();
  }

  /** QuestListDialog.Show: CurrentNPCID = GameScene.NPCID, Reset, DisplayInfo. */
  private showList(): void {
    this.list.hidden = false;
    this.currentNpcId = this.npc.currentNpcId;
    this.selected = null;
    this.displayListInfo();
  }

  /** QuestListDialog.Hide (also hides the NPC dialog). */
  private hideList(): void {
    if (this.list.hidden) return;
    this.list.hidden = true;
    this.npc.close();
  }

  /** QuestListDialog.DisplayInfo: no available quest (or the NPC gone) hides the dialog. Keeps the selection by quest
   *  index (the desktop resets to the first row on every refresh: Reset() drops SelectedQuest). */
  private displayListInfo(): void {
    this.listQuests = this.quests.availableQuests(this.currentNpcId);
    if (this.listQuests.length < 1) { this.hideList(); return; }
    const kept = this.listQuests.find((q) => q.info.Index === this.selected?.info.Index);
    if (!kept) this.selectedItemIndex = -1;
    this.selected = kept ?? this.listQuests[0]!;
    this.renderList();
    this.selectListQuest(this.selected, true);
  }

  /** QuestListDialog.RefreshInterface / QuestRow.UpdateInterface. */
  private renderList(): void {
    this.listCount.textContent = `List: ${this.listQuests.length}`;
    this.listRows.replaceChildren(...this.listQuests.map((q) => {
      const icon = entryIcon(q);
      const row = el("div", { class: `quest-row${q === this.selected ? " selected" : ""}`, onclick: () => this.selectListQuest(q) },
        prguseIcon(961 + icon + (icon > 3 ? 15 : 0)),
        el("span", { class: "quest-level" }, q.info.MinLevelNeeded > 0 ? `Lv ${q.info.MinLevelNeeded}` : ""),
        el("span", { class: "quest-name" }, q.info.Name));
      return row;
    }));
  }

  /** QuestRow click: Reward.UpdateRewards, Message.UpdateQuest, ReDisplayButtons. */
  private selectListQuest(q: QuestEntry, refresh = false): void {
    if (q === this.selected && !refresh) return;
    if (q !== this.selected) this.selectedItemIndex = -1; // QuestRewards.CleanRewards
    this.selected = q;
    for (const [i, row] of Array.from(this.listRows.children).entries()) row.classList.toggle("selected", this.listQuests[i] === q);
    this.listMessage.replaceChildren(...this.messageLines(q, false));
    this.renderRewards(this.listRewards, q, true);
    // ReDisplayButtons
    this.acceptButton.hidden = !(!q.progress.Taken && this.quests.current.length < MAX_CONCURRENT_QUESTS);
    this.finishButton.hidden = !q.progress.Completed;
  }

  /** QuestListDialog accept button. */
  private acceptClick(): void {
    const q = this.selected;
    if (!q || q.progress.Taken) return;
    this.quests.accept(q.info.NPCIndex, q.info.Index);
  }

  /** QuestListDialog finish button: a quest with select rewards needs one picked (YouMustSelectRewardItem). */
  private finishClick(): void {
    const q = this.selected;
    if (!q || !q.progress.Completed) return;
    if (this.selectedItemIndex < 0 && q.info.RewardsSelectItem.length > 0) {
      messageBox(this.stage, "You must select a reward item.");
      return;
    }
    this.quests.finish(q.info.Index, this.selectedItemIndex);
  }

  // — QuestDetailDialog —

  /** QuestDetailDialog.DisplayQuestDetails. */
  private showDetail(q: QuestEntry): void {
    this.detailQuestId = q.progress.Id;
    this.detail.querySelector(".window-title span")!.textContent = q.info.Name;
    this.detailMessage.replaceChildren(...this.messageLines(q, true));
    this.renderRewards(this.detailRewards, q, false);
    this.detail.hidden = false;
  }

  /** QuestDetailDialog cancel button: AskCancelQuest, then C.AbandonQuest and hide. */
  private cancelClick(): void {
    const id = this.detailQuestId;
    if (id < 0) return;
    messageBox(this.stage, "Are you sure you want to cancel this quest?", [
      { label: "Yes", primary: true, onClick: () => { this.quests.abandon(id); this.detail.hidden = true; } },
      { label: "No" },
    ]);
  }

  // — QuestMessage —

  /** QuestMessage.UpdateQuest + AdjustDescription + NewText. `displayProgress` = the detail dialog's message. */
  private messageLines(q: QuestEntry, displayProgress: boolean): HTMLElement[] {
    const { info, progress } = q;
    const lines: string[] = [info.Name];
    const titles = new Set<number>([0]);
    const section = (title: string, body: string[]) => { lines.push(" "); titles.add(lines.length); lines.push(title); lines.push(...body); };
    if (progress.Taken && !sameFinishNPC(info) && info.CompletionDescription.length > 0 && this.currentNpcId === info.FinishNPCIndex) {
      lines.push(...info.CompletionDescription);
    } else {
      lines.push(...info.Description);
      if (info.TaskDescription.length > 0) section(TASK_TITLE, info.TaskDescription);
      if (info.ReturnDescription.length > 0) section(RETURN_TITLE, info.ReturnDescription);
      if (info.TimeLimitInSeconds > 0) section(TIME_LIMIT_TITLE, [printTimeSpanFromSeconds(info.TimeLimitInSeconds)]);
      if (progress.Taken && progress.TaskList.length > 0 && displayProgress) section(PROGRESS_TITLE, progress.TaskList);
    }
    return lines.map((line, i) => {
      const div = el("div", { class: `quest-line${titles.has(i) ? " title" : ""}${i === 0 ? " name" : ""}` });
      this.renderMarkup(div, line);
      return div;
    });
  }

  /** NewText's match list: {text/Colour} (NewColour) and the MONSTER / NPC / ITEM links (NewLink, cyan). */
  private renderMarkup(div: HTMLElement, line: string): void {
    let at = 0;
    for (const m of line.matchAll(MARKUP)) {
      if (m.index! > at) div.append(line.slice(at, m.index));
      at = m.index! + m[0].length;
      if (m[1] !== undefined) { div.append(el("span", { style: `color:${m[2]}` }, m[1])); continue; }
      const kind = (m[3] ?? m[6])!.toUpperCase(), idx = Number(m[4] ?? m[7]), name = m[5];
      const text = name || this.linkName(kind, idx) || `LINK_${idx}`;
      div.append(el("span", { class: "npc-ref", title: `${kind.charAt(0)}${kind.slice(1).toLowerCase()} ${idx}` }, text));
    }
    if (at < line.length) div.append(line.slice(at));
    if (!div.childNodes.length) div.append(" ");
  }

  /** NPCDialog.GetDisplayNameForLink (requests a missing info). */
  private linkName(kind: string, idx: number): string {
    if (kind === "ITEM") {
      const item = this.gameInfo.item(idx);
      if (item) return itemFriendlyName(item) || item.Name;
      this.gameInfo.requestItem(idx);
      return `Item ${idx}`;
    }
    if (kind === "MONSTER") {
      const m = this.gameInfo.monster(idx);
      if (m) return m.GameName || m.Name;
      this.gameInfo.requestMonster(idx);
      return `Monster ${idx}`;
    }
    const n = this.gameInfo.npc(idx);
    if (n) return n.Name;
    this.gameInfo.requestNPC(idx);
    return `NPC ${idx}`;
  }

  // — QuestRewards / QuestCell —

  /** QuestRewards.UpdateRewards: exp / gold / credit, the fixed items and the (same-gender) select items. */
  private renderRewards(box: HTMLElement, q: QuestEntry, selectable: boolean): void {
    const info = q.info;
    const top = el("div", { class: "quest-reward-line" });
    if (info.RewardExp > 0) top.append(prguseIcon(966), el("span", {}, `${info.RewardExp}`));
    if (info.RewardGold > 0) top.append(prguseIcon(965), el("span", {}, `${info.RewardGold}`));
    if (info.RewardCredit > 0) top.append(prguseIcon(2447), el("span", {}, `${info.RewardCredit}`));
    const parts: HTMLElement[] = [el("div", { class: "inv-label" }, "Rewards"), top];
    if (info.RewardsFixedItem.length > 0)
      parts.push(el("div", { class: "quest-reward-cells" }, ...info.RewardsFixedItem.slice(0, 5).map((r) => this.rewardCell(r, "fixed"))));
    const selRewards = this.filterRewards(info.RewardsSelectItem);
    if (selRewards.length > 0) {
      parts.push(el("div", { class: "inv-label" }, "Select one reward"));
      parts.push(el("div", { class: "quest-reward-cells" }, ...selRewards.slice(0, 5).map(({ reward, index }) => {
        const cell = this.rewardCell(reward, index === this.selectedItemIndex && selectable ? "selected" : "");
        if (selectable) cell.onclick = () => { this.selectedItemIndex = index; this.renderRewards(box, q, selectable); };
        return cell;
      })));
    }
    box.replaceChildren(...parts);
  }

  /** QuestRewards.FilterRewards: only the user's gender; keeps each reward's index in RewardsSelectItem (FindSelectedItemIndex). */
  private filterRewards(rewards: QuestItemReward[]): { reward: QuestItemReward; index: number }[] {
    const out: { reward: QuestItemReward; index: number }[] = [];
    rewards.forEach((reward, index) => {
      const g = reward.Item.RequiredGender;
      if (this.quests.gender === MirGender.Male && !(g & RequiredGender.Male)) return;
      if (this.quests.gender === MirGender.Female && !(g & RequiredGender.Female)) return;
      out.push({ reward, index });
    });
    return out;
  }

  /** QuestCell: the item icon, the count when > 1, the tooltip of a UserItem made from the ItemInfo. */
  private rewardCell(r: QuestItemReward, state: "fixed" | "selected" | ""): HTMLElement {
    const c = el("div", { class: `inv-cell quest-reward${state ? ` ${state}` : ""}`, title: this.rewardText(r.Item, r.Count) });
    const img = el("img", { alt: r.Item.Name, draggable: false });
    void itemIcon(r.Item.Image).then((url) => { if (url) img.src = url; });
    c.append(img);
    if (r.Count > 1) c.append(el("span", { class: "inv-count" }, String(r.Count)));
    return c;
  }

  private rewardText(info: ItemInfo, count: number): string {
    if (!this.items.get(info.Index)) return `${info.Name}${count > 1 ? ` x${count}` : ""}`;
    const item: UserItem = {
      UniqueID: 0n, ItemIndex: info.Index, CurrentDura: info.Durability, MaxDura: info.Durability, Count: Math.max(1, count), SoulBoundId: -1,
      Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [], RefinedValue: 0, RefineAdded: 0,
      RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false, Sealed: null, GMMade: false,
    };
    return this.invWindow.describe(item);
  }

  // — QuestDiaryDialog —

  /** QuestDiaryDialog.DisplayQuests (QuestGroupQuestItem / QuestSingleQuestItem). */
  private renderDiary(): void {
    const entries = this.quests.currentEntries();
    this.diaryCount.textContent = `List: ${this.quests.current.length}/${MAX_CONCURRENT_QUESTS}`;
    const groups = new Map<string, QuestEntry[]>();
    for (const e of entries) { const g = groups.get(e.info.Group); if (g) g.push(e); else groups.set(e.info.Group, [e]); }
    const allExpanded = this.expandedGroups.size === 0;
    this.diaryBody.replaceChildren(...Array.from(groups, ([group, quests]) => {
      const expanded = allExpanded || this.expandedGroups.has(group);
      const head = el("div", { class: "quest-group-head", onclick: () => this.toggleGroup(group, groups) },
        el("span", { class: "quest-expand" }, expanded ? "−" : "+"), el("span", { class: "quest-group-name" }, group));
      const rows = expanded ? quests.map((e) => this.diaryRow(e)) : [];
      return el("div", { class: "quest-group" }, head, ...rows);
    }));
  }

  /** QuestGroupQuestItem.ChangeExpand + QuestDiaryDialog's ExpandedChanged (ExpandedGroups holds the expanded ones). */
  private toggleGroup(group: string, groups: Map<string, QuestEntry[]>): void {
    if (this.expandedGroups.size === 0) for (const g of groups.keys()) this.expandedGroups.add(g);
    if (this.expandedGroups.has(group)) this.expandedGroups.delete(group);
    else this.expandedGroups.add(group);
    // every group collapsed would read as "all expanded" (ExpandedGroups.Count <= 0): the desktop has the same rule
    this.renderDiary();
  }

  /** QuestSingleQuestItem: "Lv{min} name", the state, grey when 10+ levels below the user, yellow when new. */
  private diaryRow(e: QuestEntry): HTMLElement {
    const lowLevel = this.quests.level - e.info.MinLevelNeeded > 10;
    const tracked = this.quests.isTracked(e.progress.Id);
    const row = el("div", { class: `quest-diary-row${lowLevel ? " low" : e.progress.New ? " new" : ""}${this.diarySelected === e.progress.Id ? " selected" : ""}` },
      el("span", { class: "quest-track", title: tracked ? "Tracked" : "" }, tracked ? "◆" : ""),
      el("span", { class: "quest-name" }, `${`Lv${e.info.MinLevelNeeded}`.padEnd(4)} ${e.info.Name}`),
      el("span", { class: "quest-state" }, e.progress.Completed ? "(Complete)" : "(In Progress)"));
    row.onmousedown = (ev) => {
      ev.stopPropagation();
      if (ev.button === 0) this.showDetail(e);
      else if (ev.button === 2) {
        if (tracked) this.quests.untrackQuest(e.progress.Id);
        else if (this.quests.trackedIds.length < MAX_TRACKED_QUESTS) this.quests.trackQuest(e.progress.Id);
      }
      this.diarySelected = e.progress.Id; // OnSelectedQuestChanged
      this.renderDiary();
    };
    return row;
  }

  // — QuestTrackingDialog —

  /** QuestTrackingDialog.DisplayQuests: hidden when nothing tracked. */
  private renderTracking(): void {
    const tracked = this.quests.trackedEntries();
    this.tracking.hidden = tracked.length < 1;
    this.tracking.replaceChildren(...tracked.map((e) => el("div", { class: "quest-track-quest" },
      el("div", { class: "quest-track-name" }, e.info.Name),
      ...e.progress.TaskList.map((t) => el("div", { class: "quest-track-task" }, t)))));
  }

  // — Quest items (InventoryDialog.QuestGrid) —

  private toggleItems(show = this.itemsWindow.hidden): void {
    this.itemsWindow.hidden = !show;
    if (show) this.renderItems();
  }

  private renderItems(): void {
    this.itemsGrid.replaceChildren(...this.quests.questInventory.map((item) => {
      if (item && !this.items.get(item.ItemIndex)) this.gameInfo.requestItem(item.ItemIndex);
      return this.invWindow.viewCell(item);
    }));
  }

  /** For game.ts: the NPC's above-head QuestIcon (NPCObject.UpdateBestQuestIcon), or null. */
  npcQuestIcon(npcObjectId: number): number | null {
    return this.quests.npcQuestIcon(npcObjectId);
  }
}
