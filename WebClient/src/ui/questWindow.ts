// Quest windows: port of Client/MirScenes/Dialogs/QuestDialogs.cs with the desktop artwork, over the quest V2 state in
// src/game/quests.ts. Quest V2 adapter (plan Part B "Clients": minimal, no bars, markers or new classic features): the
// message shows the summary, the current step's objectives as client-built text lines and the step / quest time limit;
// the NPC list comes from the NPC's S.QuestAvailability entry; Accept = C.QuestAccept (the NPC's ObjectID), Finish =
// C.QuestTurnIn (one pick per choice group from the select cells), Cancel = C.QuestAbandon; an S.QuestOffer (share,
// item, area) is the old "would like to share a quest" box (Yes = C.QuestAccept with 0).
//   QuestListDialog     (Prguse 950) the NPC's available quests (NPCDialog.QuestButton): 5 QuestRows + up / down,
//                       QuestMessage, QuestRewards with the select-reward cells, Accept (C.AcceptQuest) / Finish
//                       (C.FinishQuest) / Leave.
//   QuestDetailDialog   (Prguse 960) a current quest: message with progress, rewards, Share (C.ShareQuest), Cancel
//                       (C.AbandonQuest).
//   QuestDiaryDialog    (Prguse 961) the quest log (Q, KeybindOptions.Quests): QuestGroupQuestItem (expand / collapse)
//                       + QuestSingleQuestItem rows: left-click = details, right-click = track / untrack (at most 5).
//   QuestTrackingDialog the tracked quests' names and task lines (top left).
//   QuestMessage        fixed text rows with the up / down buttons, the position bar and the mouse wheel; {text/Colour}
//                       and the [MONSTER|NPC|ITEM:idx(|name)] / <$KIND:idx> links (NPCDialog patterns).
//   QuestRewards / QuestCell  exp / gold / credit, the fixed and the select item cells.
//   Quest items         InventoryDialog's quest tab (QuestButton + QuestGrid, read-only cells) as its own window on the
//                       inventory artwork, until the inventory window ports its tabs (toggleItems() is public for it).
//   S.QuestOffer        the "X would like to share a quest with you" box (Yes = C.QuestAccept with NpcObjectID 0).

import type { ItemInfo, QuestDefinitionInfo, ServerPacket, UserItem } from "../protocol/server.ts";
import { QuestEndpointRole, QuestRewardKind, QuestRunStatus } from "../protocol/enums.generated.ts";
import { MAX_CONCURRENT_QUESTS, MAX_TRACKED_QUESTS, QUEST_CATEGORY_LABEL, printTimeSpanFromSeconds, questIconFor, type Quests } from "../game/quests.ts";
import type { ItemInfos } from "../game/items.ts";
import { type GameInfo, itemFriendlyName } from "../game/gameInfo.ts";
import type { NpcWindow } from "./npcWindow.ts";
import type { NpcSession } from "../game/npcSession.ts";
import { itemIcon, type InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, closeButton, Lib, libSize, getUiRoot, styleTooltip, showHint, hideHint, uiSound, type Point, type Size } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

// ClientTextKeys (Client/Localization/English.json)
const TASK_TITLE = "Tasks", PROGRESS_TITLE = "Progress", RETURN_TITLE = "Quest Return", TIME_LIMIT_TITLE = "Time Limit";
const MARKUP = /\{(.*?)\/(.*?)\}|\[(MONSTER|NPC|ITEM):(\d+)(?:\|([^\]]+))?\]|<\$(MONSTER|NPC|ITEM):(\d+)>/gi;
const FONT_9 = "9pt Arial", FONT_TITLE = "bold 10pt Arial";
const LIME_GREEN = "#32cd32";
const QUEST_GRID_W = 8, QUEST_GRID_H = 5;

/** Stage scale (the page scales the 1024 x 768 stage), for drags. */
function stageScale(): number {
  const root = getUiRoot();
  return root.getBoundingClientRect().width / (root.offsetWidth || 1) || 1;
}

// — Item label (GameScene.CreateItemLabel / DisposeItemLabel) —

let itemLabelBox: HTMLDivElement | null = null;

function showItemLabel(text: string, clientX: number, clientY: number): void {
  const root = getUiRoot();
  if (!itemLabelBox || !itemLabelBox.isConnected) {
    itemLabelBox = document.createElement("div");
    styleTooltip(itemLabelBox);
    Object.assign(itemLabelBox.style, { position: "absolute", zIndex: "99999", pointerEvents: "none", whiteSpace: "pre", padding: "2px 4px", lineHeight: "1.2" });
    root.append(itemLabelBox);
  }
  itemLabelBox.textContent = text;
  itemLabelBox.hidden = false;
  const r = root.getBoundingClientRect();
  const s = stageScale();
  let x = (clientX - r.left) / s + 10, y = (clientY - r.top) / s + 10;
  const w = itemLabelBox.offsetWidth, h = itemLabelBox.offsetHeight;
  if (x + w > root.offsetWidth) x = root.offsetWidth - w;
  if (y + h > root.offsetHeight) y = root.offsetHeight - h;
  itemLabelBox.style.left = Math.max(0, x) + "px";
  itemLabelBox.style.top = Math.max(0, y) + "px";
  root.append(itemLabelBox); // keep on top
}
function hideItemLabel(): void { if (itemLabelBox) itemLabelBox.hidden = true; }

/** An item picture centred in a `box` area of `cell` (Items[image] drawn at ((w - size.Width) / 2, (h - size.Height) / 2))
 *  plus the yellow, un-outlined count label with its right edge at `countRight` and its bottom at the cell's bottom. */
