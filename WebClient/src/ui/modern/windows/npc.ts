// Ported from play/hudNpc.ts: the new UI's NPC dialogue over the real NPC session (src/game/npcSession.ts). Desktop
// counterpart: NPCDialog + BigButtonDialog + the QuestListDialog the NPC's quest button opens
// (Client/MirScenes/Dialogs/NPCDialogs.cs, QuestDialogs.cs); the classic port is src/ui/npcWindow.ts (its rules are kept).
//
// Changes from the mock:
//  - opens only on the server's answer (NpcSession.page = S.NPCResponse with something to show) and closes with the
//    session (NpcSession.closed: an empty page, @Exit, a refine that starts, ...). Closing the window is NPCDialog.Hide
//    (NpcSession.close(): no packet, as the classic). Talking to an NPC in the world is NpcSession.talk (the 5 s gate);
//    a link, option card or service chip is NpcSession.call(key) (C.CallNPC "[key]"; "@Exit" closes);
//  - the page is S.NPCResponse parsed by the classic parser (src/game/npcMarkup.ts): <text/@key> links, <<text/@key>> big
//    buttons (numbered option cards, keys 1-9), {text/Colour} colours (Color.FromName = CSS colour names), (text/url) web
//    links (http(s) only, like the classic), [ITEM|MONSTER|NPC:n(|name)] and <$ITEM|MONSTER|NPC:n> info links (the item
//    tooltip; a monster / NPC info card like NPCDialog.ShowTooltip). Script variables (<$USERNAME>, ...) are replaced
//    by the server. As in the mock, a row holding only page links becomes option cards too (keys 1-9);
//  - the portrait is the NPC's own library (npcLibPath(image) of the map object, FrameSet from the library or
//    DefaultNPC): the Standing loop in the NPC's direction, and the Harvest frames as a gesture on each new page;
//  - the typewriter runs on the UI ticker while a page types (no 16 ms interval); click or Space finishes it; a redraw
//    of the same page (Log, tabs, gold) neither replays nor logs it again (the page sequence lives in NpcDialogState);
//  - the conversation log keeps what the NPC said (each page's first lines) and the option the player chose, logged
//    when the server answered it (a refused call logs nothing);
//  - Quests tab: Quests.availableQuests(npcId) only (QuestListDialog for this NPC: accept with C.AcceptQuest,
//    hand in with C.FinishQuest and the chosen reward, abandon with C.AbandonQuest after a question). Buttons lock
//    until the server's S.ChangeQuest; nothing changes locally. The server needs the NPC in range, which holds while
//    the dialogue is open;
//  - keys only through the window key() hook while the dialogue is the front window: Space finishes the typing, 1-9
//    pick an option only while the pointer is over the window (so the belt keys keep working); Esc / Shift+Esc are the
//    window manager's (close = NPCDialog.Hide).
// HIDDEN (Crystal has no data for them): the NPC title line (only the name and the place parts of the NPC's name are
// shown), the mock's fixed service list (the chips are derived from the page's own service links, so each is a key
// the server accepts on this page), the Back stack (the server refuses a page key that is not on the current page; a
// "Main" button calls @Main, which it always accepts), the teleport warp and mock prices (teleports are page links;
// the script's MOVE / TAKEGOLD run on the server).

import type { ClientQuestInfo, ItemInfo, UserItem } from "../../../protocol/server.ts";
import { MirAction, MirGender, QuestTypeName, RequiredGender, Stat } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import { isTextTarget } from "../../../game/input.ts";
import { npcLibPath, monsterLibPath, LibPath } from "../../../game/libraries.ts";
import { frameOffset, type Frame } from "../../../game/frames.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { parseLine, type NpcLinkKind, type NpcPage, type NpcSegment } from "../../../game/npcMarkup.ts";
import {
  MAX_CONCURRENT_QUESTS, entryIcon, printTimeSpanFromSeconds, questMiniMapMarker, sameFinishNPC, type QuestEntry,
} from "../../../game/quests.ts";
import { Lib, libPicture, type LibPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, fmt, h, injectCss, later, npcName, reducedMotion, setStyle, setText, svg, tabs, toLocal, type Btn,
  type IconName,
} from "../core/index.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const NPC_WINDOW = "npc";

/** Characters a second the page types out at (the mock: 3 every 16 ms). */
const TYPE_RATE = 180;
/** Log entries kept per conversation. */
const LOG_MAX = 30;

/** ClientTextKeys (English defaults, Client/Localization/English.json), as the classic windows. */
const T = {
  MonsterInfoLoading: "Monster info is being loaded...", NPCInfoLoading: "NPC info is being loaded...",
  MonsterIndexTitle: (i: number) => `Monster Index: ${i}`, NPCIndexTitle: (i: number) => `NPC Index: ${i}`,
  YouMustSelectRewardItem: "You must select a reward item.",
  AskCancelQuest: (n: string) => `Are you sure you want to cancel the quest "${n}"?`,
};

/** The NPC script's service page keys (NPCScript.cs) a page link may carry: [chip icon, short label]. */
const SERVICES: Readonly<Record<string, [IconName, string]>> = {
  BUY: ["shop", "Buy"], BUYNEW: ["shop", "Buy new"], BUYUSED: ["shop", "Buy used"], BUYSELL: ["shop", "Buy / sell"],
  BUYSELLNEW: ["shop", "Buy / sell"], PEARLBUY: ["gem", "Pearl shop"], SELL: ["coin", "Sell"], BUYBACK: ["back", "Buy back"],
  REPAIR: ["wrench", "Repair"], SREPAIR: ["wrench", "Special repair"], STORAGE: ["bank", "Storage"], CONSIGN: ["tag", "Consign"],
  MARKET: ["tag", "Trust merchant"], CRAFT: ["craft", "Craft"], REFINE: ["spark", "Refine"], REFINECHECK: ["spark", "Check refine"],
  REFINECOLLECT: ["spark", "Collect refine"], REPLACEWEDDINGRING: ["ring", "Wedding ring"], AWAKENING: ["star", "Awakening"],
  DISASSEMBLE: ["swap", "Disassemble"], DOWNGRADE: ["chevronDown", "Downgrade"], RESET: ["refresh", "Reset stats"],
  SENDPARCEL: ["mail", "Send parcel"], COLLECTPARCEL: ["inbox", "Collect parcel"], CREATEGUILD: ["guild", "Create guild"],
  REQUESTWAR: ["flag", "Guild war"], GUILDTERRITORY: ["flag", "Territory"], CREATEHERO: ["hero", "Create hero"],
  MANAGEHERO: ["hero", "Manage hero"],
};

