// Ported from play/hudChat.ts: the new UI's chat box (bottom left, mostly transparent; the desktop's ChatDialog +
// ChatControlBar + ChatOptionDialog, Client/MirScenes/Dialogs/MainDialogs.cs) and the announcement ticker
// (ChatNoticeDialog), as a view over the ChatLog model (src/game/chatLog.ts). The mock's simulation, name tables, local
// @command answers and lab item list are gone; every line comes from ChatLog `added`, every send goes through
// ChatLog.submit (C.Chat, LastPM, the local @LEVELEFFECT / @TARGETDEAD / @light commands).
//   Lines     append-only (a ring of BUFFER parsed lines, at most MAX_DOM nodes; the log is rebuilt only on a tab or filter
//             change), repeated lines folded into xN, the sender parsed from the pre-formatted text as the classic does
//             ("Name: text", "(!)Name: text", whisper in "Name=>text", whisper out "/Name text"), "<Title/UniqueID>" item
//             links from ChatLog.chatItem (the item tooltip on hover), mentions of the user's name marked, lines fading
//             after the chosen time by a CSS animation (no timer), channel tags and timestamps optional.
//   Tabs      All / Local / Whispers / Group / Guild / System + one per whisper partner (closable), unread badges. The
//             [Filters] settings (gameSettings Filter*, shared with the classic chat options) hide a ChatType in every
//             tab (chatVisible); the [Chat] Show* settings show / hide the Local / Whispers / Group / Guild tabs and the
//             Shout / Lover / Mentor channels in the channel menu.
//   Input     opens on Enter / Space / '@' / '!' and '/' (last whisper) through the input router (handleKey on the
//             'hotkeys' layer), Esc closes. Channel pill: Say / Shout '!' / Group '!!' / Guild '!~' / Whisper '/name' /
//             Mentor '!#' / Lover ':)'; typing a prefix then a space switches it (PlayerObject.Chat's prefixes). Length
//             capped at Globals.MaxChatLength (80) including the prefix, history on Up / Down, whisper names completed
//             from friends, group members and recent partners. The input stops key propagation like the classic
//             MirTextBox; F1-F12 and Tab are re-dispatched to the router (ChatTextBox_KeyDown -> CMain_KeyDown).
//   Links     addLink / linkItem (MirItemCell Shift+right-click): "<Title> " + ChatLog.addLink, refused past the length.
//   Menu      a name's menu: Whisper, Invite to group (leader, group not full), Add friend, Inspect (only for a player in
//             view: C.Inspect needs an ObjectID), Copy name, Block / Unblock (friend list Blocked entries; their lines are
//             hidden client-side too).
// Settings (background, fade, text size, timestamps, tags, lock) and the box's place / size are per-viewer conveniences
// in localStorage 'crystal.ui.chat'.

import type { UserItem } from "../../../protocol/server.ts";
import { ChatType, MirGridType, ObjectType } from "../../../protocol/enums.generated.ts";
import { CHAT_LINK, MAX_CHAT_LENGTH, chatVisible } from "../../../game/chatLog.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { gameSettings, onSettingChange, setSetting, type GameSettings } from "../../../game/settings.ts";
import { isTextTarget } from "../../../game/input.ts";
import { matchesAnyBinding } from "../../../game/keybinds.ts";
import { MAX_GROUP } from "../../../game/group.ts";
import type { ItemLabelContext } from "../../itemLabel.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { h, injectCss, rafCoalesce, setText, later, reducedMotion } from "../core/dom.ts";
import { svg } from "../core/icons.ts";
import { fmtTimeOfDay } from "../core/format.ts";
import { segmented, toggleSwitch, chips } from "../core/widgets.ts";
import { toLocal } from "../core/layers.ts";
import { itemTooltip } from "../core/tooltip.ts";
import type { UiRuntime } from "../core/runtime.ts";

/** The chat's channels (ChatType grouped the way the tabs and colours use them). */
type Ch = "Normal" | "Shout" | "Group" | "Guild" | "Whisper" | "Mentor" | "Lover" | "System" | "Hint" | "Announcement" | "LevelUp";
/** Channels you can talk in: [channel, server prefix (PlayerObject.Chat), label, the Show* setting that offers it]. */
const SPEAK: readonly [Ch, string, string, keyof GameSettings | null][] = [
  ["Normal", "", "Say", null], ["Shout", "!", "Shout", "ShowYellChat"], ["Group", "!!", "Group", null], ["Guild", "!~", "Guild", null],
  ["Whisper", "/", "Whisper", null], ["Mentor", "!#", "Mentor", "ShowMentorChat"], ["Lover", ":)", "Lover", "ShowLoverChat"],
];
/** Channel tags shown before a line (empty = none). */
const TAG: Readonly<Record<Ch, string>> = {
  Normal: "", Shout: "Shout", Group: "Group", Guild: "Guild", Whisper: "", Mentor: "Mentor", Lover: "Lover", System: "System", Hint: "Hint",
  Announcement: "Notice", LevelUp: "Level",
};

/** ChatType -> channel. */
function channelOf(type: number): Ch {
  switch (type) {
    case ChatType.Shout: case ChatType.Shout2: case ChatType.Shout3: return "Shout";
    case ChatType.System: case ChatType.System2: case ChatType.Trainer: return "System";
    case ChatType.Hint: return "Hint";
    case ChatType.Announcement: case ChatType.LineMessage: return "Announcement";
    case ChatType.Group: return "Group";
    case ChatType.WhisperIn: case ChatType.WhisperOut: return "Whisper";
    case ChatType.Guild: return "Guild";
    case ChatType.LevelUp: return "LevelUp";
    case ChatType.Relationship: return "Lover";
    case ChatType.Mentor: return "Mentor";
  }
  return "Normal";
}

/** Parsed lines kept for re-rendering a tab. */
const BUFFER = 300;
/** Line nodes kept in the log. */
const MAX_DOM = 150;
/** Lines repeated within this time fold into xN. */
const FOLD_MS = 30_000;
const STORE = "crystal.ui.chat";

interface Line {
  readonly type: number; readonly ch: Ch; readonly text: string;
  /** Sender (or whisper partner) and where the message text starts after the sender part. */
  readonly from: string; readonly body: string; readonly whisper: "in" | "out" | null;
  readonly mention: boolean;
  t: number; n: number;
  el: HTMLElement | null; xEl: HTMLElement | null;
}
interface Tab {
  readonly key: string; name: string;
  /** null = every channel. */
  readonly chans: ReadonlySet<Ch> | null;
  readonly whisper?: string;
  /** The Show* setting that shows this tab. */
  readonly show?: keyof GameSettings;
  unread: number; ping: boolean;
}
interface Opts { op: number; fade: number; fs: number; time: boolean; tags: boolean; lock: boolean }
interface Place { left: number; bottom: number; w: number; h: number }

const DEFAULT_OPTS: Opts = { op: 0.55, fade: 20, fs: 12, time: false, tags: true, lock: false };
const DEFAULT_PLACE: Place = { left: 8, bottom: 186, w: 440, h: 196 };