function drawItem(cell: MirControl, image: number, count: number, box: Size, countRight: number): void {
  const pic = el("div", { style: `position:absolute;left:0;top:0;width:${box[0]}px;height:${box[1]}px;display:flex;align-items:center;justify-content:center;pointer-events:none` });
  const img = el("img", { alt: "", draggable: false, style: "image-rendering:pixelated;display:block" });
  img.hidden = true;
  void itemIcon(image).then((url) => { if (url) { img.src = url; img.hidden = false; } });
  pic.append(img);
  cell.el.append(pic);
  if (count > 1) {
    const label = new MirLabel({ parent: cell, notControl: true, autoSize: true, outline: false, foreColour: "#ffff00", text: String(count) });
    label.el.style.left = "auto";
    label.el.style.right = (cell.size[0] - countRight) + "px";
    label.el.style.top = "auto";
    label.el.style.bottom = "0px";
  }
}

function hoverItem(cell: MirControl, text: () => string): void {
  cell.el.addEventListener("pointerenter", (e) => showItemLabel(text(), e.clientX, e.clientY));
  cell.el.addEventListener("pointermove", (e) => showItemLabel(text(), e.clientX, e.clientY));
  cell.el.addEventListener("pointerleave", hideItemLabel);
}

// — QuestMessage —

interface MessageLayout {
  location: Point; size: Size; lineCount: number; displayProgress: boolean;
  up: Point; down: Point; bar: Point; posMinY: number; posMaxY: number;
}

/** QuestMessage: LineCount fixed MirLabels 15 px apart (titles bold 10pt + 5 px, the Prguse 919 marker in front of
 *  them), ScrollUpButton / ScrollDownButton / PositionBar, mouse wheel. */
class QuestMessageView {
  readonly box: MirControl;
  private readonly bar: MirButton;
  private readonly layout: MessageLayout;
  private readonly linkName: (kind: string, idx: number) => string;
  private lines: string[] = [];
  private topLine = 0;

  constructor(parent: MirControl, layout: MessageLayout, linkName: (kind: string, idx: number) => string) {
    this.layout = layout;
    this.linkName = linkName;
    new MirButton({ parent, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], location: layout.up, sound: SoundList.ButtonA,
      onClick: () => { if (this.topLine <= 0) return; this.topLine--; this.newText(false); this.updatePositionBar(); } });
    new MirButton({ parent, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], location: layout.down, sound: SoundList.ButtonA,
      onClick: () => { if (this.topLine + layout.lineCount >= this.lines.length) return; this.topLine++; this.newText(false); this.updatePositionBar(); } });
    this.bar = new MirButton({ parent, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: layout.bar, sound: 0, visible: false });
    this.box = new MirControl({ parent, location: layout.location, size: layout.size });
    this.box.el.addEventListener("wheel", (e) => { e.preventDefault(); this.wheel(e.deltaY < 0 ? 1 : e.deltaY > 0 ? -1 : 0); }, { passive: false });
    this.bar.el.addEventListener("pointerdown", (e) => this.dragBar(e));
  }

  /** QuestMessage_MouseWheel (count = Delta / MouseWheelScrollDelta: + = up). */
  private wheel(count: number): void {
    const n = this.lines.length, lc = this.layout.lineCount;
    if (this.topLine === 0 && count >= 0) return;
    if (this.topLine === n - 1 && count <= 0) return;
    if (n <= lc) return;
    this.topLine -= count;
    if (this.topLine < 0) this.topLine = 0;
    if (this.topLine + lc > n - 1) this.topLine = n - lc;
    this.newText(false);
    this.updatePositionBar();
  }

  /** PositionBar (Movable) + PositionBar_OnMoving: the bar slides between PosMinY and PosMaxY and picks TopLine. */
  private dragBar(e: PointerEvent): void {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    const { posMinY, posMaxY, lineCount } = this.layout;
    const startY = e.clientY, y0 = this.bar.location[1], scale = stageScale();
    const move = (m: PointerEvent) => {
      let y = Math.round(y0 + (m.clientY - startY) / scale);
      if (y >= posMaxY) y = posMaxY;
      if (y <= posMinY) y = posMinY;
      const interval = Math.max(1, Math.floor((posMaxY - posMinY) / Math.max(1, this.lines.length - lineCount)));
      this.topLine = Math.min(Math.floor((y - posMinY) / interval), Math.max(0, this.lines.length - lineCount));
      this.bar.location = [this.layout.bar[0], y];
      this.newText(false);
    };
    const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  /** UpdatePositionBar. */
  private updatePositionBar(): void {
    const { posMinY, posMaxY, lineCount } = this.layout;
    if (this.lines.length <= lineCount) { this.bar.visible = false; return; }
    this.bar.visible = true;
    const interval = Math.floor((posMaxY - posMinY) / (this.lines.length - lineCount));
    let y = posMinY + this.topLine * interval;
    if (y >= posMaxY) y = posMaxY;
    if (y <= posMinY) y = posMinY;
    this.bar.location = [this.layout.bar[0], y];
  }

  /** UpdateQuest + AdjustDescription: the quest V2 text lines (see QuestWindow.messageLines). */
  update(lines: string[]): void {
    this.lines = lines;
    this.newText(true);
  }

  /** Redraws the visible rows (link names follow their info). */
  refresh(): void { this.newText(false); }

  /** NewText (+ QuestMessage_AfterDraw's Prguse 919 title markers). */
  private newText(resetIndex: boolean): void {
    if (resetIndex) { this.topLine = 0; this.updatePositionBar(); }
    hideHint();
    this.box.el.replaceChildren();
    const lines = this.lines, lc = this.layout.lineCount;
    const bottom = lines.length > lc ? Math.min(lc + this.topLine, lines.length) : lines.length;
    let adjust = 0;
    for (let i = this.topLine; i < bottom; i++) {
      const line = lines[i]!;
      const title = i === 0 || line === TASK_TITLE || line === PROGRESS_TITLE || line === RETURN_TITLE || line === TIME_LIMIT_TITLE;
      const y = (i - this.topLine) * 15 + adjust;
      const label = new MirLabel({ parent: this.box, location: [title ? 15 : 0, y], size: [this.layout.size[0], 20], font: title ? FONT_TITLE : FONT_9, foreColour: i === 0 ? "#ffff00" : "#ffffff" });
      label.el.style.overflow = "hidden";
      this.renderMarkup(label.el, line);
      if (title) {
        new MirImage({ parent: this.box, library: Lib.Prguse, index: 919, location: [5, y + 5], notControl: true });
        adjust += 5;
      }
    }
  }

  /** NewText's match list: {text/Colour} (NewColour) and the MONSTER / NPC / ITEM links (NewLink: cyan, orange on hover). */
  private renderMarkup(div: HTMLElement, line: string): void {
    let at = 0;
    for (const m of line.matchAll(MARKUP)) {
      if (m.index! > at) div.append(line.slice(at, m.index));
      at = m.index! + m[0].length;
      if (m[1] !== undefined) { div.append(el("span", { style: `color:${m[2]};font:${FONT_9}` }, m[1])); continue; }
      const kind = (m[3] ?? m[6])!.toUpperCase(), idx = Number(m[4] ?? m[7]), name = m[5];
      const text = name || this.linkName(kind, idx) || `LINK_${idx}`;
      const link = el("span", { style: `color:#00ffff;font:${FONT_9}` }, text);
      const hint = `${kind.charAt(0)}${kind.slice(1).toLowerCase()}: ${text}`;
      link.addEventListener("pointerenter", (e) => { link.style.color = "#ffa500"; showHint(hint, e.clientX, e.clientY); });
      link.addEventListener("pointerleave", () => { link.style.color = "#00ffff"; hideHint(); });
      div.append(link);
    }
    if (at < line.length) div.append(line.slice(at));
  }
}