/** A link the page offers as an option card / chip ("@key", the text it shows). */
interface Choice { readonly text: string; readonly key: string }

/** The plain text of a parsed row (info links by their given name or index). */
function rowText(row: readonly NpcSegment[]): string {
  let s = "";
  for (const seg of row) s += seg.kind === "info" ? seg.name ?? `${seg.link} ${seg.index}` : seg.text;
  return s;
}

/** A row that holds only page links (and blanks) gives its links as option cards (the mock's bottom buttons). */
function linkOnly(row: readonly NpcSegment[]): Choice[] | null {
  const out: Choice[] = [];
  for (const seg of row) {
    if (seg.kind === "link") out.push({ text: seg.text, key: seg.key });
    else if (seg.kind !== "text" || seg.text.trim() !== "") return null;
  }
  return out.length ? out : null;
}

/** A UserItem standing for an ItemInfo (QuestCell / NPCDialog item links: MaxDura = CurrentDura = Durability). */
function displayItem(info: ItemInfo | undefined, index: number, count = 1): UserItem {
  return {
    UniqueID: 0n, ItemIndex: index, CurrentDura: info?.Durability ?? 0, MaxDura: info?.Durability ?? 0, Count: Math.max(1, count),
    SoulBoundId: -1, Identified: false, Cursed: false, Slots: [], GemCount: 0, AddedStats: {}, AwakeType: 0, AwakeLevels: [],
    RefinedValue: 0, RefineAdded: 0, RefineSuccessChance: 0, WeddingRing: -1, ExpiryDate: null, Rental: null, IsShopItem: false,
    Sealed: null, GMMade: false,
  };
}

// — The dialogue state outside the window (built at UI start by the watch hook, so pages before the first open count) —

export interface NpcLogLine { readonly who: string; readonly text: string }

/** NpcSession.callPage: NPCTime = send + 5 s (cleared by S.NPCResponse, which also clears pendingChoice). */
const NPC_CALL_GATE_MS = 5000;

/** The page sequence and the conversation log of the current NPC session. One per UI runtime (npcDialogState). */
export class NpcDialogState {
  /** A new page arrived or the log changed. */
  readonly changed = new Emitter<void>();
  private _seq = 0;
  private _log: NpcLogLine[] = [];
  private _logVersion = 0;
  private pendingChoice = "";
  /** When pendingChoice was taken (performance.now(), the npcSession clock). */
  private pendingAt = 0;

  constructor(private readonly host: ModernUiHost) {
    const s = host.ctx.models.npcSession, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(s.opened.on(() => { this._log = []; this.pendingChoice = ""; this._logVersion++; }));
    on(s.page.on((page) => this.newPage(page)));
    on(s.closed.on(() => { this.pendingChoice = ""; }));
  }

  /** Bumped on every S.NPCResponse page (a redraw of the same page keeps it). */
  get seq(): number { return this._seq; }
  get log(): readonly NpcLogLine[] { return this._log; }
  get logVersion(): number { return this._logVersion; }

  /** The player picked an option: logged when the server answers with the next page. A second pick inside the
   *  session's 5 s CallNPC gate was dropped, so the first one stays (the gate opens again after 5 s or on an answer). */
  chose(text: string): void {
    const t = performance.now();
    if (this.pendingChoice && t <= this.pendingAt + NPC_CALL_GATE_MS) return;
    this.pendingChoice = text.trim();
    this.pendingAt = t;
  }

  private newPage(page: NpcPage): void {
    this._seq++;
    if (this.pendingChoice) this.push("You", this.pendingChoice);
    this.pendingChoice = "";
    const said: string[] = [];
    for (const row of page.rows) {
      if (linkOnly(row)) continue;
      const t = rowText(row).trim();
      if (t) said.push(t);
      if (said.length >= 2) break;
    }
    if (said.length) this.push(npcName(this.host.ctx.models.npcSession.npcName || "NPC")[0], said.join(" "));
    this.changed.emit();
  }

  private push(who: string, text: string): void {
    this._log.push({ who, text });
    if (this._log.length > LOG_MAX) this._log.splice(0, this._log.length - LOG_MAX);
    this._logVersion++;
  }
}

/** The runtime's NPC dialogue state (built on first use). */
export function npcDialogState(host: ModernUiHost): NpcDialogState {
  return host.rt.service("npcDialogState", () => new NpcDialogState(host));
}

// — The window —

