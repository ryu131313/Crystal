// Quest V2 pieces shared by the new UI's quest window (windows/quests.ts), the NPC dialogue's Quests tab (windows/npc.ts)
// and the HUD tracker / toasts: the objective line with its bar (text built by src/game/quests.ts), the reward block
// (EXP / gold / credit chips, item cells with tooltips, choice groups whose pick is kept in the Quests model and sent with
// C.QuestTurnIn) and the row marker. Plain DOM built with h(); the CSS is scoped under .hudm .qparts.

import type { ItemInfo, QuestDefinitionInfo, QuestDefObjective, UserItem } from "../../../protocol/server.ts";
import { QuestRewardKind, QuestRewardScope, QuestRunStatus } from "../../../protocol/enums.generated.ts";
import { questIconFor, questMiniMapMarker, type Quests } from "../../../game/quests.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import type { ModernUiHost } from "../windowRegistry.ts";
import { fmt, h, injectCss } from "../core/index.ts";

export const QUEST_PARTS_CSS = `
.hudm .qparts .qobj { padding: 5px 8px; margin-bottom: 4px; border-radius: calc(var(--rad) * .7); background: color-mix(in srgb, var(--frl) 6%, transparent); font-size: 12px; }
.hudm .qparts .qobj .ql { display: flex; gap: 6px; align-items: baseline; overflow-wrap: anywhere; }
.hudm .qparts .qobj .ql span { flex: 1; min-width: 0; }
.hudm .qparts .qobj .ql em { font-style: normal; font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--dim); }
.hudm .qparts .qobj.ok .ql span { color: #5fe07a; }
.hudm .qparts .qobj.ok .ql span::before { content: "\\2713  "; }
.hudm .qparts .qobj .qb { margin-top: 4px; height: 4px; border-radius: 2px; background: color-mix(in srgb, var(--fr) 60%, transparent); overflow: hidden; }
.hudm .qparts .qobj .qb i { display: block; height: 100%; width: 100%; transform-origin: 0 50%; background: var(--ac); transition: transform .25s; }
.hudm .qparts .qobj.ok .qb i { background: #5fe07a; }
.hudm .qparts .rew { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.hudm .qparts .chip { height: 24px; padding: 0 10px; display: inline-flex; align-items: center; gap: 6px; font-size: 12px; border-radius: 12px;
  background: color-mix(in srgb, var(--frl) 8%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .qparts .chip span { color: var(--dim); font-size: 10px; font-weight: 700; letter-spacing: .08em; }
.hudm .qparts .chip b { color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .qparts .rcell { position: relative; width: 36px; height: 36px; flex: none; border-radius: calc(var(--rad) * .7); background: center no-repeat color-mix(in srgb, #000 35%, var(--pb));
  border: 1px solid var(--fr); image-rendering: pixelated; }
.hudm .qparts .rcell em { position: absolute; right: 2px; bottom: 0; font-style: normal; font-size: 10px; color: #ff0; text-shadow: 0 1px 1px #000; }
.hudm .qparts .rcell.pick { cursor: pointer; }
.hudm .qparts .rcell.pick:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .qparts .rcell.on { border-color: var(--ac); box-shadow: inset 0 0 0 1px var(--ac); }
.hudm .qparts .rlbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin: 8px 0 4px; }
.hudm .qparts .rnote { font-size: 11px; color: var(--dim); }
`;

/** A UserItem standing for an ItemInfo (QuestCell.OnMouseEnter: MaxDura = CurrentDura = Durability). */
export function displayItem(info: ItemInfo, count: number): UserItem {
  return {
    UniqueID: 0n, ItemIndex: info.Index, CurrentDura: info.Durability, MaxDura: info.Durability, Count: Math.max(1, count), SoulBoundId: -1,
    Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [], RefinedValue: 0, RefineAdded: 0,
    RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false, Sealed: null, GMMade: false,
  };
}

/** The "!" / "?" marker of a quest: offered (by category), Ready (by category), in progress (white), failed (red x). */
export function questMarker(q: Quests, quest: number): { text: string; colour: string } | null {
  const def = q.def(quest), run = q.run(quest), cat = def?.Category ?? 1;
  if (!run) return questMiniMapMarker(questIconFor(cat, "offer"));
  if (run.status === QuestRunStatus.Failed) return { text: "×", colour: "#ff5a4a" };
  return questMiniMapMarker(questIconFor(cat, run.status === QuestRunStatus.Ready ? "ready" : "active"));
}