// — QuestRewards / QuestCell —

class QuestRewardsView {
  readonly box: MirControl;
  private quest = -1;
  private readonly owner: QuestWindow;
  private readonly quests: Quests;

  constructor(parent: MirControl, location: Point, size: Size, owner: QuestWindow, quests: Quests) {
    this.owner = owner;
    this.quests = quests;
    this.box = new MirControl({ parent, location, size });
  }

  /** UpdateRewards. */
  update(quest: number): void {
    this.quest = quest;
    this.render();
  }

  /** QuestReward_BeforeDraw + UpdateInterface: exp / gold / credit, the fixed item cells (no choice group), the select
   *  cells (every choice group's items; a click picks that group's reward). */
  private render(): void {
    const q = this.quests, def = q.def(this.quest);
    this.box.el.replaceChildren();
    hideItemLabel();
    if (!def) return;
    const list = q.rewards(def);
    const amount = (kind: number) => list.reduce((n, r) => n + (r.reward.Kind === kind && r.reward.ChoiceGroup === 0 ? Number(r.reward.Amount) : 0), 0);
    const exp = amount(QuestRewardKind.Exp), gold = amount(QuestRewardKind.Gold), credit = amount(QuestRewardKind.Credit);
    let goldX = 0, creditX = 0;
    if (exp > 0) {
      new MirImage({ parent: this.box, library: Lib.Prguse, index: 966, location: [10, 2], notControl: true });
      new MirLabel({ parent: this.box, location: [40, 0], size: [75, 20], text: String(exp) });
    } else { goldX = -90; creditX -= 90; }
    if (gold > 0) {
      new MirImage({ parent: this.box, library: Lib.Prguse, index: 965, location: [100 + goldX, 2], notControl: true });
      new MirLabel({ parent: this.box, location: [120 + goldX, 0], size: [75, 20], text: String(gold) });
    } else creditX -= 90;
    if (credit > 0) {
      new MirImage({ parent: this.box, library: Lib.Prguse, index: 2447, location: [190 + creditX, 2], notControl: true });
      new MirLabel({ parent: this.box, location: [210 + creditX, 0], size: [75, 20], text: String(credit) });
    }
    new MirImage({ parent: this.box, library: Lib.Title, index: 17, location: [20, 66], notControl: true });

    const items = list.filter((r) => r.reward.Kind === QuestRewardKind.Item);
    items.filter((r) => r.reward.ChoiceGroup === 0).slice(0, 5).forEach((r, i) => this.cell(r.reward.TargetId, Number(r.reward.Amount), [i * 45 + 15, 24], true, false));
    items.filter((r) => r.reward.ChoiceGroup > 0).slice(0, 5).forEach(({ reward, position }, i) => {
      const cell = this.cell(reward.TargetId, Number(reward.Amount), [i * 45 + 15, 89], false, q.pickOf(def.Quest, reward.ChoiceGroup) === position);
      cell.onClick(() => { q.pick(def.Quest, reward.ChoiceGroup, position); this.render(); });
    });
  }

  /** QuestCell (32 x 32): Prguse 989 under a fixed item, Prguse 979 under the selected one, the icon centred in 40 x 32. */
  private cell(itemIndex: number, count: number, location: Point, fixed: boolean, selected: boolean): MirControl {
    const cell = new MirControl({ parent: this.box, location, size: [32, 32] });
    if (fixed) new MirImage({ parent: cell, library: Lib.Prguse, index: 989, location: [0, -1], notControl: true });
    else if (selected) new MirImage({ parent: cell, library: Lib.Prguse, index: 979, location: [0, -5], notControl: true });
    const info = this.owner.itemInfo(itemIndex);
    if (info) {
      drawItem(cell, info.Image, count, [40, 32], 40);
      hoverItem(cell, () => this.owner.rewardText(info, count));
    }
    return cell;
  }
}

// — QuestWindow —