/** The sender of a pre-formatted chat line (ChatDialog's click-to-whisper parse, made per channel). */
function parseLine(type: number, text: string): { from: string; body: string; whisper: "in" | "out" | null } {
  if (type === ChatType.WhisperIn) {
    const m = /^([^\s=]+)=>\s?/.exec(text); // "Name=>text" (PlayerObject.Chat)
    if (m) return { from: m[1]!, body: text.slice(m[0].length), whisper: "in" };
  } else if (type === ChatType.WhisperOut) {
    const m = /^\/(\S+)\s?/.exec(text); // "/Name text"
    if (m) return { from: m[1]!, body: text.slice(m[0].length), whisper: "out" };
  } else if (type !== ChatType.System && type !== ChatType.System2 && type !== ChatType.Hint && type !== ChatType.Announcement
    && type !== ChatType.LineMessage && type !== ChatType.LevelUp) {
    const m = /^(?:\(!\))?([^\s:<>]+?)\s?:\s?/.exec(text); // "Name: text", "(!)Name: text"
    if (m) return { from: m[1]!, body: text.slice(m[0].length), whisper: null };
  }
  return { from: "", body: text, whisper: type === ChatType.WhisperIn ? "in" : type === ChatType.WhisperOut ? "out" : null };
}

const CSS = `
.hudm .hchat { position: absolute; z-index: 3; pointer-events: auto; display: flex; flex-direction: column; color: var(--tx); border-radius: var(--rad); border: 1px solid transparent;
  --cbg: 0; background: color-mix(in srgb, var(--pb) calc(var(--cbg) * 100%), transparent); transition: background .3s, border-color .3s; }
.hudm .hchat.act { --cbg: var(--cop); border-color: color-mix(in srgb, var(--fr) 70%, transparent); box-shadow: 0 6px 24px rgba(0,0,0,.35); }
.hudm .hchat .ctop { display: flex; align-items: center; gap: 2px; padding: 3px 4px 0; opacity: 0; transition: opacity .3s; cursor: move; touch-action: none; }
.hudm .hchat.act .ctop, .hudm .hchat .ctop.ping { opacity: 1; }
.hudm .hchat.lock .ctop { cursor: default; }
.hudm .hchat .tstrip { position: relative; flex: 1; min-width: 0; display: flex; gap: 2px; overflow-x: auto; scrollbar-width: none; }
.hudm .hchat .ct { position: relative; flex: none; display: flex; align-items: center; gap: 4px; height: 20px; padding: 0 7px; cursor: pointer; user-select: none; font-size: 11px; font-weight: 600;
  border-radius: calc(var(--rad) * .7) calc(var(--rad) * .7) 0 0; color: var(--dim); text-shadow: 0 1px 2px #000; }
.hudm .hchat .ct:hover { color: var(--tx); }
.hudm .hchat .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 22%, transparent); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .hchat .ct.ping { color: #fff; background: color-mix(in srgb, var(--ac) 38%, transparent); }
.hudm .hchat .ct b { min-width: 14px; height: 14px; padding: 0 3px; border-radius: 7px; font-size: 9px; display: flex; align-items: center; justify-content: center; color: #fff; background: var(--ac); }
.hudm .hchat .ct b:empty { display: none; }
.hudm .hchat .ct i { display: none; font-style: normal; color: var(--dim); margin-left: 2px; } .hudm .hchat .ct:hover i { display: inline; } .hudm .hchat .ct i:hover { color: var(--bad-soft); }
.hudm .hchat .ib { width: 22px; height: 20px; display: flex; align-items: center; justify-content: center; cursor: pointer; color: var(--dim); border-radius: 4px; flex: none; }
.hudm .hchat .ib:hover, .hudm .hchat .ib.on { color: #fff; background: color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .hchat .ib svg.ic { width: 13px; height: 13px; }
.hudm .hchat .log { position: relative; flex: 1; min-height: 0; overflow-y: auto; overflow-x: hidden; padding: 3px 8px 4px; scrollbar-width: none;
  mask-image: linear-gradient(180deg, transparent, #000 14px); }
.hudm .hchat.act .log { scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hchat .ln { position: relative; padding: 1px 4px; margin: 0 -4px; border-radius: 3px; line-height: 1.35; font-size: var(--cfs, 12px); text-shadow: 0 1px 2px #000, 0 0 3px #000; overflow-wrap: anywhere; }
.hudm .hchat:not(.act):not(.nofade) .ln { animation: hchat-fade 1.2s ease var(--fade, 20s) forwards; }
.hudm .hchat:not(.act):not(.nofade) .ln.old { animation: none; opacity: 0; }
@keyframes hchat-fade { to { opacity: 0; } }
@media (prefers-reduced-motion: reduce) { .hudm .hchat:not(.act):not(.nofade) .ln { animation: none !important; } }
.hudm .hchat .ln:hover { background: rgba(255,255,255,.05); }
.hudm .hchat .ln.mention { background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 2px 0 0 var(--ac); }
.hudm .hchat .ln .tm { font-size: .82em; color: var(--dim); margin-right: 4px; font-variant-numeric: tabular-nums; }
.hudm .hchat .ln .tg { font-size: .78em; font-weight: 700; margin-right: 4px; opacity: .9; }
.hudm .hchat .ln .nm { font-weight: 700; cursor: pointer; } .hudm .hchat .ln .nm:hover { text-decoration: underline; }
.hudm .hchat .ln .x { margin-left: 5px; font-size: .78em; font-weight: 800; padding: 0 4px; border-radius: 6px; color: #000; background: var(--dim); }
.hudm .hchat .ln.sys { font-style: italic; }
.hudm .hchat .lk { cursor: pointer; font-weight: 700; color: var(--gold); } .hudm .hchat .lk:hover { text-decoration: underline; }
.hudm .hchat .c-normal { color: #eef2fa; } .hudm .hchat .c-shout { color: #ffd84a; } .hudm .hchat .c-group { color: #8ad4ff; } .hudm .hchat .c-guild { color: #6fe08a; }
.hudm .hchat .c-whisper { color: #d9a6ff; } .hudm .hchat .c-mentor { color: #c9a0ff; } .hudm .hchat .c-lover { color: #ff9cc4; } .hudm .hchat .c-system { color: #ff7a6a; }
.hudm .hchat .c-hint { color: #8fd8a8; } .hudm .hchat .c-announcement { color: #7fb0ff; } .hudm .hchat .c-levelup { color: #f2c94c; }
.hudm .hchat .newp { position: absolute; left: 50%; bottom: 40px; translate: -50% 0; z-index: 2; padding: 2px 10px; border-radius: 10px; cursor: pointer; font-size: 10px; font-weight: 700; color: #fff;
  background: var(--ac); box-shadow: 0 0 10px var(--glow); }
.hudm .hchat .cin { display: none; align-items: center; gap: 5px; padding: 4px; border-top: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .hchat.typing .cin { display: flex; }
.hudm .hchat .hint { padding: 2px 8px 5px; font-size: 10px; color: var(--dim); text-shadow: 0 1px 2px #000; opacity: 0; transition: opacity .3s; }
.hudm .hchat.act:not(.typing) .hint { opacity: 1; }
.hudm .hchat.typing .hint { display: none; }
.hudm .hchat .pill { flex: none; height: 22px; padding: 0 8px; display: flex; align-items: center; gap: 4px; cursor: pointer; user-select: none; font-size: 11px; font-weight: 700;
  border-radius: 11px; border: 1px solid currentColor; background: #05070cd0; }
.hudm .hchat .cin input { flex: 1; min-width: 0; height: 24px; padding: 0 8px; font-size: 12px; border-radius: calc(var(--rad) * .7); border: 1px solid var(--fr); background: #05070cd8; color: #fff; outline: none; }
.hudm .hchat .cin input:focus { border-color: var(--ac); }
.hudm .hchat .cnt { font-size: 9px; color: var(--dim); width: 34px; text-align: right; font-variant-numeric: tabular-nums; } .hudm .hchat .cnt.hi { color: var(--bad-soft); }
.hudm .hchat .pop { position: absolute; z-index: 5; min-width: 150px; max-width: 260px; padding: 4px; border-radius: var(--rad); background: color-mix(in srgb, var(--pb) 96%, #000);
  border: 1px solid var(--fr); box-shadow: 0 8px 24px rgba(0,0,0,.7); }
.hudm .hchat .pop .it { display: flex; align-items: center; gap: 8px; padding: 4px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; white-space: nowrap; }
.hudm .hchat .pop .it:hover, .hudm .hchat .pop .it.on { background: color-mix(in srgb, var(--ac) 25%, transparent); color: #fff; }
.hudm .hchat .pop .it small { color: var(--dim); font-size: 10px; margin-left: auto; }
.hudm .hchat .pop .it.bad { color: var(--bad-soft); }
.hudm .hchat .pop .hd { padding: 3px 8px; font-size: 9px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hchat .pop .sep { height: 1px; margin: 3px 4px; background: var(--fr); }
.hudm .hchat .pop.set { width: 260px; max-width: none; padding: 8px 10px; display: flex; flex-direction: column; gap: 7px; font-size: 11px; }
.hudm .hchat .pop.set .r { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.hudm .hchat .pop.set input[type=range] { width: 110px; accent-color: var(--ac); }
.hudm .hchat .grip { position: absolute; right: -1px; top: -1px; width: 14px; height: 14px; cursor: nesw-resize; opacity: 0; touch-action: none;
  border-top: 2px solid var(--ac); border-right: 2px solid var(--ac); border-radius: 0 var(--rad) 0 0; }
.hudm .hchat.act:not(.lock) .grip { opacity: .8; }
/* Announcement ticker (ChatNoticeDialog) */
.hudm .cnotice { position: absolute; z-index: 4; left: 50%; top: 10px; translate: -50% 0; width: min(560px, 90%); height: 26px; overflow: hidden; pointer-events: none; border-radius: 13px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--pb) 85%, transparent) 15%, color-mix(in srgb, var(--pb) 85%, transparent) 85%, transparent);
  mask-image: linear-gradient(90deg, transparent, #000 12%, #000 88%, transparent); }
.hudm .cnotice span { position: absolute; left: 0; top: 4px; white-space: nowrap; font-size: 13px; font-weight: 700; color: #fff; text-shadow: 0 0 8px var(--glow), 0 1px 2px #000;
  will-change: transform; animation: hchat-tick var(--dur) linear forwards; }
.hudm .cnotice span.still { left: 50%; translate: -50% 0; animation: none; }
.hudm .cnotice span b { color: var(--ac); margin-right: 8px; }
@keyframes hchat-tick { from { transform: translateX(var(--from, 560px)); } to { transform: translateX(-100%); } }
`;