/** One objective: its text (built by the model) and, for counted ones, a bar. */
export function objectiveRow(q: Quests, o: QuestDefObjective, live = true): HTMLElement {
  injectCss("quest-parts", QUEST_PARTS_CSS);
  const done = live && q.done(o);
  const line = h("div", "ql", h("span", "", live ? q.objectiveText(o) : textWithoutCount(q, o)));
  if (o.Optional) line.append(h("em", "", "Optional"));
  const row = h("div", "qobj" + (done ? " ok" : ""), line);
  if (live && !q.binary(o) && Math.max(1, o.Count) > 1) {
    const fill = h("i");
    fill.style.transform = `scaleX(${q.fraction(o).toFixed(3)})`;
    row.append(h("div", "qb", fill));
  }
  return row;
}

/** A future / past step's objective without a live count ("Kill Hen: 0/5" -> "Kill Hen x5"). */
function textWithoutCount(q: Quests, o: QuestDefObjective): string {
  if (o.Text) return o.Text;
  const t = q.objectiveText(o);
  const m = /^(.*?):\s*\d+\/(\d+)$/.exec(t);
  return m ? (m[2] === "1" ? m[1]! : `${m[1]} x${m[2]}`) : t;
}

export interface RewardOpts {
  /** The reward scope (default Quest = turn-in). */
  scope?: number;
  scopeId?: number;
  /** Choice groups are clickable (the pick is kept in the model). */
  pick?: boolean;
  /** The title above the block ("" = none). */
  title?: string;
}

/** The rewards of a scope: chips for EXP / gold / credit and the other kinds, item cells (tooltips), and per choice group
 *  "Choose one" cells (clickable with `pick`). Amounts already include the server rates and the class / gender filter. */
export function rewardsBlock(ui: ModernUiHost, q: Quests, def: QuestDefinitionInfo, o: RewardOpts = {}): HTMLElement | null {
  injectCss("quest-parts", QUEST_PARTS_CSS);
  const list = q.rewards(def, o.scope ?? QuestRewardScope.Quest, o.scopeId ?? 0);
  if (!list.length) return null;
  const box = h("div", "qparts");
  if (o.title !== "") box.append(h("div", "rlbl", o.title ?? "Rewards"));
  const always = h("div", "rew");
  const groups = new Map<number, HTMLElement>();
  for (const { reward, position } of list) {
    let into: HTMLElement = always;
    if (reward.ChoiceGroup > 0) {
      let g = groups.get(reward.ChoiceGroup);
      if (!g) { g = h("div", "rew"); groups.set(reward.ChoiceGroup, g); }
      into = g;
    }
    if (reward.Kind === QuestRewardKind.Item) {
      into.append(rewardCell(ui, q, def.Quest, reward.TargetId, Number(reward.Amount),
        reward.ChoiceGroup > 0 && o.pick ? { group: reward.ChoiceGroup, position } : null, reward.ChoiceGroup > 0 && q.pickOf(def.Quest, reward.ChoiceGroup) === position));
    } else if (reward.Kind === QuestRewardKind.Exp || reward.Kind === QuestRewardKind.Gold || reward.Kind === QuestRewardKind.Credit) {
      const label = reward.Kind === QuestRewardKind.Exp ? "EXP" : reward.Kind === QuestRewardKind.Gold ? "GOLD" : "CREDIT";
      into.append(h("div", "chip", h("span", "", label), h("b", "", fmt(reward.Amount))));
    } else into.append(h("div", "chip", h("b", "", q.rewardText(reward))));
  }
  if (always.childNodes.length) box.append(always);
  for (const g of groups.values()) box.append(h("div", "rlbl", o.pick ? "Choose one" : "Choose one (at turn-in)"), g);
  return box;
}

function rewardCell(ui: ModernUiHost, q: Quests, quest: number, itemIndex: number, count: number, pick: { group: number; position: number } | null, on: boolean): HTMLElement {
  const gi = ui.ctx.models.gameInfo;
  const info = gi.item(itemIndex);
  if (!info) gi.requestItem(itemIndex);
  const cell = h("div", "rcell" + (pick ? " pick" : "") + (on ? " on" : ""), count > 1 ? h("em", "", String(count)) : null);
  if (info) {
    void libPicture(Lib.Items, info.Image).then((p) => { if (p) cell.style.backgroundImage = `url(${p.url})`; });
    cell.setAttribute("aria-label", itemFriendlyName(info) || info.Name);
  }
  cell.addEventListener("pointerenter", () => {
    const i = gi.item(itemIndex);
    if (i) ui.tooltip.show(cell, displayItem(i, count), ui.labelContext(), { info: i });
  });
  cell.addEventListener("pointerleave", () => ui.tooltip.hide());
  if (pick) cell.addEventListener("click", () => {
    q.pick(quest, pick.group, pick.position);
    for (const c of Array.from(cell.parentElement?.querySelectorAll<HTMLElement>(".rcell.pick") ?? [])) c.classList.toggle("on", c === cell);
  });
  return cell;
}