export class QuestWindow {
  private readonly stage: HTMLElement;
  private readonly quests: Quests;
  private readonly npc: NpcWindow;
  /** GameScene.NPCID / NPCDialog.Hide (src/game/npcSession.ts). */
  private readonly session: NpcSession;
  private readonly invWindow: InventoryWindow;
  private readonly items: ItemInfos;
  private readonly gameInfo: GameInfo;
  /** QuestListDialog's help button (HelpDialog.DisplayPage("Quests")): set by the game when it has a help window. */
  onHelp: (() => void) | null = null;

  // QuestListDialog
  private readonly list: MirWindow;
  private readonly listCount: MirLabel;
  private readonly listMessage: QuestMessageView;
  private readonly listRewards: QuestRewardsView;
  private readonly acceptButton: MirButton;
  private readonly finishButton: MirButton;
  private readonly rowBox: MirControl;
  /** QuestListDialog.CurrentNPCID / Quests / SelectedQuest / SelectedIndex / StartIndex (quest ids). */
  private currentNpcId = 0;
  private listQuests: number[] = [];
  private selected = -1;
  private selectedIndex = 0;
  private startIndex = 0;

  // QuestDetailDialog
  private readonly detail: MirWindow;
  private readonly detailMessage: QuestMessageView;
  private readonly detailRewards: QuestRewardsView;
  private detailQuestId = -1;

  // QuestDiaryDialog
  private readonly diary: MirWindow;
  private readonly diaryCount: MirLabel;
  private readonly diaryBody: MirControl;
  /** QuestDiaryDialog.ExpandedGroups (empty = all expanded). */
  private readonly expandedGroups = new Set<string>();
  private diarySelected = -1;

  // QuestTrackingDialog
  private readonly tracking: MirWindow;

  // Quest items (InventoryDialog quest tab)
  private readonly itemsWindow: MirWindow;
  private readonly itemsGrid: MirControl;