function load(): { opts: Opts; place: Place } {
  const out = { opts: { ...DEFAULT_OPTS }, place: { ...DEFAULT_PLACE } };
  try {
    const raw = localStorage.getItem(STORE);
    const saved = raw ? (JSON.parse(raw) as { opts?: Partial<Opts>; place?: Partial<Place> }) : null;
    for (const k of Object.keys(out.opts) as (keyof Opts)[]) if (typeof saved?.opts?.[k] === typeof out.opts[k]) (out.opts as unknown as Record<string, unknown>)[k] = saved!.opts![k];
    for (const k of Object.keys(out.place) as (keyof Place)[]) if (typeof saved?.place?.[k] === "number" && Number.isFinite(saved.place[k])) out.place[k] = saved.place[k]!;
  } catch { /* storage blocked / bad data: defaults */ }
  return out;
}

export interface ChatHudOptions {
  /** The item label context for chat-link tooltips (the integrator's ItemLabelContext). Without it links show no tooltip. */
  labelContext?: () => ItemLabelContext;
}

export class ChatHud {
  readonly el: HTMLElement;
  /** The announcement ticker (top centre of the windows layer). */
  readonly notice: HTMLElement;
  private readonly lines: Line[] = [];
  private readonly tabs: Tab[] = [];
  private tab = 0;
  private ch: Ch = "Normal";
  private target = "";
  private readonly hist: string[] = [];
  private histAt = -1;
  private readonly partners: string[] = [];
  private blocked = new Set<string>();
  private readonly opts: Opts;
  private readonly place: Place;
  private stick = true;
  /** When the player last scrolled the log by hand (wheel, touch, drag, keys): only such a scroll stops the follow,
   *  not one caused by a resize (the phone keyboard, the mobile chat sheet opening / closing). */
  private userScrollAt = -1e9;
  private unseen = 0;
  private hover = false;
  private readonly log: HTMLElement;
  private readonly tabBar: HTMLElement;
  private readonly tabStrip: HTMLElement;
  private readonly input: HTMLInputElement;
  private readonly pill: HTMLElement;
  private readonly cnt: HTMLElement;
  private readonly newp: HTMLElement;
  private pop: HTMLElement | null = null;
  private popSel = 0;
  private popItems: [HTMLElement, () => void][] = [];
  private pingOff: (() => void) | null = null;
  private readonly tabEls = new Map<string, { el: HTMLElement; badge: HTMLElement }>();
  private readonly scrollEnd: () => void;
  private readonly signal: AbortSignal;

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext, private readonly o: ChatHudOptions = {}) {
    injectCss("hudm-chat-css", CSS);
    this.signal = rt.signal;
    const saved = load();
    this.opts = saved.opts;
    this.place = saved.place;
    const sig = { signal: this.signal };

    const mk = (key: string, name: string, chans: Ch[] | null, show?: keyof GameSettings): Tab =>
      ({ key, name, chans: chans ? new Set(chans) : null, show, unread: 0, ping: false });
    this.tabs.push(mk("all", "All", null), mk("local", "Local", ["Normal", "Shout", "System", "Hint", "Announcement", "LevelUp"], "ShowNormalChat"),
      mk("whisper", "Whispers", ["Whisper", "Lover", "Mentor"], "ShowWhisperChat"), mk("group", "Group", ["Group", "System"], "ShowGroupChat"),
      mk("guild", "Guild", ["Guild", "Announcement"], "ShowGuildChat"), mk("system", "System", ["System", "Hint", "Announcement", "LevelUp"]));

    // — Structure —
    this.tabStrip = h("div", "tstrip");
    this.tabStrip.addEventListener("wheel", (e) => { this.tabStrip.scrollLeft += e.deltaY; e.preventDefault(); }, { passive: false, signal: this.signal });
    const trade = this.iconButton("swap", "Trade with the player you face", () => ctx.models.trade.request());
    const lock = this.iconButton(this.opts.lock ? "lock" : "unlock", "", () => {
      this.opts.lock = !this.opts.lock;
      this.save(); this.applyOpts();
      lock.replaceChildren(svg(this.opts.lock ? "lock" : "unlock"));
    });
    const gear = this.iconButton("gear", "Chat settings", () => this.settings());
    this.tabBar = h("div", "ctop", this.tabStrip, trade, lock, gear);
    this.log = h("div", "log");
    this.log.addEventListener("scroll", () => {
      const atEnd = this.log.scrollHeight - this.log.scrollTop - this.log.clientHeight < 6;
      if (atEnd && !this.stick) { this.stick = true; this.unseen = 0; this.renderNewPill(); }
      else if (!atEnd && performance.now() - this.userScrollAt < 1000) this.stick = false;
    }, { passive: true, signal: this.signal });
    const byHand = () => { this.userScrollAt = performance.now(); };
    for (const ev of ["wheel", "pointerdown", "touchmove", "keydown"]) this.log.addEventListener(ev, byHand, { passive: true, signal: this.signal });
    // A resize keeps a following log at its end (the scroll position alone would leave it short)
    const ro = new ResizeObserver(() => { if (this.stick) this.scrollEnd(); });
    ro.observe(this.log);
    this.signal.addEventListener("abort", () => ro.disconnect());
    this.newp = h("div", "newp");
    this.newp.hidden = true;
    this.newp.addEventListener("click", () => { this.stick = true; this.unseen = 0; this.scrollEnd(); this.renderNewPill(); }, sig);
    this.pill = h("div", "pill");
    this.pill.addEventListener("mousedown", (e) => e.preventDefault(), sig);
    this.pill.addEventListener("click", (e) => { e.stopPropagation(); if (this.pop?.dataset.kind === "menu") this.closePop(); else this.channelMenu(); }, sig);
    this.input = h("input");
    this.input.autocomplete = "off";
    this.input.spellcheck = false;
    this.input.setAttribute("aria-label", "Chat message");
    this.input.addEventListener("keydown", (e) => this.onKey(e), sig);
    this.input.addEventListener("input", () => this.onInput(), sig);
    this.input.addEventListener("blur", () => later(() => {
      if (document.activeElement !== this.input && !this.pop && !this.input.value) this.el.classList.remove("typing");
      this.refreshAct();
    }, 150, this.signal), sig);
    const send = this.iconButton("send", "Send (Enter)", () => this.send());
    this.cnt = h("div", "cnt");
    const grip = h("div", "grip");
    grip.title = "Resize";
    this.el = h("div", "hchat snap", this.tabBar, this.log, this.newp, h("div", "cin", this.pill, this.input, this.cnt, send),
      h("div", "hint", "Enter to chat · right-click a name for options"), grip);
    this.el.addEventListener("pointerenter", () => { this.hover = true; this.refreshAct(); }, sig);
    this.el.addEventListener("pointerleave", () => { this.hover = false; this.refreshAct(); }, sig);
    this.dragBy(this.tabBar, false);
    this.dragBy(grip, true);
    this.notice = h("div", "cnotice");
    this.notice.hidden = true;
    this.scrollEnd = rafCoalesce(() => { this.log.scrollTop = this.log.scrollHeight; }, this.signal);

    rt.layers.wins.append(this.el, this.notice);
    this.applyOpts();
    this.applyPlace();
    this.updatePill();

    // — Models —
    const m = ctx.models;
    this.readBlocked();
    for (const msg of m.chatLog.messages.slice(-BUFFER)) this.push(msg.text, msg.type, false);
    this.renderTabs();
    this.renderLog();
    const offs = [
      m.chatLog.added.on((msg) => this.add(msg.text, msg.type)),
      m.chatLog.announcement.on((text) => this.ticker(text)),
      m.friends.changed.on(() => { if (this.readBlocked()) this.renderLog(); }),
      onSettingChange((key) => {
        if (key.startsWith("Filter")) this.renderLog();
        else if (key.startsWith("Show")) { this.renderTabs(); this.updatePill(); }
      }),
      rt.layoutChanged.on(() => this.applyPlace()),
    ];
    // Outside clicks close a popover (any pointerdown in the page, the game canvas included).
    document.addEventListener("pointerdown", (e) => {
      const t = e.target as HTMLElement;
      if (this.pop && !this.pop.contains(t) && !(this.el.contains(t) && t.closest(".ib, .pill"))) this.closePop();
    }, sig);
    this.signal.addEventListener("abort", () => { for (const off of offs) off(); this.pingOff?.(); }, { once: true });
  }

  // — GameUi surface —

  /** ChatDialog: the input has the focus (game keys and CheckInput stop). */
  get typing(): boolean { return document.activeElement === this.input; }

  /** GameScene map click: the input loses the focus (it closes when empty). */
  blur(): void { this.input.blur(); }

  /** Back to following the newest line (the mobile chat sheet opening / closing). */
  follow(): void { this.stick = true; this.unseen = 0; this.scrollEnd(); this.renderNewPill(); }

  /** True when `name` is on the block list (its lines are hidden). */
  isBlocked(name: string): boolean { return this.blocked.has(name); }

  /** The input element (the mobile chat sheet tells a tap on it from one on the log). */
  get inputEl(): HTMLInputElement { return this.input; }

  /** Opens the input (Enter). */
  focus(): void {
    this.el.classList.add("typing", "act");
    this.input.focus();
    const n = this.input.value.length;
    this.input.setSelectionRange(n, n);
    this.updatePill();
  }

  /** ChatDialog.SetChatText: opens the input with `text` ("/name " starts a whisper, a bare prefix picks its channel). */
  prefill(text: string): void {
    const w = /^\/(\S+)\s*$/.exec(text);
    if (w) { this.setChannel("Whisper", w[1]!); this.input.value = ""; }
    else {
      const sp = SPEAK.find(([, pre]) => pre && pre !== "/" && text.trimEnd() === pre);
      if (sp) { this.setChannel(sp[0]); this.input.value = ""; }
      else this.input.value = text.slice(0, this.maxLen());
    }
    this.focus();
  }

  /** MirItemCell Shift+right-click: "<Title> " + ChatDialog.LinkedItems. False when the message would be too long. */
  addLink(uniqueId: bigint, title: string, grid: number): boolean {
    const text = `<${title}> `;
    const v = this.input.value;
    const sep = v && !v.endsWith(" ") ? " " : "";
    if (v.length + sep.length + text.length > this.maxLen()) return false;
    this.ctx.models.chatLog.addLink({ UniqueID: uniqueId, Title: title, Grid: grid });
    this.input.value = v + sep + text;
    this.focus();
    return true;
  }

  /** The drag controller's onLink hook: links a bag item with its friendly name (classic inventoryWindow.onLink). */
  linkItem(item: UserItem, grid: number = MirGridType.Inventory): void {
    const info = this.ctx.models.itemInfos.get(item.ItemIndex);
    if (!info) return;
    const title = itemFriendlyName(info) + (item.Count > 1 ? ` (${item.Count})` : "");
    if (!this.addLink(item.UniqueID, title, grid)) this.system("Unable to link item, message exceeds allowed length");
  }

  /** Posts a local system line (through the chat log, like every other line). */
  system(text: string): void { this.ctx.models.chatLog.receive(text, ChatType.System); }

  /** ChatPanel_KeyPress / KeyDown for the input router ('hotkeys' layer): Enter / Space / '@' / '!' open the input,
   *  '/' opens a whisper to the last partner (LastPM), PageUp / PageDown / End scroll the log. True = consumed. */
  handleKey(e: KeyboardEvent): boolean {
    if (e.ctrlKey || e.altKey || e.metaKey || isTextTarget(e) || this.root().hidden) return false;
    switch (e.key) {
      case "Enter": case " ": case "@": case "!":
        e.preventDefault();
        if (e.key === "@" || e.key === "!") this.input.value = e.key;
        this.focus();
        return true;
      case "/": {
        e.preventDefault();
        const last = this.ctx.models.chatLog.lastPM;
        if (last.length > 1) this.prefill(last + " "); else { this.input.value = "/"; this.focus(); this.suggest(); }
        return true;
      }
    }
    if (matchesAnyBinding(e)) return false;
    switch (e.key) {
      case "PageUp": this.userScrollAt = performance.now(); this.log.scrollTop -= this.log.clientHeight * 0.8; break;
      case "PageDown": this.userScrollAt = performance.now(); this.log.scrollTop += this.log.clientHeight * 0.8; break;
      case "End": this.stick = true; this.unseen = 0; this.scrollEnd(); this.renderNewPill(); break;
      default: return false;
    }
    e.preventDefault();
    return true;
  }

  /** Removes the elements (the runtime's signal also stops every listener). */
  dispose(): void {
    this.closePop();
    this.el.remove();
    this.notice.remove();
  }

  // — Lines —

  private root(): HTMLElement { return this.rt.root; }

  private me(): string { return this.ctx.player.user?.name ?? ""; }

  /** Parses and keeps a line (folding repeats). Returns it, or null when folded into the last line. */
  private push(text: string, type: number, live: boolean): Line | null {
    const now = live ? Date.now() : Date.now() - 3_600_000; // lines from before the box existed count as old
    const last = this.lines[this.lines.length - 1];
    if (last && last.type === type && last.text === text && now - last.t < FOLD_MS) {
      last.n++; last.t = now;
      if (last.el) {
        if (!last.xEl) { last.xEl = h("span", "x"); last.el.append(last.xEl); }
        setText(last.xEl, "×" + last.n);
      }
      return null;
    }
    const p = parseLine(type, text);
    const me = this.me();
    const mention = !!me && !!p.from && p.from !== me && p.whisper !== "out" && text.toLowerCase().includes(me.toLowerCase());
    const l: Line = { type, ch: channelOf(type), text, from: p.from, body: p.body, whisper: p.whisper, mention, t: now, n: 1, el: null, xEl: null };
    this.lines.push(l);
    if (this.lines.length > BUFFER) this.lines.shift();
    if (l.whisper && l.from) this.notePartner(l.from, live);
    return l;
  }

  /** A whisper partner: recent list (autocomplete) and a tab of its own. */
  private notePartner(name: string, live: boolean): void {
    const i = this.partners.indexOf(name);
    if (i >= 0) this.partners.splice(i, 1);
    this.partners.unshift(name);
    if (this.partners.length > 12) this.partners.pop();
    if (!this.tabs.some((t) => t.whisper === name)) {
      this.tabs.push({ key: "w:" + name, name, chans: new Set<Ch>(["Whisper"]), whisper: name, unread: 0, ping: false });
      if (live) this.renderTabs();
    }
  }

  private shows(tab: Tab, l: Line): boolean {
    if (!chatVisible(l.type)) return false;
    if (l.from && l.whisper !== "out" && this.blocked.has(l.from)) return false;
    if (tab.whisper) return l.ch === "Whisper" && l.from === tab.whisper;
    return !tab.chans || tab.chans.has(l.ch);
  }

  /** A new message from the model. */
  private add(text: string, type: number): void {
    const l = this.push(text, type, true);
    if (!l) return;
    const cur = this.tabs[this.tab]!;
    const own = l.whisper === "out" || (!!l.from && l.from === this.me());
    this.tabs.forEach((t, i) => {
      if (i === this.tab || own || !this.shows(t, l) || !this.tabVisible(t)) return;
      t.unread++;
      if (l.mention || (l.whisper === "in" && t.whisper)) t.ping = true;
      this.renderBadge(t);
    });
    if (this.shows(cur, l)) {
      this.appendLine(l);
      if (this.stick) this.scrollEnd();
      else { this.unseen++; this.renderNewPill(); }
    }
    if (l.mention) {
      this.tabBar.classList.add("ping");
      this.pingOff?.();
      this.pingOff = later(() => this.tabBar.classList.remove("ping"), 4000, this.signal);
    }
  }

  private appendLine(l: Line): void {
    const e = this.lineEl(l);
    this.log.append(e);
    while (this.log.childElementCount > MAX_DOM) {
      const first = this.log.firstElementChild!;
      first.remove();
    }
  }

  /** Rebuilds the log for the current tab (tab switch, filter / block / option change). */
  private renderLog(): void {
    const tab = this.tabs[this.tab]!;
    const cut = Date.now() - this.opts.fade * 1000;
    const nodes: HTMLElement[] = [];
    for (let i = this.lines.length - 1; i >= 0 && nodes.length < MAX_DOM; i--) {
      const l = this.lines[i]!;
      if (!this.shows(tab, l)) { l.el = null; continue; }
      const e = this.lineEl(l);
      if (this.opts.fade && l.t < cut) e.classList.add("old");
      nodes.push(e);
    }
    nodes.reverse();
    this.log.replaceChildren(...nodes);
    this.stick = true;
    this.unseen = 0;
    this.renderNewPill();
    this.scrollEnd();
  }

  private lineEl(l: Line): HTMLElement {
    const e = h("div", "ln c-" + l.ch.toLowerCase() + (l.from || l.whisper ? "" : " sys") + (l.mention ? " mention" : ""));
    if (this.opts.time) e.append(h("span", "tm", fmtTimeOfDay(l.t)));
    if (l.whisper && l.from) e.append(h("span", "tg", l.whisper === "out" ? "To" : "From"), this.nameEl(l.from), ": ");
    else {
      if (this.opts.tags && TAG[l.ch]) e.append(h("span", "tg", `[${TAG[l.ch]}]`));
      if (l.from) e.append(this.nameEl(l.from), ": ");
    }
    this.rich(e, l.from || l.whisper ? l.body : l.text);
    l.xEl = l.n > 1 ? h("span", "x", "×" + l.n) : null;
    if (l.xEl) e.append(l.xEl);
    l.el = e;
    return e;
  }

  /** The text with "<Title/UniqueID>" item links (ChatDialog.ChatLink). */
  private rich(e: HTMLElement, text: string): void {
    let last = 0;
    for (const m of text.matchAll(CHAT_LINK)) {
      if (m.index! > last) e.append(text.slice(last, m.index));
      const item = this.ctx.models.chatLog.chatItem(m[2]!);
      if (item) e.append(this.linkEl(m[1]!, item));
      else e.append(m[1]!);
      last = m.index! + m[0].length;
    }
    if (last < text.length) e.append(text.slice(last));
  }

  private linkEl(title: string, item: UserItem): HTMLElement {
    const a = h("span", "lk", title);
    const show = () => {
      const lc = this.o.labelContext?.();
      if (lc) itemTooltip(this.rt, this.ctx.models.gameInfo).show(a, item, lc);
    };
    a.addEventListener("pointerenter", show);
    a.addEventListener("pointerleave", () => itemTooltip(this.rt, this.ctx.models.gameInfo).hide());
    a.addEventListener("click", (ev) => { ev.stopPropagation(); show(); });
    return a;
  }

  private nameEl(n: string): HTMLElement {
    const e = h("span", "nm", n);
    const open = (ev: MouseEvent) => { ev.preventDefault(); ev.stopPropagation(); this.nameMenu(n, e); };
    e.addEventListener("click", open);
    e.addEventListener("contextmenu", open);
    return e;
  }

  private renderNewPill(): void {
    this.newp.hidden = !this.unseen || this.stick;
    if (!this.newp.hidden) setText(this.newp, `${this.unseen} new message${this.unseen === 1 ? "" : "s"} ↓`);
  }

  // — Tabs —

  private tabVisible(t: Tab): boolean { return !t.show || gameSettings[t.show] !== false; }

  private renderTabs(): void {
    if (!this.tabVisible(this.tabs[this.tab]!)) this.tab = 0;
    this.tabEls.clear();
    const nodes: HTMLElement[] = [];
    this.tabs.forEach((t, i) => {
      if (!this.tabVisible(t)) return;
      const badge = h("b");
      const e = h("div", "ct" + (i === this.tab ? " on" : ""), t.whisper ? "✉ " + t.name : t.name, badge);
      if (t.whisper) {
        const x = h("i", "", "×");
        x.title = "Close tab";
        x.addEventListener("click", (ev) => {
          ev.stopPropagation();
          const at = this.tabs.indexOf(t);
          this.tabs.splice(at, 1);
          if (this.tab >= at) this.tab = Math.max(0, this.tab - 1);
          this.renderTabs(); this.renderLog();
        });
        e.append(x);
      }
      e.addEventListener("click", () => this.selectTab(this.tabs.indexOf(t)));
      this.tabEls.set(t.key, { el: e, badge });
      this.renderBadge(t);
      nodes.push(e);
    });
    this.tabStrip.replaceChildren(...nodes);
  }

  private renderBadge(t: Tab): void {
    const r = this.tabEls.get(t.key);
    if (!r) return;
    const cur = this.tabs[this.tab] === t;
    setText(r.badge, !cur && t.unread ? (t.unread > 99 ? "99+" : String(t.unread)) : "");
    r.el.classList.toggle("ping", !cur && t.ping);
  }

  private selectTab(i: number): void {
    const t = this.tabs[i];
    if (!t) return;
    this.tab = i; t.unread = 0; t.ping = false;
    if (t.whisper) this.setChannel("Whisper", t.whisper);
    this.renderTabs(); this.renderLog();
  }

  // — Input —

  private setChannel(ch: Ch, target = ""): void {
    this.ch = ch;
    this.target = ch === "Whisper" ? target || this.target : "";
    this.updatePill();
  }

  /** Globals.MaxChatLength less the channel prefix the send adds. */
  private maxLen(): number {
    const pre = this.ch === "Whisper" ? `/${this.target} ` : SPEAK.find((s) => s[0] === this.ch)?.[1] ?? "";
    return Math.max(1, MAX_CHAT_LENGTH - pre.length);
  }

  private updatePill(): void {
    const sp = SPEAK.find((s) => s[0] === this.ch) ?? SPEAK[0]!;
    if (sp[3] && !gameSettings[sp[3]]) { this.ch = "Normal"; this.target = ""; }
    const label = this.ch === "Whisper" ? `To ${this.target || "…"}` : (SPEAK.find((s) => s[0] === this.ch) ?? SPEAK[0]!)[2];
    setText(this.pill, label);
    this.pill.className = "pill c-" + this.ch.toLowerCase();
    this.input.placeholder = this.ch === "Whisper" ? `Whisper to ${this.target || "(type /name then space)"}` : this.ch === "Normal" ? "Say something…" : `Message your ${label.toLowerCase()}…`;
    const max = this.maxLen();
    this.input.maxLength = max;
    if (this.input.value.length > max) this.input.value = this.input.value.slice(0, max);
    setText(this.cnt, `${this.input.value.length}/${max}`);
    this.cnt.classList.toggle("hi", this.input.value.length > max - 10);
  }

  private onInput(): void {
    const v = this.input.value;
    // A prefix then a space switches the channel pill (PlayerObject.Chat's prefixes).
    const w = /^\/(\S+) $/.exec(v);
    if (w) { this.setChannel("Whisper", w[1]!); this.input.value = ""; }
    else for (const [ch, pre, , show] of SPEAK) {
      if (pre && pre !== "/" && v === pre + " " && (!show || gameSettings[show])) { this.setChannel(ch); this.input.value = ""; break; }
    }
    this.updatePill();
    this.suggest();
  }

  /** "/na" completes whisper names from friends (online first), the group and recent partners (no other presence data). */
  private suggest(): void {
    const v = this.input.value;
    if (!/^\/\S*$/.test(v)) { if (this.pop?.dataset.kind === "ac") this.closePop(); return; }
    const q = v.slice(1).toLowerCase();
    const seen = new Set<string>();
    const out: [string, string][] = [];
    const me = this.me();
    const addName = (n: string, note: string) => { if (n && n !== me && !seen.has(n) && n.toLowerCase().startsWith(q)) { seen.add(n); out.push([n, note]); } };
    const friends = [...this.ctx.models.friends.list].filter((f) => !f.Blocked).sort((a, b) => Number(b.Online) - Number(a.Online));
    for (const f of friends) addName(f.Name, f.Online ? "online" : "offline");
    for (const n of this.ctx.models.group.list) addName(n, "group");
    for (const n of this.partners) addName(n, "recent");
    if (!out.length) { if (this.pop?.dataset.kind === "ac") this.closePop(); return; }
    this.openPop("Whisper to", out.slice(0, 8).map(([n, note]) => [h("div", "it", n, h("small", "", note)), () => {
      this.setChannel("Whisper", n); this.input.value = ""; this.closePop(); this.updatePill(); this.input.focus();
    }]), this.input, "ac");
  }

  /** The input's own keys: stops propagation (game keys do not fire while typing) except F1-F12 / Tab, which go on to
   *  the router (ChatTextBox_KeyDown -> CMain_KeyDown). */
  private onKey(e: KeyboardEvent): void {
    if (/^F([1-9]|1[0-2])$/.test(e.key) || e.key === "Tab") {
      if (this.pop?.dataset.kind === "ac" && e.key === "Tab" && this.popItems.length) { e.preventDefault(); e.stopPropagation(); this.popItems[this.popSel]![1](); return; }
      e.preventDefault();
      e.stopPropagation();
      window.dispatchEvent(new KeyboardEvent("keydown", e));
      return;
    }
    e.stopPropagation();
    if (this.pop && this.popItems.length && (e.key === "ArrowUp" || e.key === "ArrowDown" || (e.key === "Enter" && this.pop.dataset.kind === "ac"))) {
      e.preventDefault();
      if (e.key === "Enter") { this.popItems[this.popSel]![1](); return; }
      this.popSel = (this.popSel + (e.key === "ArrowUp" ? this.popItems.length - 1 : 1)) % this.popItems.length;
      this.popItems.forEach(([el], i) => el.classList.toggle("on", i === this.popSel));
      return;
    }
    switch (e.key) {
      case "Escape":
        e.preventDefault();
        if (this.pop) { this.closePop(); return; }
        this.close();
        return;
      case "Enter": e.preventDefault(); this.send(); return;
      case "ArrowUp": case "ArrowDown": {
        if (!this.hist.length) return;
        e.preventDefault();
        const up = e.key === "ArrowUp";
        this.histAt = up ? (this.histAt < 0 ? this.hist.length - 1 : Math.max(0, this.histAt - 1)) : this.histAt < 0 ? -1 : this.histAt + 1;
        if (this.histAt >= this.hist.length) this.histAt = -1;
        this.input.value = this.histAt < 0 ? "" : this.hist[this.histAt]!;
        this.updatePill();
        return;
      }
      case "Backspace":
        if (!this.input.value && this.ch !== "Normal") { this.setChannel("Normal"); }
        return;
    }
  }

  /** Closes the input (cancel or after a send): ChatDialog.LinkedItems.Clear. */
  private close(): void {
    this.input.value = "";
    this.ctx.models.chatLog.clearLinks();
    this.closePop();
    this.input.blur();
    this.el.classList.remove("typing");
    this.updatePill();
    this.refreshAct();
  }

  /** ChatTextBox Enter: the channel prefix + text through ChatLog.submit (C.Chat, local commands, LastPM). */
  private send(): void {
    const text = this.input.value.trim();
    if (!text) { this.close(); return; }
    this.hist.push(text);
    if (this.hist.length > 30) this.hist.shift();
    this.histAt = -1;
    let msg: string;
    if (this.ch === "Normal" || text.startsWith("@")) msg = text;
    else if (this.ch === "Whisper") {
      if (!this.target) { this.system("Type /name and a space to pick who to whisper."); return; }
      msg = `/${this.target} ${text}`;
    } else msg = (SPEAK.find((s) => s[0] === this.ch)?.[1] ?? "") + text;
    this.ctx.models.chatLog.submit(msg.slice(0, MAX_CHAT_LENGTH));
    this.stick = true; this.unseen = 0; this.renderNewPill();
    this.close();
  }

  // — Popovers (inside the chat box; text and nodes only) —

  private openPop(head: string, items: [HTMLElement, () => void][], anchor: HTMLElement, kind: string): void {
    this.closePop();
    this.popItems = items; this.popSel = 0;
    const p = h("div", "pop");
    p.dataset.kind = kind;
    if (head) p.append(h("div", "hd", head));
    items.forEach(([it, fn], i) => {
      if (kind === "ac" && i === 0) it.classList.add("on");
      it.addEventListener("mousedown", (e) => e.preventDefault());
      it.addEventListener("click", (e) => { e.stopPropagation(); fn(); });
      p.append(it);
    });
    this.el.append(p);
    this.pop = p;
    // Measured once: the anchor's place in the box (the layer is scaled; toLocal divides it out).
    const [ax] = toLocal(this.el, anchor.getBoundingClientRect().left, 0);
    const [, ay] = toLocal(this.el, 0, anchor.getBoundingClientRect().top);
    p.style.left = Math.max(0, Math.min(ax, this.el.offsetWidth - 150)) + "px";
    p.style.bottom = Math.max(0, this.el.offsetHeight - ay + 4) + "px";
    this.refreshAct();
  }

  private closePop(): void {
    this.pop?.remove();
    this.pop = null;
    this.popItems = [];
    this.refreshAct();
  }

  private item(label: string | (Node | string)[], fn: () => void, cls = ""): [HTMLElement, () => void] {
    return [h("div", "it" + (cls ? " " + cls : ""), ...(typeof label === "string" ? [label] : label)), fn];
  }

  private channelMenu(): void {
    const items: [HTMLElement, () => void][] = [];
    for (const [ch, pre, label, show] of SPEAK) {
      if (show && !gameSettings[show]) continue;
      const dot = h("span", "c-" + ch.toLowerCase(), "●");
      items.push(this.item([dot, ` ${label}`, h("small", "", pre ? pre + (ch === "Whisper" ? "name" : "") : "no prefix")], () => {
        this.closePop();
        this.setChannel(ch);
        this.input.focus();
        if (ch === "Whisper" && !this.target) { this.input.value = "/"; this.suggest(); }
      }));
    }
    this.openPop("Talk in", items, this.pill, "menu");
  }

  private nameMenu(n: string, anchor: HTMLElement): void {
    const m = this.ctx.models;
    const me = this.me();
    if (n === me) { this.openPop(n, [this.item("That's you", () => this.closePop())], anchor, "menu"); return; }
    const items: [HTMLElement, () => void][] = [this.item("Whisper", () => { this.closePop(); this.setChannel("Whisper", n); this.focus(); })];
    const g = m.group;
    if (g.isLeader(me) && g.list.length < MAX_GROUP && !g.has(n)) items.push(this.item("Invite to group", () => { this.closePop(); g.addMember(n); }));
    const friend = m.friends.list.find((f) => f.Name === n);
    if (!friend) items.push(this.item("Add friend", () => { this.closePop(); m.friends.add(n, false); }));
    const ob = this.playerInView(n);
    if (ob !== null) items.push(this.item("Inspect", () => { this.closePop(); m.inspect.inspect(ob); }));
    items.push(this.item("Copy name", () => { this.closePop(); void navigator.clipboard?.writeText(n).catch(() => {}); }));
    if (friend?.Blocked) items.push(this.item(`Unblock ${n}`, () => { this.closePop(); m.friends.remove(friend.Index); }));
    else if (!friend) items.push(this.item(`Block ${n}`, () => { this.closePop(); m.friends.add(n, true); }, "bad"));
    this.openPop(n, items, anchor, "menu");
  }

  /** The ObjectID of a visible player with this name (C.Inspect needs one), or null. */
  private playerInView(name: string): number | null {
    for (const ob of this.ctx.world.objects.values()) if (ob.race === ObjectType.Player && ob.name === name) return ob.id;
    return null;
  }

  private readBlocked(): boolean {
    const next = new Set<string>();
    for (const f of this.ctx.models.friends.list) if (f.Blocked) next.add(f.Name);
    const same = next.size === this.blocked.size && [...next].every((n) => this.blocked.has(n));
    this.blocked = next;
    return !same;
  }

  // — Settings —

  private settings(): void {
    if (this.pop?.dataset.kind === "set") { this.closePop(); return; }
    this.closePop();
    const p = h("div", "pop set");
    p.dataset.kind = "set";
    const change = (fn: () => void, relog = false) => { fn(); this.save(); this.applyOpts(); if (relog) this.renderLog(); };
    const op = h("input");
    op.type = "range"; op.min = "0"; op.max = "0.9"; op.step = "0.05"; op.value = String(this.opts.op);
    op.addEventListener("input", () => change(() => { this.opts.op = +op.value; }));
    const fade = segmented<number>([{ value: 10, label: "10s" }, { value: 20, label: "20s" }, { value: 60, label: "1m" }, { value: 0, label: "Never" }],
      this.opts.fade, (v) => change(() => { this.opts.fade = v; }, true));
    const size = segmented<number>([{ value: 11, label: "S" }, { value: 12, label: "M" }, { value: 14, label: "L" }], this.opts.fs, (v) => change(() => { this.opts.fs = v; }));
    const time = toggleSwitch(this.opts.time, (v) => change(() => { this.opts.time = v; }, true), { small: true });
    const tags = toggleSwitch(this.opts.tags, (v) => change(() => { this.opts.tags = v; }, true), { small: true });
    const lock = toggleSwitch(this.opts.lock, (v) => change(() => { this.opts.lock = v; }), { small: true });
    // [Filters]: shared with the classic chat options (gameSettings FilterXChat).
    const FILTERS: [keyof GameSettings, string][] = [["FilterNormalChat", "Say"], ["FilterShoutChat", "Shout"], ["FilterWhisperChat", "Whisper"],
      ["FilterGroupChat", "Group"], ["FilterGuildChat", "Guild"], ["FilterSystemChat", "System"]];
    const hidden = FILTERS.filter(([k]) => gameSettings[k] === true).map(([k]) => k);
    const filters = chips<keyof GameSettings>(FILTERS.map(([value, label]) => ({ value, label })), hidden, (sel) => {
      for (const [k] of FILTERS) setSetting(k, sel.has(k) as never);
    }, { multi: true, allowNone: true });
    p.append(h("div", "r", "Background", op), h("div", "r", "Fade lines after", fade.el), h("div", "r", "Text size", size.el),
      h("div", "r", "Timestamps", time.el), h("div", "r", "Channel tags", tags.el), h("div", "r", "Lock position", lock.el),
      h("div", "hd", "Hide in every tab"), filters.el);
    p.addEventListener("pointerdown", (e) => e.stopPropagation());
    this.el.append(p);
    this.pop = p;
    p.style.right = "0";
    p.style.bottom = "100%";
    this.refreshAct();
  }

  private applyOpts(): void {
    this.el.style.setProperty("--cop", String(this.opts.op));
    this.el.style.setProperty("--cfs", this.opts.fs + "px");
    this.el.style.setProperty("--fade", this.opts.fade + "s");
    this.el.classList.toggle("nofade", !this.opts.fade);
    this.el.classList.toggle("lock", this.opts.lock);
  }

  private refreshAct(): void {
    const on = this.hover || document.activeElement === this.input || !!this.pop;
    this.el.classList.toggle("act", on);
  }

  private save(): void {
    try { localStorage.setItem(STORE, JSON.stringify({ opts: this.opts, place: this.place })); } catch { /* not kept */ }
  }

  // — Place: drag the tab bar to move, the grip to resize (pointer capture, measured once per gesture) —

  private applyPlace(): void {
    const { width: W, height: H } = this.rt.layout;
    const p = this.place;
    p.w = Math.min(Math.max(260, p.w), Math.max(260, Math.min(760, W - 8)));
    p.h = Math.min(Math.max(110, p.h), Math.max(110, Math.min(520, H - 40)));
    p.left = Math.min(Math.max(0, p.left), Math.max(0, W - 80));
    p.bottom = Math.min(Math.max(0, p.bottom), Math.max(0, H - 60));
    Object.assign(this.el.style, { left: p.left + "px", bottom: p.bottom + "px", width: p.w + "px", height: p.h + "px" });
  }

  private dragBy(handle: HTMLElement, resize: boolean): void {
    handle.addEventListener("pointerdown", (e) => {
      if (e.button !== 0 || this.opts.lock) return;
      if (!resize && (e.target as HTMLElement).closest(".ct, .ib, input")) return;
      e.preventDefault();
      e.stopPropagation();
      try { handle.setPointerCapture(e.pointerId); } catch { /* gone */ }
      const k = this.el.getBoundingClientRect().width / (this.el.offsetWidth || 1) || 1;
      const sx = e.clientX, sy = e.clientY, o = { ...this.place };
      const ac = new AbortController();
      const opts = { signal: ac.signal };
      const move = rafCoalesce((m: PointerEvent) => {
        const dx = (m.clientX - sx) / k, dy = (m.clientY - sy) / k;
        if (resize) { this.place.w = o.w + dx; this.place.h = o.h - dy; }
        else { this.place.left = o.left + dx; this.place.bottom = o.bottom - dy; }
        this.applyPlace();
      }, ac.signal);
      const end = () => { if (ac.signal.aborted) return; ac.abort(); this.save(); };
      handle.addEventListener("pointermove", (m) => { if (m.pointerId === e.pointerId) move(m); }, opts);
      handle.addEventListener("pointerup", end, opts);
      handle.addEventListener("pointercancel", end, opts);
      handle.addEventListener("lostpointercapture", end, opts);
      this.signal.addEventListener("abort", end, opts);
    }, { signal: this.signal });
  }

  private iconButton(icon: Parameters<typeof svg>[0], title: string, fn: () => void): HTMLElement {
    const b = h("div", "ib", svg(icon));
    if (title) b.title = title;
    b.addEventListener("mousedown", (e) => e.preventDefault(), { signal: this.signal });
    b.addEventListener("click", (e) => { e.stopPropagation(); fn(); }, { signal: this.signal });
    return b;
  }

  // — Announcement ticker (ChatNoticeDialog) —

  private ticker(text: string): void {
    const s = h("span", "", h("b", "", "NOTICE"), text);
    this.notice.hidden = false;
    this.notice.replaceChildren(s);
    if (reducedMotion()) {
      s.classList.add("still");
      later(() => { if (s.isConnected) this.notice.hidden = true; }, 8000, this.signal);
      return;
    }
    s.style.setProperty("--dur", (6 + text.length * 0.12).toFixed(1) + "s");
    s.style.setProperty("--from", (this.notice.offsetWidth || 560) + "px");
    s.addEventListener("animationend", () => { if (s.isConnected) this.notice.hidden = true; }, { once: true });
  }
}