const NPC_CSS = `
.hudm .npwin { width: 780px; }
.hudm .npwin .panel { height: 470px; display: flex; flex-direction: column; overflow: hidden; position: relative; }
.hudm .npwin .nbody { flex: 1; min-height: 0; display: flex; }
.hudm .npwin .side { width: 230px; flex: none; display: flex; flex-direction: column; align-items: center; padding: 10px; gap: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 80% 55% at 50% 40%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .npwin .stage { position: relative; width: 210px; height: 220px; overflow: hidden; flex: none; }
.hudm .npwin .stage::before { content: ""; position: absolute; left: 50%; bottom: 18px; width: 150px; height: 30px; translate: -50% 0; border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%); }
.hudm .npwin .spr { position: absolute; left: calc(50% + 14px); bottom: 26px; transform-origin: 50% 100%; }
.hudm .npwin .spr img { position: absolute; image-rendering: pixelated; }
.hudm .npwin .mark { position: absolute; left: 50%; top: 4px; translate: -50% 0; font-size: 30px; font-weight: 900; text-shadow: 0 0 10px currentColor, 0 2px 3px #000; animation: hudm-npmark 1.4s ease-in-out infinite; }
@keyframes hudm-npmark { 50% { translate: -50% -5px; } }
.hudm .npwin .who { text-align: center; } .hudm .npwin .who b { display: block; font-size: 20px; color: #fff; letter-spacing: .03em; text-shadow: 0 0 10px var(--glow); }
.hudm .npwin .who small { font-size: 11px; color: var(--ac); }
.hudm .npwin .svc { display: flex; flex-wrap: wrap; justify-content: center; gap: 4px; }
.hudm .npwin .svc button { display: flex; align-items: center; gap: 4px; padding: 3px 9px; border-radius: 10px; cursor: pointer; font: inherit; font-size: 11px; color: var(--tx); border: 1px solid var(--fr); background: var(--pb); }
.hudm .npwin .svc button svg { width: 12px; height: 12px; }
.hudm .npwin .svc button:hover { color: #fff; border-color: var(--ac); }
.hudm .npwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .npwin .tabrow { display: flex; align-items: center; gap: 8px; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .npwin .tabrow .sp { flex: 1; }
.hudm .npwin .tabrow .lg { cursor: pointer; font-size: 11px; font-weight: 600; color: var(--dim); background: none; border: 0; font-family: inherit; padding: 4px 6px; }
.hudm .npwin .tabrow .lg:hover, .hudm .npwin .tabrow .lg.on { color: var(--ac); }
.hudm .npwin .talk { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 18px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .npwin .say { font-size: 14px; line-height: 1.6; color: #e9eef8; white-space: pre-wrap; cursor: pointer; min-height: 60px; overflow-wrap: anywhere; }
.hudm .npwin .say .caret { display: inline-block; width: 7px; height: 14px; margin-left: 2px; vertical-align: -2px; background: var(--ac); animation: hudm-npcaret .8s steps(2) infinite; }
@keyframes hudm-npcaret { 50% { opacity: 0; } }
.hudm .npwin .say a { color: var(--ac); cursor: pointer; text-decoration: underline; text-underline-offset: 3px; font-weight: 600; } .hudm .npwin .say a:hover { color: #fff; }
.hudm .npwin .say .it { cursor: help; font-weight: 700; color: var(--info); } .hudm .npwin .say .it:hover { color: var(--warn); }
.hudm .npwin .say .it::before { content: "["; opacity: .6; } .hudm .npwin .say .it::after { content: "]"; opacity: .6; }
.hudm .npwin .say .tw { visibility: hidden; }
.hudm .npwin .opts { display: flex; flex-direction: column; gap: 5px; margin-top: 12px; opacity: 0; transform: translateY(6px); transition: opacity .25s, transform .25s; pointer-events: none; }
.hudm .npwin .opts.on { opacity: 1; transform: none; pointer-events: auto; }
.hudm .npwin .op { display: flex; align-items: center; gap: 10px; padding: 7px 10px; border-radius: var(--rad); cursor: pointer; font-size: 13px; color: var(--tx); text-align: left; font-family: inherit; width: 100%;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 10%, transparent), transparent); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); transition: transform .12s, border-color .12s; }
.hudm .npwin .op:hover { border-color: var(--ac); transform: translateX(4px); color: #fff; }
.hudm .npwin .op kbd { width: 20px; height: 20px; flex: none; display: flex; align-items: center; justify-content: center; font: 700 11px ui-monospace, Consolas, monospace; border-radius: 4px; color: var(--ac); border: 1px solid var(--ac); }
.hudm .npwin .op kbd:empty { visibility: hidden; }
.hudm .npwin .op span { flex: 1; }
.hudm .npwin .op i { width: 10px; height: 10px; border-radius: 50%; flex: none; }
.hudm .npwin .op.exit { background: none; border-style: dashed; color: var(--dim); }
.hudm .npwin .foot { display: flex; align-items: center; gap: 8px; padding: 6px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 11px; color: var(--dim); }
.hudm .npwin .foot .sp { flex: 1; } .hudm .npwin .foot b { color: var(--gold); } .hudm .npwin .foot .btn { height: 22px; }
.hudm .npwin .log { padding: 6px 18px; max-height: 120px; overflow-y: auto; border-bottom: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); font-size: 11px; color: var(--dim); background: #0004; flex: none; }
.hudm .npwin .log div { padding: 2px 0; } .hudm .npwin .log b { color: var(--tx); }
.hudm .npwin .ql { flex: 1; min-height: 0; overflow-y: auto; padding: 10px 14px; display: flex; flex-direction: column; gap: 8px; scrollbar-width: thin; }
.hudm .npwin .qc { display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .npwin .qc .qm { width: 30px; height: 30px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 17px; font-weight: 900; border: 1px solid currentColor; }
.hudm .npwin .qc .qt { flex: 1; min-width: 0; font-size: 12px; } .hudm .npwin .qc .qt > b { font-size: 13px; color: #fff; } .hudm .npwin .qc .qt small { margin-left: 6px; font-size: 10px; color: var(--dim); }
.hudm .npwin .qc .qt p { margin: 3px 0; color: var(--tx); line-height: 1.4; white-space: pre-wrap; overflow-wrap: anywhere; }
.hudm .npwin .qc .qt .sec { margin-top: 6px; font-size: 10px; font-weight: 700; letter-spacing: .1em; color: var(--acd); text-transform: uppercase; }
.hudm .npwin .qc .qt .it { font-weight: 700; color: var(--info); cursor: help; }
.hudm .npwin .qc .rw { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 4px; color: var(--gold); }
.hudm .npwin .qc .rcell { position: relative; width: 36px; height: 36px; border-radius: 4px; border: 1px solid var(--fr); background: var(--field) center / contain no-repeat; cursor: help; }
.hudm .npwin .qc .rcell.pick { cursor: pointer; } .hudm .npwin .qc .rcell.pick:hover { border-color: var(--ac); }
.hudm .npwin .qc .rcell.on { border-color: var(--ac); box-shadow: 0 0 0 1px var(--ac); }
.hudm .npwin .qc .rcell em { position: absolute; right: 2px; bottom: 0; font-style: normal; font-size: 10px; color: #fff; text-shadow: 0 1px 2px #000; }
.hudm .npwin .qc .btns { display: flex; flex-direction: column; gap: 4px; justify-content: center; flex: none; }
.hudm .npwin .qc .btns small { color: var(--dim); font-size: 10px; text-align: center; }
.hudm .npwin .qempty { padding: 30px; text-align: center; color: var(--dim); }
.hudm .npwin .icard { position: absolute; z-index: 5; min-width: 200px; max-width: 320px; display: flex; gap: 10px; padding: 8px 10px; pointer-events: none; border-radius: var(--rad);
  background: color-mix(in srgb, var(--pb) 96%, #000); border: 1px solid var(--fr); box-shadow: 0 8px 20px rgba(0,0,0,.6); font-size: 11px; color: var(--tx); }
.hudm .npwin .icard img { image-rendering: pixelated; max-width: 96px; max-height: 120px; object-fit: contain; align-self: flex-start; }
.hudm .npwin .icard b { display: block; font-size: 13px; color: #fff; margin-bottom: 3px; } .hudm .npwin .icard div { white-space: pre-line; line-height: 1.45; }
/* Narrow layer: a slimmer portrait column. */
.hudm.narrow .npwin { width: 640px; }
.hudm.narrow .npwin .side { width: 170px; }
.hudm.narrow .npwin .stage { width: 150px; height: 170px; }
.hudm.narrow .npwin .who b { font-size: 16px; }
`;