  constructor(stage: HTMLElement, quests: Quests, npc: NpcWindow, session: NpcSession, invWindow: InventoryWindow, items: ItemInfos, gameInfo: GameInfo) {
    this.stage = stage;
    this.quests = quests;
    this.npc = npc;
    this.session = session;
    this.invWindow = invWindow;
    this.items = items;
    this.gameInfo = gameInfo;
    const linkName = (kind: string, idx: number) => this.linkName(kind, idx);

    // QuestTrackingDialog ctor: Movable, Location (0, 100), Sort = false, no picture
    this.tracking = new MirWindow({ location: [0, 100], movable: true, sort: false });

    // QuestListDialog ctor: Prguse 950 at (NPCDialog.Size.Width + 47, 0)
    this.list = new MirWindow({ library: Lib.Prguse, index: 950, movable: true, sort: true, location: [0, 0], onHide: () => this.session.close() });
    void libSize(Lib.Prguse, 995).then(([w]) => { this.list.location = [w + 47, 0]; });
    new MirImage({ parent: this.list, library: Lib.Title, index: 14, location: [18, 9] });
    new MirButton({ parent: this.list, library: Lib.Prguse, index: 951, hoverIndex: 952, pressedIndex: 953, location: [291, 35], sound: SoundList.ButtonA, onClick: () => this.scrollRows(-1) });
    new MirButton({ parent: this.list, library: Lib.Prguse, index: 957, hoverIndex: 958, pressedIndex: 959, location: [291, 83], sound: SoundList.ButtonA, onClick: () => this.scrollRows(1) });
    this.rowBox = new MirControl({ parent: this.list, location: [0, 0], size: [0, 0] });
    this.acceptButton = new MirButton({ parent: this.list, library: Lib.Title, index: 270, hoverIndex: 271, pressedIndex: 272, location: [40, 436], sound: SoundList.ButtonA, onClick: () => this.acceptClick() });
    this.finishButton = new MirButton({ parent: this.list, library: Lib.Title, index: 273, hoverIndex: 274, pressedIndex: 275, location: [40, 436], sound: SoundList.ButtonA, visible: false, onClick: () => this.finishClick() });
    new MirButton({ parent: this.list, library: Lib.Title, index: 276, hoverIndex: 277, pressedIndex: 278, location: [205, 436], sound: SoundList.ButtonA, onClick: () => this.hideList() });
    this.listMessage = new QuestMessageView(this.list, { location: [10, 135], size: [280, 160], lineCount: 10, displayProgress: false, up: [292, 136], down: [292, 282], bar: [292, 149], posMinY: 149, posMaxY: 263 }, linkName);
    this.listRewards = new QuestRewardsView(this.list, [5, 307], [313, 130], this, quests);
    this.listCount = new MirLabel({ parent: this.list, autoSize: true, location: [210, 8] });
    closeButton(this.list, [289, 3], () => this.hideList());
    new MirButton({ parent: this.list, library: Lib.Prguse2, index: 257, hoverIndex: 258, pressedIndex: 259, location: [266, 3], sound: SoundList.ButtonA, onClick: () => this.onHelp?.() });

    // QuestDetailDialog ctor: Prguse 960 at (ScreenWidth / 2 + 20, 60)
    this.detail = new MirWindow({ library: Lib.Prguse, index: 960, movable: true, sort: true, location: [1024 / 2 + 20, 60] });
    new MirImage({ parent: this.detail, library: Lib.Title, index: 16, location: [18, 9] });
    this.detailMessage = new QuestMessageView(this.detail, { location: [10, 35], size: [280, 320], lineCount: 16, displayProgress: true, up: [293, 33], down: [293, 280], bar: [293, 48], posMinY: 46, posMaxY: 261 }, linkName);
    this.detailRewards = new QuestRewardsView(this.detail, [5, 307], [315, 130], this, quests);
    new MirButton({ parent: this.detail, library: Lib.Title, index: 616, hoverIndex: 617, pressedIndex: 618, location: [40, 436], sound: SoundList.ButtonA,
      onClick: () => { if (this.detailQuestId >= 0) quests.share(this.detailQuestId); } });
    // _pauseButton (Title 270, (120, 436)) is created hidden and never shown on the desktop: not ported
    new MirButton({ parent: this.detail, library: Lib.Title, index: 203, hoverIndex: 204, pressedIndex: 205, location: [200, 436], sound: SoundList.ButtonA, onClick: () => this.cancelClick() });
    closeButton(this.detail, [289, 3], () => this.detail.hide());

    // QuestDiaryDialog ctor: Prguse 961 at (ScreenWidth / 2 - 300 - 20, 60)
    this.diary = new MirWindow({ library: Lib.Prguse, index: 961, movable: true, sort: true, location: [1024 / 2 - 300 - 20, 60] });
    new MirImage({ parent: this.diary, library: Lib.Title, index: 15, location: [18, 9] });
    this.diaryCount = new MirLabel({ parent: this.diary, autoSize: true, location: [210, 7] });
    this.diaryBody = new MirControl({ parent: this.diary, location: [0, 0], size: [0, 0] });
    new MirButton({ parent: this.diary, library: Lib.Title, index: 193, hoverIndex: 194, pressedIndex: 195, location: [200, 436], sound: SoundList.ButtonA, onClick: () => this.diary.hide() });
    closeButton(this.diary, [289, 3], () => this.diary.hide());
    this.diary.el.addEventListener("contextmenu", (e) => e.preventDefault());

    // Quest items: InventoryDialog (Title 196) in its quest-tab state (ItemButton 737, ItemButton2 738, QuestButton 198)
    this.itemsWindow = new MirWindow({ library: Lib.Title, index: 196, movable: true, sort: true, location: [0, 0] });
    const toInventory = () => { this.itemsWindow.hide(); this.invWindow.toggle(true); };
    new MirButton({ parent: this.itemsWindow, library: Lib.Title, index: 737, location: [6, 7], size: [72, 23], sound: SoundList.ButtonA, onClick: toInventory });
    new MirButton({ parent: this.itemsWindow, library: Lib.Title, index: 738, location: [76, 7], size: [72, 23], sound: SoundList.ButtonA, onClick: toInventory });
    new MirButton({ parent: this.itemsWindow, library: Lib.Title, index: 198, location: [146, 7], size: [72, 23], sound: SoundList.ButtonA });
    closeButton(this.itemsWindow, [289, 3], () => this.itemsWindow.hide());
    this.itemsGrid = new MirControl({ parent: this.itemsWindow, location: [0, 0], size: [0, 0] });

    // NPCDialog.QuestButton -> QuestListDialog.Toggle; NPCDialog.Hide hides QuestListDialog
    npc.questsAvailable = (id) => quests.npcHasQuests(id);
    npc.onQuestButton = () => this.toggleList();
    const offClosed = session.closed.on(() => this.hideList());
    // S.QuestOffer (a share, an item or an area): the PlayerShareQuestRequest box
    const offOffer = quests.offered.on((o) => {
      const name = quests.name(o.quest);
      const text = o.sharedBy ? `${o.sharedBy} would like to share a quest with you. Do you accept?\n${name}` : `Do you accept the quest "${name}"?`;
      messageBox(this.stage, text, [
        { label: "Yes", onClick: () => this.quests.accept(o.quest, 0) },
        { label: "No", onClick: () => this.quests.dropOffer(o.quest) },
      ]);
    });

    const offChange = quests.onChange((c) => this.changed(c));
    gameInfo.onChange(() => this.refreshMessages()); // link names follow their info
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.Quests: Q without Alt (RequireAlt = 0) toggles QuestDiaryDialog
      if (matches(e, KeybindOptions.Quests)) this.toggleDiary();
      // KeybindOptions.Closeall hides QuestListDialog, QuestDetailDialog, QuestLogDialog (and InventoryDialog)
      else if (matches(e, KeybindOptions.Closeall)) { this.hideList(); this.detail.hide(); this.diary.hide(); this.itemsWindow.hide(); }
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => {
      offClosed();
      offOffer();
      offChange();
      for (const w of [this.list, this.detail, this.diary, this.tracking, this.itemsWindow]) w.dispose();
      itemLabelBox?.remove();
      itemLabelBox = null;
    });
  }

  /** Server packets (after Quests.handle has updated the state): nothing left here (offers come from the model). */
  handle(_p: ServerPacket): void {}

  /** MainDialog.QuestButton / KeybindOptions.Quests: QuestDiaryDialog.Toggle. */
  toggleDiary(show = !this.diary.visible): void {
    if (show) { this.renderDiary(); this.diary.show(); } else this.diary.hide();
  }

  /** InventoryDialog.QuestButton: the quest item grid. */
  toggleItems(show = !this.itemsWindow.visible): void {
    if (show) { this.renderItems(); this.itemsWindow.show(); } else this.itemsWindow.hide();
  }

  // — State changes (GameScene's refresh tail, plus the windows that read the other lists) —

  private changed(c: string): void {
    if (c === "items") { if (this.itemsWindow.visible) this.renderItems(); return; }
    if (c === "offers" || c === "history") return;
    this.renderTracking();
    if (this.list.visible) this.displayListInfo();
    this.npc.refreshQuestButton();
    if (this.diary.visible) this.renderDiary();
    if (this.detail.visible) {
      if (this.quests.run(this.detailQuestId)) this.showDetail(this.detailQuestId);
      else this.detail.hide();
    }
  }

  private refreshMessages(): void {
    if (this.list.visible && this.selected >= 0) this.listMessage.refresh();
    if (this.detail.visible) this.detailMessage.refresh();
    if (this.itemsWindow.visible) this.renderItems();
  }

  // — Quest V2 text (the adapter) —

  /** The turn-in NPC names (Finish endpoints, Start when none). */
  private finishName(def: QuestDefinitionInfo): string {
    const names = this.quests.endpointNames(def, QuestEndpointRole.Finish);
    return names.length ? names.map((n) => n.split("_")[0]).join(" or ") : "the quest giver";
  }

  /** QuestMessage lines: the name, the summary (the turn-in text when Ready), the current step's objectives (or the
   *  first step's for an offer), the time limit / time left, and the turn-in NPC. */
  private messageLines(quest: number): string[] {
    const q = this.quests, def = q.def(quest), run = q.run(quest);
    if (!def) return [q.name(quest), "Loading..."];
    const lines: string[] = [def.Name];
    const section = (title: string, body: string[]) => { if (body.length) lines.push(" ", title, ...body); };
    const text = (s: string) => s ? s.split(/\r?\n/) : [];
    if (run?.status === QuestRunStatus.Ready) {
      const last = def.Steps[def.Steps.length - 1];
      lines.push(...text(def.CompleteText || last?.ReadyText || def.Summary));
      section(RETURN_TITLE, [`Return to ${this.finishName(def)}.`]);
      return lines;
    }
    lines.push(...text(def.Summary));
    if (run?.status === QuestRunStatus.Failed) { section(PROGRESS_TITLE, ["{Failed/Red}"]); return lines; }
    const step = run ? q.currentStep(quest) : def.Steps[0];
    if (step) {
      if (run && step.Text) lines.push(...text(step.Text));
      const objs = run ? q.shownObjectives(def, step.Index) : q.objectives(def, step.Index).filter((o) => !o.Hidden);
      section(run ? PROGRESS_TITLE : TASK_TITLE, objs.map((o) => (run && q.done(o) ? `${q.objectiveText(o)} (done)` : q.objectiveText(o)) + (o.Optional ? " (optional)" : "")));
    }
    if (run) {
      const left = q.timeLeft(run);
      if (left !== null) section(TIME_LIMIT_TITLE, [printTimeSpanFromSeconds(left / 1000)]);
    } else if (def.TimeLimitSeconds > 0) section(TIME_LIMIT_TITLE, [printTimeSpanFromSeconds(def.TimeLimitSeconds)]);
    if (run && q.pendingBranches(quest).length) section(RETURN_TITLE, [`Speak to ${this.finishName(def)} to choose.`]);
    return lines;
  }

  // — QuestListDialog —

  private toggleList(): void {
    if (!this.list.visible) this.showList();
    else this.hideList();
  }

  /** QuestListDialog.Show: CurrentNPCID = GameScene.NPCID, Reset, DisplayInfo. */
  private showList(): void {
    this.currentNpcId = this.session.npcId;
    this.selected = -1;
    this.startIndex = 0;
    this.selectedIndex = 0;
    this.list.show();
    this.displayListInfo();
  }

  /** QuestListDialog.Hide (also hides the NPC dialog, via onHide). */
  private hideList(): void {
    this.list.hide();
  }

  /** The NPC's quests (S.QuestAvailability): finishing here first, then offered. */
  private npcQuests(): number[] {
    const e = this.quests.npcQuests(this.currentNpcId);
    if (!e) return [];
    const out = [...e.finishing];
    for (const id of e.offered) if (!out.includes(id)) out.push(id);
    return out;
  }

  /** QuestListDialog.DisplayInfo: no quest (or the NPC gone) hides the dialog. Keeps the selection by quest id. */
  private displayListInfo(): void {
    this.listQuests = this.npcQuests();
    if (this.listQuests.length < 1) { this.hideList(); return; }
    const at = this.listQuests.indexOf(this.selected);
    if (at < 0) { this.startIndex = 0; this.selectedIndex = 0; }
    else if (at < this.startIndex || at >= this.startIndex + 5) { this.startIndex = at; this.selectedIndex = 0; }
    else this.selectedIndex = at - this.startIndex;
    this.selected = at < 0 ? -1 : this.listQuests[at]!;
    this.refreshInterface();
  }

  /** upQuestButton / downQuestButton: move the selection inside the 5 rows, else scroll StartIndex. */
  private scrollRows(dir: -1 | 1): void {
    if (this.selected >= 0) {
      this.selectedIndex = this.findSelectedIndex();
      if (dir < 0) { if (this.selectedIndex > 0) this.selectedIndex--; else this.startIndex--; }
      else { if (this.selectedIndex < 4) this.selectedIndex++; else this.startIndex++; }
    }
    this.refreshInterface();
  }

  private rowQuests(): number[] { return this.listQuests.slice(this.startIndex, this.startIndex + 5); }

  /** FindSelectedIndex. */
  private findSelectedIndex(): number {
    const i = this.rowQuests().indexOf(this.selected);
    return i < 0 ? 0 : i;
  }

  /** RefreshInterface + UpdateRows + ReDisplayButtons (QuestRow at (9, 36 + i * 19)). */
  private refreshInterface(): void {
    this.listCount.text = `List: ${this.listQuests.length}`;
    const maxIndex = this.listQuests.length - 5;
    if (this.startIndex > maxIndex) this.startIndex = maxIndex;
    if (this.startIndex < 0) this.startIndex = 0;
    const rows = this.rowQuests();
    if (this.selectedIndex >= rows.length) this.selectedIndex = rows.length - 1;
    this.selected = rows[this.selected >= 0 ? Math.max(0, this.selectedIndex) : 0] ?? -1;
    this.showSelected();
    this.rowBox.el.replaceChildren();
    rows.forEach((q, i) => this.questRow(q, [9, 36 + i * 19]));
    this.reDisplayButtons();
  }

  /** The QuestIcon of a quest in the list: offered (by category), Ready, or in progress (white "?"). */
  private iconOf(quest: number): number {
    const run = this.quests.run(quest), cat = this.quests.def(quest)?.Category ?? 1;
    return questIconFor(cat, !run ? "offer" : run.status === QuestRunStatus.Ready ? "ready" : "active");
  }

  /** QuestRow (200 x 17): selected bar Prguse 956 at (25, 0), the quest icon Prguse 961 + Icon (+ 15 above 3) at (3, 0),
   *  "Lv n" at (20, 0), the name at (60, 0). */
  private questRow(quest: number, location: Point): void {
    const def = this.quests.def(quest);
    const row = new MirControl({ parent: this.rowBox, location, size: [200, 17] });
    if (quest === this.selected) new MirImage({ parent: row, library: Lib.Prguse, index: 956, location: [25, 0], notControl: true });
    const icon = this.iconOf(quest);
    new MirImage({ parent: row, library: Lib.Prguse, index: 961 + icon + (icon > 3 ? 15 : 0), location: [3, 0], notControl: true });
    new MirLabel({ parent: row, location: [20, 0], size: [178, 17], vCenter: true, notControl: true, text: def && def.LevelHint > 0 ? `Lv ${def.LevelHint}` : "" });
    new MirLabel({ parent: row, location: [60, 0], size: [140, 17], vCenter: true, notControl: true, text: this.quests.name(quest) });
    row.onClick(() => {
      uiSound(SoundList.ButtonA);
      if (quest === this.selected) return;
      this.selected = quest;
      this.selectedIndex = this.findSelectedIndex();
      this.showSelected();
      this.rowBox.el.replaceChildren();
      this.rowQuests().forEach((r, i) => this.questRow(r, [9, 36 + i * 19]));
      this.reDisplayButtons();
    });
  }

  /** Reward.UpdateRewards + Message.UpdateQuest for SelectedQuest. */
  private showSelected(): void {
    if (this.selected < 0) return;
    if (!this.quests.def(this.selected)) this.quests.requestDefinition(this.selected);
    this.listRewards.update(this.selected);
    this.listMessage.update(this.messageLines(this.selected));
  }

  /** ReDisplayButtons. */
  private reDisplayButtons(): void {
    const run = this.selected >= 0 ? this.quests.run(this.selected) : undefined;
    this.acceptButton.visible = this.selected >= 0 && !run && this.quests.activeCount < MAX_CONCURRENT_QUESTS;
    this.finishButton.visible = run?.status === QuestRunStatus.Ready;
  }

  /** QuestListDialog accept button: C.QuestAccept at this NPC. */
  private acceptClick(): void {
    if (this.selected < 0 || this.quests.run(this.selected)) return;
    this.quests.accept(this.selected, this.currentNpcId);
  }

  /** QuestListDialog finish button: each choice group needs a pick (YouMustSelectRewardItem), then C.QuestTurnIn. */
  private finishClick(): void {
    const def = this.selected >= 0 ? this.quests.def(this.selected) : undefined;
    if (!def || this.quests.run(def.Quest)?.status !== QuestRunStatus.Ready) return;
    const picks = this.quests.picksFor(def);
    if (!picks) { messageBox(this.stage, "You must select a reward item."); return; }
    this.quests.turnIn(def.Quest, this.currentNpcId, picks);
  }

  // — QuestDetailDialog —

  /** QuestDetailDialog.DisplayQuestDetails. */
  private showDetail(quest: number): void {
    this.detailQuestId = quest;
    this.detailRewards.update(quest);
    this.detailMessage.update(this.messageLines(quest));
    this.detail.show();
  }

  /** QuestDetailDialog cancel button: AskCancelQuest, then C.QuestAbandon and hide. */
  private cancelClick(): void {
    const id = this.detailQuestId;
    if (id < 0) return;
    messageBox(this.stage, "Are you sure you want to cancel this quest?", [
      { label: "Yes", onClick: () => { this.quests.abandon(id); this.detail.hide(); } },
      { label: "No" },
    ]);
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

  // — QuestRewards helpers —

  /** A reward item's info (asked once when missing; the server sends it with the definition). */
  itemInfo(index: number): ItemInfo | undefined {
    const info = this.items.get(index);
    if (!info) this.gameInfo.requestItem(index);
    return info;
  }

  /** QuestCell.OnMouseEnter: the item label of a UserItem made from the ItemInfo (MaxDura = CurrentDura = Durability). */
  rewardText(info: ItemInfo, count: number): string {
    if (!this.items.get(info.Index)) return `${info.Name}${count > 1 ? ` x${count}` : ""}`;
    const item: UserItem = {
      UniqueID: 0n, ItemIndex: info.Index, CurrentDura: info.Durability, MaxDura: info.Durability, Count: Math.max(1, count), SoulBoundId: -1,
      Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [], RefinedValue: 0, RefineAdded: 0,
      RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false, Sealed: null, GMMade: false,
    };
    return this.invWindow.describe(item);
  }

  // — QuestDiaryDialog —

  /** QuestDiaryDialog.DisplayQuests: QuestGroupQuestItem per group (GroupName, else the category) at (15, nextY). */
  private renderDiary(): void {
    const q = this.quests;
    const runs = q.runs();
    this.diaryCount.text = `List: ${q.activeCount}/${MAX_CONCURRENT_QUESTS}`;
    const groups = new Map<string, number[]>();
    for (const r of runs) {
      const def = q.def(r.quest);
      const g = def ? def.GroupName || QUEST_CATEGORY_LABEL[def.Category] || "" : "";
      const list = groups.get(g);
      if (list) list.push(r.quest); else groups.set(g, [r.quest]);
    }
    const allExpanded = this.expandedGroups.size === 0;
    this.diaryBody.el.replaceChildren();
    let nextY = 40;
    for (const [group, list] of groups) {
      const expanded = allExpanded || this.expandedGroups.has(group);
      nextY += this.groupItem(group, list, expanded, groups, nextY);
    }
  }

  /** QuestGroupQuestItem: expand button Prguse 917 / 918, the LimeGreen group label, the quest rows 15 px apart.
   *  Returns SizeY. */
  private groupItem(group: string, list: number[], expanded: boolean, groups: Map<string, number[]>, y: number): number {
    const sizeY = 15 + (expanded ? list.length * 15 : 0);
    const item = new MirControl({ parent: this.diaryBody, location: [15, y], size: [280, sizeY] });
    new MirButton({ parent: item, library: Lib.Prguse, index: expanded ? 917 : 918, size: [16, 14], location: [0, 0], sound: SoundList.ButtonA, onClick: () => this.toggleGroup(group, groups) });
    new MirLabel({ parent: item, autoSize: true, location: [18, 0], foreColour: LIME_GREEN, text: group });
    if (expanded) list.forEach((quest, i) => this.singleQuestItem(item, quest, [18, 15 * (i + 1)]));
    return sizeY;
  }

  /** QuestGroupQuestItem.ChangeExpand + QuestDiaryDialog's ExpandedChanged (ExpandedGroups holds the expanded ones). */
  private toggleGroup(group: string, groups: Map<string, number[]>): void {
    if (this.expandedGroups.size === 0) for (const g of groups.keys()) this.expandedGroups.add(g);
    if (this.expandedGroups.has(group)) this.expandedGroups.delete(group);
    else this.expandedGroups.add(group);
    // every group collapsed reads as "all expanded" (ExpandedGroups.Count <= 0): the desktop has the same rule
    this.renderDiary();
  }

  /** QuestSingleQuestItem (280 x 15): selected bar Prguse 956 at (-10, 0), "{Lv,-4} name" at (0, 0), the state at
   *  (185, 0), grey when 10+ levels below the user; the track mark Prguse 997 at (-15, 0). */
  private singleQuestItem(parent: MirControl, quest: number, location: Point): void {
    const q = this.quests, def = q.def(quest), run = q.run(quest);
    const row = new MirControl({ parent, location, size: [280, 15] });
    if (this.diarySelected === quest) new MirImage({ parent: row, library: Lib.Prguse, index: 956, location: [-10, 0], notControl: true });
    const level = def?.LevelHint ?? 0;
    const failed = run?.status === QuestRunStatus.Failed;
    const colour = failed ? "#ff4040" : level > 0 && q.level - level > 10 ? "#808080" : "#ffffff";
    const tracked = q.isTracked(quest);
    const name = new MirLabel({ parent: row, autoSize: true, location: [0, 0], foreColour: colour, text: `${`Lv${level}`.padEnd(4)} ${q.name(quest)}` });
    new MirLabel({ parent: row, autoSize: true, location: [185, 0], foreColour: colour,
      text: failed ? "(Failed)" : run?.status === QuestRunStatus.Ready ? "(Complete)" : "(In Progress)" });
    if (tracked) new MirImage({ parent: row, library: Lib.Prguse, index: 997, location: [-15, 0], notControl: true });
    // _questLabel.Click: left = DisplayQuestDetails, right = track / untrack (QuestTrackingDialog.AddQuest / RemoveQuest)
    name.el.addEventListener("mouseup", (ev) => {
      if (ev.button !== 0 && ev.button !== 2) return;
      ev.stopPropagation();
      uiSound(SoundList.ButtonA);
      if (ev.button === 0) this.showDetail(quest);
      else if (tracked) q.untrackQuest(quest);
      else if (q.trackedIds.length < MAX_TRACKED_QUESTS) q.trackQuest(quest);
      this.diarySelected = quest; // OnSelectedQuestChanged
      this.renderDiary();
    });
  }

  // — QuestTrackingDialog —

  /** QuestTrackingDialog.DisplayQuests: name LimeGreen at (5, 20 + y), the current step's objective lines at (25, 20 + y),
   *  15 px per line, 30 px between quests; hidden when nothing is tracked. */
  private renderTracking(): void {
    const q = this.quests;
    const tracked = q.trackedRuns();
    this.tracking.el.replaceChildren();
    if (tracked.length < 1) { this.tracking.hide(); return; }
    let y = 0;
    for (let i = 0; i < tracked.length; i++) {
      const run = tracked[i]!, def = q.def(run.quest)!;
      new MirLabel({ parent: this.tracking, autoSize: true, location: [5, 20 + y], foreColour: LIME_GREEN, text: def.Name });
      const step = q.currentStep(run.quest);
      const tasks = run.status === QuestRunStatus.Ready ? [`Return to ${this.finishName(def)}`]
        : run.status === QuestRunStatus.Failed ? ["Failed"]
        : step ? q.shownObjectives(def, step.Index).map((o) => q.objectiveText(o)) : [];
      for (const task of tasks) {
        y += 15;
        new MirLabel({ parent: this.tracking, autoSize: true, location: [25, 20 + y], text: task });
      }
      if (i >= 5) break;
      y += 30;
    }
    if (!this.tracking.visible) this.tracking.show();
  }

  // — Quest items (InventoryDialog.QuestGrid: 8 x 5 MirItemCells at (x * 36 + 9 + x, y * 32 + 37 + y)) —

  private renderItems(): void {
    this.itemsGrid.el.replaceChildren();
    hideItemLabel();
    for (let y = 0; y < QUEST_GRID_H; y++) {
      for (let x = 0; x < QUEST_GRID_W; x++) {
        const item = this.quests.questInventory[QUEST_GRID_W * y + x] ?? null;
        if (!item) continue;
        const info = this.items.get(item.ItemIndex);
        if (!info) { this.gameInfo.requestItem(item.ItemIndex); continue; }
        const cell = new MirControl({ parent: this.itemsGrid, location: [x * 36 + 9 + x, y * 32 + 37 + y], size: [36, 32] });
        drawItem(cell, info.Image, item.Count, [36, 32], 36);
        hoverItem(cell, () => this.invWindow.describe(item));
      }
    }
  }

  /** For game.ts: the NPC's above-head QuestIcon (the server's, S.QuestAvailability), or null. */
  npcQuestIcon(npcObjectId: number): number | null {
    return this.quests.npcQuestIcon(npcObjectId);
  }
}