/** A loaded portrait: the Standing loop and the Harvest gesture, and the box the Standing frames span. */
interface Portrait {
  readonly key: string;
  readonly stand: LibPicture[]; readonly standMs: number;
  readonly harvest: LibPicture[]; readonly harvestMs: number;
  readonly box: readonly [number, number, number, number];
}

/** An info link on the shown page (its text follows the info once it arrives). */
interface InfoLink { readonly span: HTMLElement; readonly kind: NpcLinkKind; readonly index: number; readonly name: string | undefined }

class NpcWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: 0 | 1 = 0; // Talk / Quests
  private showLog = false;
  private hover = false;
  private shownSeq = -1;
  private shownLog = -1;
  private questsDirty = true;
  private choices: Choice[] = [];
  private links: InfoLink[] = [];
  private readonly pendingQuests = new Set<number>();
  private readonly rewardPick = new Map<number, number>();

  // typewriter
  private typing: { texts: [Text, string][]; hidden: HTMLElement[]; total: number; shown: number; start: number } | null = null;
  private readonly typeTick;
  // portrait
  private portrait: Portrait | null = null;
  private portraitKey = "";
  private portraitNpc = 0;
  private portraitLoads = 0;
  private gestureAt = -1;
  private shownPic: LibPicture | null = null;
  // info card
  private cardSeq = 0;

  // — DOM, built once —
  private readonly titleText: HTMLElement;
  private readonly stage = h("div", "stage");
  private readonly spr = h("div", "spr");
  private readonly sprImg = h("img");
  private readonly mark = h("div", "mark");
  private readonly whoName = h("b");
  private readonly whoPlace = h("small");
  private readonly svc = h("div", "svc");
  private readonly tabsW: ReturnType<typeof tabs>;
  private readonly logBtn = h("button", "lg", "Log");
  private readonly logEl = h("div", "log");
  private readonly talk = h("div", "talk");
  private readonly say = h("div", "say");
  private readonly caret = h("span", "caret");
  private readonly opts = h("div", "opts");
  private readonly ql = h("div", "ql");
  private readonly gold = h("b");
  private readonly mainBtn: Btn;
  private readonly hint = h("span", "", "1-9 choose (pointer on the window) · Space skip");
  private readonly card = h("div", "icard");

  constructor(private readonly ui: ModernUiHost, private readonly state: NpcDialogState) {
    super(ui.rt, "npwin", 40, 50);
    injectCss("win-npc", NPC_CSS);
    this.m = ui.ctx.models;
    const sig = ui.rt.signal;

    this.sprImg.alt = "";
    this.sprImg.draggable = false;
    this.spr.append(this.sprImg);
    this.mark.hidden = true;
    this.stage.append(this.spr, this.mark);
    const side = h("div", "side", this.stage, h("div", "who", this.whoName, this.whoPlace), this.svc);

    this.tabsW = tabs(["Talk", "Quests"], 0, (i) => { this.tab = i === 1 ? 1 : 0; this.questsDirty = true; this.invalidate(); });
    this.logBtn.type = "button";
    this.logBtn.title = "Show what was said with this NPC";
    this.logBtn.addEventListener("click", () => { this.showLog = !this.showLog; this.shownLog = -1; this.invalidate(); }, { signal: sig });
    const tabRow = h("div", "tabrow", this.tabsW.el, h("div", "sp"), this.logBtn);

    this.say.addEventListener("click", () => this.finishTyping(), { signal: sig });
    this.talk.append(this.say, this.opts);
    this.mainBtn = button("Main", () => this.choose("@Main", "Main"), { icon: "home", title: "Back to this NPC's first page" });
    const foot = h("div", "foot", this.mainBtn.el, h("span", "sp"), "Gold ", this.gold, this.hint);
    this.logEl.hidden = true;
    this.ql.hidden = true;
    const main = h("div", "main", tabRow, this.logEl, this.talk, this.ql, foot);
    this.card.hidden = true;
    this.titleText = h("span");
    const title = this.titleBar("", "Close (Esc)");
    title.prepend(this.titleText);
    this.el.append(h("div", "panel", title, h("div", "nbody", side, main), this.card));

    // Number keys only while the pointer is over the dialogue (the belt keys keep working otherwise).
    this.el.addEventListener("pointerenter", () => { this.hover = true; }, { signal: sig });
    this.el.addEventListener("pointerleave", () => { this.hover = false; }, { signal: sig });

    this.typeTick = ui.rt.ticker.frame((now) => this.typeStep(now));
    sig.addEventListener("abort", () => this.typeTick.off(), { once: true });
    this.everyShown(100, (now) => this.animate(now));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(state.changed.on(() => this.invalidate()));
    on(this.m.inventory.subscribe(() => { if (this.open) setText(this.gold, fmt(this.m.inventory.gold)); }));
    on(this.m.quests.onChange((c) => {
      if (c === "quests") this.pendingQuests.clear();
      if (c !== "tracking" && c !== "items") { this.questsDirty = true; this.invalidate(); }
    }));
    on(this.m.gameInfo.onChange((kind, index) => this.infoArrived(kind, index)));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.questsDirty = true; this.invalidate(); } }));
  }

  private get npc() { return this.m.npcSession; }

  protected override shown(): void {
    setText(this.gold, fmt(this.m.inventory.gold));
    if (this.typing) this.typeTick.on();
  }

  protected override closed(): void {
    this.typeTick.off();
    this.finishTyping();
    this.hideCard();
    this.ui.tooltip.hide();
    this.hover = false;
    this.pendingQuests.clear();
    this.questsDirty = true;
    // NPCDialog.Hide: the drop panel and every NPC window go with it (no packet, as the classic).
    if (this.npc.open) this.npc.close();
  }

  // — Keys (WindowManager.handleKey: only while this is the front window) —

  override key(e: KeyboardEvent): boolean {
    if (e.ctrlKey || e.altKey || e.metaKey || isTextTarget(e)) return false;
    if (e.code === "Space" && this.typing) { this.finishTyping(); return true; }
    const n = /^Digit([1-9])$/.exec(e.code);
    if (n && !e.shiftKey && !e.repeat && this.hover && this.tab === 0 && !this.typing) {
      const c = this.choices[+n[1]! - 1];
      if (c) { this.choose(c.key, c.text); return true; }
    }
    return false;
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const s = this.npc;
    const [name, place] = npcName(s.npcName || "NPC");
    setText(this.titleText, name.toUpperCase());
    setText(this.whoName, name);
    setText(this.whoPlace, place);
    this.whoPlace.hidden = !place;
    setText(this.gold, fmt(this.m.inventory.gold));
    this.renderPortrait();

    // Quests tab only when this NPC has quests (QuestButton visibility); badge = acceptable + ready to hand in.
    const quests = this.m.quests.availableQuests(s.npcId);
    const qTab = this.tabsW.el.children[1] as HTMLElement | undefined;
    if (qTab) qTab.hidden = quests.length === 0;
    if (quests.length === 0 && this.tab === 1) { this.tab = 0; this.tabsW.select(0); }
    this.tabsW.badge(1, quests.filter((q) => !q.progress.Taken || q.progress.Completed).length);
    const marker = questMiniMapMarker(this.m.quests.npcQuestIcon(s.npcId));
    this.mark.hidden = !marker;
    if (marker) { setText(this.mark, marker.text); setStyle(this.mark, "color", marker.colour); }

    if (this.shownSeq !== this.state.seq) this.renderPage();
    this.mainBtn.el.hidden = s.lastPageKey.toLowerCase() === "@main";

    this.logBtn.classList.toggle("on", this.showLog);
    setText(this.logBtn, this.showLog ? "Hide log" : "Log");
    this.logEl.hidden = !this.showLog;
    if (this.showLog && this.shownLog !== this.state.logVersion) this.renderLog();

    this.talk.hidden = this.tab !== 0;
    this.ql.hidden = this.tab !== 1;
    this.hint.hidden = this.tab !== 0;
    if (this.tab === 1 && this.questsDirty) this.renderQuests(quests);
  }

  // — The page —

  private renderPage(): void {
    this.shownSeq = this.state.seq;
    this.hideCard();
    this.ui.tooltip.hide();
    const page = this.npc.currentPage;
    this.links = [];
    const choices: Choice[] = page.bigButtons.map((b) => ({ text: b.text, key: b.action }));
    const colours = new Map<string, string>(page.bigButtons.map((b) => [b.action, b.colour]));
    const kids: Node[] = [];
    let blank = 0;
    for (const row of page.rows) {
      const only = linkOnly(row);
      if (only) { choices.push(...only); continue; }
      const empty = rowText(row).trim() === "";
      if (empty) { if (++blank > 1 || kids.length === 0) continue; } else blank = 0;
      if (kids.length) kids.push(document.createTextNode("\n"));
      kids.push(...this.renderRow(row, true));
    }
    while (kids.length && kids[kids.length - 1]!.nodeType === 3 && !(kids[kids.length - 1]!.textContent ?? "").trim()) kids.pop();
    this.say.replaceChildren(...kids);
    this.talk.scrollTop = 0;

    // Option cards: numbered 1-9 (the rest still click); @Exit as a dashed card.
    this.choices = choices;
    this.opts.replaceChildren(...choices.map((c, i) => {
      const exit = c.key.toLowerCase() === "@exit";
      const colour = colours.get(c.key);
      const dot = colour && colour !== "RoyalBlue" && CSS.supports("color", colour) ? h("i") : null;
      if (dot) dot.style.background = colour!;
      const b = h("button", "op" + (exit ? " exit" : ""), h("kbd", "", i < 9 ? String(i + 1) : ""), h("span", "", c.text), dot);
      b.type = "button";
      b.addEventListener("click", () => this.choose(c.key, c.text));
      return b;
    }));

    // Service chips: the page's own service links (each is a key the server accepts on this page).
    const seen = new Set<string>();
    const chips: HTMLElement[] = [];
    const chip = (c: Choice) => {
      const k = c.key.replace(/^@/, "").toUpperCase();
      const svc = SERVICES[k];
      if (!svc || seen.has(k)) return;
      seen.add(k);
      const b = h("button", "", svg(svc[0], ""), h("span", "", svc[1]));
      b.type = "button";
      b.title = c.text;
      b.addEventListener("click", () => this.choose(c.key, c.text));
      chips.push(b);
    };
    for (const c of choices) chip(c);
    for (const row of page.rows) for (const seg of row) if (seg.kind === "link") chip(seg);
    this.svc.replaceChildren(...chips);
    this.svc.hidden = chips.length === 0;

    this.startTyping();
    this.gestureAt = reducedMotion() ? -1 : performance.now();
  }

  /** NewButton / NewColour / NewLink on a row (npcMarkup.ts segments). `pageLinks` = page links call the NPC (quest
   *  texts show them as plain text: the server only accepts the current page's keys). */
  private renderRow(row: readonly NpcSegment[], pageLinks: boolean): Node[] {
    const out: Node[] = [];
    for (const seg of row) {
      switch (seg.kind) {
        case "text": out.push(document.createTextNode(seg.text)); break;
        case "link": {
          if (!pageLinks) { out.push(document.createTextNode(seg.text)); break; }
          const a = h("a", "", seg.text);
          a.addEventListener("click", (e) => { e.stopPropagation(); this.choose(seg.key, seg.text); });
          out.push(a);
          break;
        }
        case "colour": {
          // NewColour: Color.FromName (an unknown name draws transparent on the desktop)
          const span = h("span", "", seg.text);
          span.style.color = CSS.supports("color", seg.colour) ? seg.colour : "transparent";
          out.push(span);
          break;
        }
        case "url": {
          // NewButton with an address: http(s) only, in a new tab
          const a = h("a", "", seg.text);
          const url = seg.url;
          a.title = url;
          a.addEventListener("click", (e) => {
            e.stopPropagation();
            if (/^https?:\/\//i.test(url)) window.open(url, "_blank", "noopener,noreferrer");
          });
          out.push(a);
          break;
        }
        case "info": {
          // NewLink: the info's tooltip on hover
          const span = h("span", "it", this.linkName(seg.link, seg.index, seg.name));
          const link: InfoLink = { span, kind: seg.link, index: seg.index, name: seg.name };
          span.addEventListener("pointerenter", () => this.showInfo(link));
          span.addEventListener("pointerleave", () => this.hideInfo());
          span.addEventListener("click", (e) => { e.stopPropagation(); this.hideInfo(); });
          this.links.push(link);
          out.push(span);
          break;
        }
      }
    }
    return out;
  }

  /** A link, option card or chip: C.CallNPC "[key]" through the session (the 5 s gate; @Exit closes). */
  private choose(key: string, text: string): void {
    this.finishTyping();
    if (key.toLowerCase() !== "@exit") this.state.chose(text);
    this.npc.call(key);
  }

  // — Typewriter (the UI ticker, only while a page types and the window is shown) —

  private startTyping(): void {
    this.typeTick.off();
    this.typing = null;
    this.say.append(this.caret);
    const texts: [Text, string][] = [];
    const hidden: HTMLElement[] = [];
    let total = 0;
    const walk = (n: Node) => {
      if (n.nodeType === 3) { const t = n.textContent ?? ""; if (t) { texts.push([n as Text, t]); total += t.length; (n as Text).textContent = ""; } }
      else n.childNodes.forEach(walk);
    };
    for (const c of Array.from(this.say.childNodes)) if (c !== this.caret) walk(c);
    for (const c of Array.from(this.say.children)) if (c !== this.caret) { (c as HTMLElement).classList.add("tw"); hidden.push(c as HTMLElement); }
    this.opts.classList.remove("on");
    this.typing = { texts, hidden, total, shown: 0, start: -1 };
    if (reducedMotion() || total === 0) { this.finishTyping(); return; }
    if (this.open) this.typeTick.on();
  }

  private typeStep(now: number): void {
    const t = this.typing;
    if (!t) { this.typeTick.off(); return; }
    if (t.start < 0) t.start = now;
    const want = Math.min(t.total, Math.floor((now - t.start) * TYPE_RATE / 1000));
    if (want <= t.shown) return;
    let left = want;
    for (const [node, full] of t.texts) {
      const n = Math.min(left, full.length);
      if (node.length !== n) {
        node.textContent = full.slice(0, n);
        const p = node.parentElement;
        if (n > 0 && p && p !== this.say) p.closest(".tw")?.classList.remove("tw");
      }
      left -= n;
      if (left <= 0) break;
    }
    t.shown = want;
    if (want >= t.total) this.finishTyping();
  }

  /** Shows the whole page at once (click, Space, a choice, closing). */
  private finishTyping(): void {
    const t = this.typing;
    this.typeTick.off();
    if (!t) return;
    this.typing = null;
    for (const [node, full] of t.texts) if (node.textContent !== full) node.textContent = full;
    for (const e of t.hidden) e.classList.remove("tw");
    this.caret.remove();
    this.opts.classList.add("on");
  }

  // — Log —

  private renderLog(): void {
    this.shownLog = this.state.logVersion;
    const log = this.state.log;
    if (!log.length) { this.logEl.replaceChildren(h("div", "", "Nothing said yet.")); return; }
    this.logEl.replaceChildren(...log.map((l) => h("div", "", h("b", "", `${l.who}: `), l.text)));
    this.logEl.scrollTop = this.logEl.scrollHeight;
  }

  // — Quests (QuestListDialog for this NPC) —

  private renderQuests(list: QuestEntry[]): void {
    this.questsDirty = false;
    if (!list.length) { this.ql.replaceChildren(h("div", "qempty", "No quests here.")); return; }
    const q = this.m.quests;
    this.ql.replaceChildren(...list.map((e) => this.questCard(e, q.current.length)));
  }

  private questCard(e: QuestEntry, currentCount: number): HTMLElement {
    const { info, progress } = e;
    const npcId = this.npc.npcId;
    const marker = questMiniMapMarker(entryIcon(e));
    const qm = h("div", "qm", marker?.text ?? "…");
    if (marker) qm.style.color = marker.colour; else qm.style.color = "var(--dim)";

    const kind = QuestTypeName[info.Type] ?? "";
    const lv = info.MaxLevelNeeded > 0 && info.MaxLevelNeeded < 65535 && info.MaxLevelNeeded !== info.MinLevelNeeded
      ? `Lv ${info.MinLevelNeeded}-${info.MaxLevelNeeded}` : `Lv ${info.MinLevelNeeded}`;
    const t = h("div", "qt", h("b", "", info.Name), h("small", "", [lv, kind, info.Group].filter(Boolean).join(" · ")));
    // QuestMessage.UpdateQuest: the completion text at the finish NPC, else description / task / return / time / progress.
    const text = (lines: readonly string[]) => {
      const p = h("p");
      lines.forEach((ln, i) => { if (i) p.append("\n"); p.append(...this.questRow(ln)); });
      return p;
    };
    if (progress.Taken && !sameFinishNPC(info) && info.CompletionDescription.length > 0 && npcId === info.FinishNPCIndex) {
      t.append(text(info.CompletionDescription));
    } else {
      if (info.Description.length) t.append(text(info.Description));
      if (info.TaskDescription.length) t.append(h("div", "sec", "Tasks"), text(info.TaskDescription));
      if (info.ReturnDescription.length) t.append(h("div", "sec", "Return"), text(info.ReturnDescription));
      if (info.TimeLimitInSeconds > 0) t.append(h("div", "sec", "Time limit"), h("p", "", printTimeSpanFromSeconds(info.TimeLimitInSeconds)));
      if (progress.Taken && progress.TaskList.length) t.append(h("div", "sec", "Progress"), text(progress.TaskList));
    }
    t.append(this.rewards(info, progress.Completed));

    const btns = h("div", "btns");
    const pending = this.pendingQuests.has(info.Index);
    if (!progress.Taken) {
      const full = currentCount >= MAX_CONCURRENT_QUESTS;
      const b = button(pending ? "Accepting…" : "Accept", () => this.accept(info), { cls: "main" });
      if (pending || full) b.setDisabled(true, full ? `You can carry at most ${MAX_CONCURRENT_QUESTS} quests.` : "Waiting for the server");
      btns.append(b.el);
    } else if (progress.Completed) {
      const b = button(pending ? "Handing in…" : "Hand in", () => this.finish(info), { cls: "main" });
      if (pending) b.setDisabled(true, "Waiting for the server");
      btns.append(b.el);
    } else {
      btns.append(h("small", "", "In progress"));
      const b = button("Abandon", () => void this.abandon(info), { title: "Abandon this quest" });
      if (pending) b.setDisabled(true, "Waiting for the server");
      btns.append(b.el);
    }
    return h("div", "qc", qm, t, btns);
  }

  /** A quest text row: colours and info links; page links as plain text. */
  private questRow(line: string): Node[] {
    return this.renderRow(parseLine(line), false);
  }

  /** QuestRewards: exp / gold / credit, the fixed items, the select items (the user's gender only; one to pick when the
   *  quest is ready to hand in). */
  private rewards(info: ClientQuestInfo, ready: boolean): HTMLElement {
    const rw = h("div", "rw");
    const parts: string[] = [];
    if (info.RewardExp > 0) parts.push(`${fmt(info.RewardExp)} EXP`);
    if (info.RewardGold > 0) parts.push(`${fmt(info.RewardGold)} gold`);
    if (info.RewardCredit > 0) parts.push(`${fmt(info.RewardCredit)} credit`);
    if (parts.length) rw.append(h("span", "", parts.join(" · ")));
    for (const r of info.RewardsFixedItem.slice(0, 5)) rw.append(this.rewardCell(r.Item, r.Count, null));
    const gender = this.m.quests.gender;
    const select = info.RewardsSelectItem
      .map((reward, index) => ({ reward, index }))
      .filter(({ reward }) => {
        const g = reward.Item.RequiredGender;
        return !(gender === MirGender.Male && !(g & RequiredGender.Male)) && !(gender === MirGender.Female && !(g & RequiredGender.Female));
      })
      .slice(0, 5);
    if (select.length) {
      rw.append(h("span", "", ready ? "Choose one:" : "One of:"));
      for (const { reward, index } of select) rw.append(this.rewardCell(reward.Item, reward.Count, ready ? { quest: info.Index, index } : null));
    }
    if (!rw.childNodes.length) rw.append(h("span", "", "No reward listed."));
    return h("div", "", h("div", "sec", "Reward"), rw);
  }

  private rewardCell(info: ItemInfo, count: number, pick: { quest: number; index: number } | null): HTMLElement {
    const cell = h("div", "rcell" + (pick ? " pick" : ""), count > 1 ? h("em", "", String(count)) : null);
    void libPicture(Lib.Items, info.Image).then((p) => { if (p) cell.style.backgroundImage = `url(${p.url})`; });
    const name = itemFriendlyName(info) || info.Name;
    cell.setAttribute("aria-label", name);
    if (pick) cell.classList.toggle("on", this.rewardPick.get(pick.quest) === pick.index);
    cell.addEventListener("pointerenter", () => this.ui.tooltip.show(cell, displayItem(info, info.Index, count), this.ui.labelContext(), { info }));
    cell.addEventListener("pointerleave", () => this.ui.tooltip.hide());
    if (pick) cell.addEventListener("click", () => {
      this.rewardPick.set(pick.quest, pick.index);
      for (const c of Array.from(cell.parentElement?.querySelectorAll<HTMLElement>(".rcell.pick") ?? [])) c.classList.toggle("on", c === cell);
    });
    return cell;
  }

  /** Locks a quest's buttons until S.ChangeQuest; a refusal sends nothing back, so the lock lifts after 5 s. */
  private lockQuest(index: number): void {
    this.pendingQuests.add(index);
    later(() => { if (this.pendingQuests.delete(index)) { this.questsDirty = true; this.invalidate(); } }, 5000, this.signal);
    this.questsDirty = true;
    this.invalidate();
  }

  /** QuestListDialog accept: C.AcceptQuest (NPCIndex = the quest's NPC). Locked until S.ChangeQuest. */
  private accept(info: ClientQuestInfo): void {
    if (this.pendingQuests.has(info.Index)) return;
    this.lockQuest(info.Index);
    this.m.quests.accept(info.NPCIndex, info.Index);
  }

  /** QuestListDialog finish: a quest with select rewards needs one picked (YouMustSelectRewardItem). */
  private finish(info: ClientQuestInfo): void {
    if (this.pendingQuests.has(info.Index)) return;
    const sel = this.rewardPick.get(info.Index) ?? -1;
    if (sel < 0 && info.RewardsSelectItem.length > 0) { void this.ui.rt.popups.ask(T.YouMustSelectRewardItem, { title: info.Name }); return; }
    this.lockQuest(info.Index);
    this.m.quests.finish(info.Index, sel);
  }

  /** QuestDetailDialog cancel: the question, then C.AbandonQuest. */
  private async abandon(info: ClientQuestInfo): Promise<void> {
    const ok = await this.ui.rt.popups.ask(T.AskCancelQuest(info.Name), { title: "Abandon quest", kind: "warning", okCancel: true });
    if (!ok || this.pendingQuests.has(info.Index)) return;
    this.lockQuest(info.Index);
    this.m.quests.abandon(info.Index);
  }

  // — Info links (NPCDialog.GetDisplayNameForLink / ShowTooltip) —

  /** The link's text: its given name, else the info's name (requested once when missing). */
  private linkName(kind: NpcLinkKind, idx: number, provided: string | undefined): string {
    if (provided) return provided;
    const gi = this.m.gameInfo;
    switch (kind) {
      case "ITEM": { const i = gi.item(idx); if (i) return itemFriendlyName(i) || i.Name || `Item ${idx}`; gi.requestItem(idx); return `Item ${idx}`; }
      case "MONSTER": { const m = gi.monster(idx); if (m) return m.GameName || m.Name; gi.requestMonster(idx); return `Monster ${idx}`; }
      case "NPC": { const n = gi.npc(idx); if (n) return n.Name; gi.requestNPC(idx); return `NPC ${idx}`; }
    }
  }

  private infoArrived(kind: string, index: number): void {
    if (kind === "item") { this.questsDirty = true; if (this.tab === 1) this.invalidate(); }
    const want = kind === "monster" ? "MONSTER" : kind === "npc" ? "NPC" : kind === "item" ? "ITEM" : null;
    for (const l of this.links) {
      const match = want ? l.kind === want && l.index === index : kind === "map" && l.kind === "NPC"; // a map title for an NPC card
      if (!match) continue;
      if (want && !l.name) setText(l.span, this.linkName(l.kind, l.index, undefined));
      if (this.cardLink === l) this.showInfo(l);
    }
  }

  private cardLink: InfoLink | null = null;

  /** Item: the shared item tooltip; monster / NPC: NPCDialog.ShowTooltip's card (image, title, lines). */
  private showInfo(link: InfoLink): void {
    if (link.kind === "ITEM") {
      const info = this.m.gameInfo.item(link.index);
      if (!info) this.m.gameInfo.requestItem(link.index);
      this.ui.tooltip.show(link.span, displayItem(info, link.index), this.ui.labelContext());
      return;
    }
    this.cardLink = link;
    const gi = this.m.gameInfo;
    let title = "", body = "";
    let image: { lib: string; index: number } | null = null;
    if (link.kind === "MONSTER") {
      const m = gi.monster(link.index);
      if (m) {
        const st = (s: number) => m.Stats[s] ?? 0;
        title = m.GameName || m.Name;
        body = [`Level: ${m.Level}`, `Health: ${fmt(st(Stat.HP))}`, `Experience: ${fmt(m.Experience)}`,
          `DC: ${st(Stat.MinDC)}~${st(Stat.MaxDC)}`, `MC: ${st(Stat.MinMC)}~${st(Stat.MaxMC)}`,
          `AC: ${st(Stat.MinAC)}~${st(Stat.MaxAC)}`, `MAC: ${st(Stat.MinMAC)}~${st(Stat.MaxMAC)}`].join("\n");
        const lib = monsterLibPath(m.Image);
        if (lib !== null) image = { lib, index: 20 };
      } else { gi.requestMonster(link.index); title = T.MonsterIndexTitle(link.index); body = T.MonsterInfoLoading; }
    } else {
      const n = gi.npc(link.index);
      if (n) {
        title = n.Name;
        const map = this.m.currentMap;
        const mapName = map.index === n.MapIndex ? map.title : n.MapIndex > 0 ? gi.map(n.MapIndex)?.Title ?? "" : "";
        let where: string;
        if (mapName.trim()) where = `Location: ${mapName}`;
        else if (n.MapIndex > 0) { gi.requestMap(n.MapIndex); where = "Location: Loading map..."; }
        else where = "Location: Unknown map";
        body = [`Name: ${n.Name}`, where, `Coordinates: (${n.Location.X}, ${n.Location.Y})`].join("\n");
        image = { lib: LibPath.npc(n.Image), index: 0 };
      } else { gi.requestNPC(link.index); title = T.NPCIndexTitle(link.index); body = T.NPCInfoLoading; }
    }
    const img = h("img");
    img.alt = "";
    img.hidden = true;
    this.card.replaceChildren(img, h("div", "", h("b", "", title), body));
    const seq = ++this.cardSeq;
    if (image) void libPicture(image.lib, image.index).then((p) => { if (p && seq === this.cardSeq) { img.src = p.url; img.hidden = false; } });
    // Placed under the link, inside the panel (measured once per hover).
    const panel = this.card.parentElement as HTMLElement;
    const r = link.span.getBoundingClientRect();
    const [x, y] = toLocal(panel, r.left, r.bottom);
    this.card.hidden = false;
    const maxX = panel.clientWidth - this.card.offsetWidth - 6;
    const cy = y + 6 + this.card.offsetHeight > panel.clientHeight ? Math.max(4, toLocal(panel, r.left, r.top)[1] - this.card.offsetHeight - 6) : y + 6;
    setStyle(this.card, "left", `${Math.max(4, Math.min(x, maxX))}px`);
    setStyle(this.card, "top", `${cy}px`);
  }

  private hideInfo(): void {
    this.ui.tooltip.hide();
    this.hideCard();
  }

  private hideCard(): void {
    this.cardLink = null;
    this.cardSeq++;
    this.card.hidden = true;
  }

  // — Portrait (the NPC's library: Standing in its direction, Harvest as the page gesture) —

  private renderPortrait(): void {
    const ob = this.ui.ctx.world.objects.get(this.npc.npcId);
    const path = ob ? npcLibPath(ob.image) : null;
    if (!ob || path === null) {
      // Keep this NPC's portrait if it left the view; another NPC (or none loaded) shows no portrait.
      if (this.portraitNpc !== this.npc.npcId || !this.portrait) { this.portraitLoads++; this.portrait = null; this.portraitKey = ""; this.stage.hidden = true; }
      return;
    }
    const stand = ob.frames.get(MirAction.Standing);
    const harvest = ob.frames.get(MirAction.Harvest);
    const key = `${ob.id}|${path}|${ob.direction}|${stand?.start}|${harvest?.start}|${this.wm.layers.layout.narrow}`;
    if (key === this.portraitKey) return;
    this.portraitKey = key;
    this.portraitNpc = ob.id;
    const seq = ++this.portraitLoads;
    const frames = (fr: Frame | undefined) => fr
      ? Promise.all(Array.from({ length: fr.count }, (_, i) => libPicture(path, fr.start + frameOffset(fr) * ob.direction + i)))
        .then((ps) => ps.filter((p): p is LibPicture => !!p))
      : Promise.resolve([] as LibPicture[]);
    void Promise.all([frames(stand), frames(harvest)]).then(([s, hv]) => {
      if (seq !== this.portraitLoads) return;
      const all = s.length ? s : hv; // the standing frames set the box; gestures may reach outside it
      if (!all.length) { this.portrait = null; this.stage.hidden = true; return; }
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
      for (const p of all) { x0 = Math.min(x0, p.x); y0 = Math.min(y0, p.y); x1 = Math.max(x1, p.x + p.width); y1 = Math.max(y1, p.y + p.height); }
      this.portrait = { key, stand: s, standMs: stand?.interval || 450, harvest: hv, harvestMs: harvest?.interval || 200, box: [x0, y0, x1, y1] };
      this.stage.hidden = false;
      // Fit the box into the stage once (measure-once: the stage size is fixed per layout).
      const w = x1 - x0, ht = y1 - y0;
      const sh = this.stage.clientHeight || 220, sw = this.stage.clientWidth || 210;
      const k = Math.min(3, Math.max(1, sh - 50) / Math.max(1, ht), Math.max(1, sw - 20) / Math.max(1, w));
      Object.assign(this.spr.style, { width: `${w}px`, height: `${ht}px`, marginLeft: `${-w / 2}px`, transform: `scale(${k})` });
      this.shownPic = null;
      this.animate(performance.now());
    });
  }

  private animate(now: number): void {
    const p = this.portrait;
    if (!p || !this.open) return;
    let pic: LibPicture | undefined;
    if (this.gestureAt >= 0 && p.harvest.length) {
      const i = Math.floor((now - this.gestureAt) / p.harvestMs);
      if (i < p.harvest.length) pic = p.harvest[Math.max(0, i)];
      else this.gestureAt = -1;
    }
    if (!pic) pic = p.stand.length ? p.stand[Math.floor(now / p.standMs) % p.stand.length] : p.harvest[0];
    if (!pic || pic === this.shownPic) return;
    this.shownPic = pic;
    const [x0, y0] = p.box;
    this.sprImg.src = pic.url;
    Object.assign(this.sprImg.style, { left: `${pic.x - x0}px`, top: `${pic.y - y0}px`, width: `${pic.width}px`, height: `${pic.height}px` });
  }

  protected override disposed(): void {
    this.typeTick.off();
    this.typing = null;
  }
}

/** Opens the dialogue on each page the server sends and closes it with the session. Called by this window's watch hook
 *  once per game (the window itself is built on the first page). */
function watchNpc(host: ModernUiHost, open: (show?: boolean) => boolean): () => void {
  npcDialogState(host); // subscribes before the opener below, so the page count is current when the window renders
  const s = host.ctx.models.npcSession;
  const offPage = s.page.on(() => { open(true); });
  const offClosed = s.closed.on(() => { open(false); });
  return () => { offPage(); offClosed(); };
}

registerWindow(NPC_WINDOW, (host) => new NpcWindow(host, npcDialogState(host)), {
  binds: [],
  watch: (host, open) => watchNpc(host, open),
});
