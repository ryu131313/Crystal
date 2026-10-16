// Ported from play/hudGroup.ts: the new UI's group window over the GroupState model (src/game/group.ts: GroupDialog.GroupList
// with the leader first, AllowGroup, GroupMembersMap, BigMapViewPort.PlayerLocations, and the web-only S.GroupMemberStatus).
// Desktop counterpart: GroupDialog (Client/MirScenes/Dialogs/GroupDialog.cs) + GameScene.GroupInvite / AddMember /
// DeleteMember / DeleteGroup / SwitchGroup; the classic port is src/ui/groupWindow.ts. Changes from the mock:
//  - members are the real roster (Globals.MaxGroup = MAX_GROUP); a card shows class and level from S.GroupMemberStatus,
//    else from the member's MapObj while in view (class, level, the S.ObjectHealth % while unexpired); HP / MP bars are
//    percentages (the status carries no absolute values), empty while nothing is known; the user's own card reads the
//    user (level, HP / MP of RefreshStats, buffs);
//  - where: another map's title (S.GroupMembersMap), else the distance and direction on the user's map
//    (S.SendMemberLocation, the Chebyshev distance like the party frames), refreshed twice a second only while shown;
//  - Allow group = C.SwitchGroup (GroupState.toggleAllowGroup); the switch locks until S.SwitchGroup echoes it. Turning
//    it off while grouped makes the server take you out of the group (PlayerObject.SwitchGroup -> LeaveGroup), so it asks
//    first; "Leave group" on your own card is the same request (Crystal has no other way to leave);
//  - Invite = C.AddMember by name (the classic GroupDialog.AddMember path: leader, or not grouped yet; refused when
//    full); Kick = C.DelMember (leader only, confirm twice). The server answers in chat and with S.AddMember /
//    S.DeleteMember, so nothing changes here until it does;
//  - the incoming invite (S.GroupInvite, GroupState.invite) is a request toast with a countdown that declines on its own
//    (C.GroupInvite false), wired at UI start by this entry's watch hook (GroupSession), so it works while the window is
//    closed; the group log is built from roster changes in the same session (cls d: Crystal keeps none);
//  - HIDDEN (Crystal has no system for them): the loot rule, "Asking to join" requests, Make leader, Disband,
//    Resurrect, the party-frames knob (the HUD's frames, hud/party.ts, follow the roster on their own), and a Trade
//    button (C.TradeRequest is for the player you face, not by name).
// Narrow layout (a layer below 1040 px): one card column, the side panel below the cards.

import { BuffTypeName, ObjectType, Stat } from "../../../protocol/enums.generated.ts";
import { buffIcon } from "../../../game/buffs.ts";
import { Emitter } from "../../../game/emitter.ts";
import { MAX_GROUP } from "../../../game/group.ts";
import type { MapObj } from "../../../game/objects.ts";
import { libPicture } from "../../mir/controls.ts";
import {
  UiWindow, button, confirmTwice, fmtTimeOfDay, h, injectCss, pretty, setStyle, setText, svg, toggleSwitch, typingField,
  type Btn, type PopupPromise,
} from "../core/index.ts";
import { className, classTag } from "../core/format.ts";
import { classVar } from "../core/styles.ts";
import { WINDOW_KEYS } from "../hud/gameMenu.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

/** The window's registry key (hud/gameMenu.ts: menu strip, launcher tile, KeybindOptions.Group = P). */
export const GROUP_WINDOW = WINDOW_KEYS.group;

/** How long the invite toast waits before it declines on its own (the server keeps the invitation until answered). */
const INVITE_SECONDS = 30;
const LOG_MAX = 40;
const MAX_BUFFS = 8;
const CROWN = "M3 8l4 4 5-7 5 7 4-4-2 11H5z";
const ARROW = "M12 3l6 16-6-4-6 4z";
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

// — The group session (DOM-free apart from the toast it asks): the invite prompt and the group log —

export interface GroupLogLine { readonly text: string; readonly at: number }

/** One per UI runtime, built by the watch hook at UI start: answers S.GroupInvite with a request toast and keeps a log of
 *  roster changes (joins, leaves, the group ending) while the window is closed. */
export class GroupSession {
  /** The log changed. */
  readonly changed = new Emitter<void>();
  private readonly lines: GroupLogLine[] = [];
  private roster: readonly string[] = [];
  /** The open invite toast and whether it was settled without an answer (replaced by a newer invite). */
  private prompt: { p: PopupPromise<boolean>; settled: boolean } | null = null;

  constructor(private readonly host: ModernUiHost) {
    const g = host.ctx.models.group, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    this.roster = [...g.list];
    on(g.invite.on(({ name }) => this.invite(name)));
    on(g.changed.on(() => this.diff()));
  }

  get log(): readonly GroupLogLine[] { return this.lines; }

  /** GameScene.GroupInvite: a toast (Accept / Decline) that declines when its bar runs out. */
  private invite(name: string): void {
    // The server holds one invitation per player; a newer one replaces the old toast without answering it.
    if (this.prompt) { this.prompt.settled = true; this.prompt.p.close(); }
    const entry = { p: this.host.rt.popups.request({ from: name, tag: "Group", text: "invites you to join the group.", seconds: INVITE_SECONDS }), settled: false };
    this.prompt = entry;
    void entry.p.then((yes) => {
      if (this.prompt === entry) this.prompt = null;
      // Replaced by a newer invite, or the UI went (log-out): no answer. Accept / Decline / the bar running out answer.
      if (entry.settled || this.host.rt.signal.aborted) return;
      this.host.ctx.models.group.answerInvite(yes);
    });
  }

  /** Roster changes -> log lines (S.AddMember / S.DeleteMember / S.DeleteGroup, after the model's chat line). */
  private diff(): void {
    const now = this.host.ctx.models.group.list;
    const before = this.roster;
    if (now.length === before.length && now.every((n, i) => n === before[i])) return;
    const me = this.host.ctx.player.user?.name ?? "";
    if (before.length === 0 && now.length > 0) this.add(now[0] === me ? "You formed a group." : `You joined ${now[0]}'s group.`);
    else if (now.length === 0) this.add("You left the group.");
    if (now.length > 0) {
      // (Joining sends one S.AddMember per member, the user among them: the first one already said "You joined".)
      for (const n of now) if (!before.includes(n) && before.length > 0 && n !== me) this.add(`${n} joined.`);
      for (const n of before) if (!now.includes(n)) this.add(`${n} left.`);
      if (before.length > 0 && before[0] !== now[0] && now[0]) this.add(`${now[0]} leads the group.`);
    }
    this.roster = [...now];
  }

  private add(text: string): void {
    this.lines.push({ text, at: Date.now() });
    if (this.lines.length > LOG_MAX) this.lines.splice(0, this.lines.length - LOG_MAX);
    this.changed.emit();
  }
}

/** The runtime's group session (built on first use). */
export function groupSession(host: ModernUiHost): GroupSession {
  return host.rt.service("groupSession", () => new GroupSession(host));
}

// — The window —

const CSS = `
.hudm .grpw { width: 720px; }
.hudm .grpw .panel { display: flex; flex-direction: column; }
.hudm .grpw .head { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .grpw .count b { font-size: 18px; color: #fff; } .hudm .grpw .count span { font-size: 11px; color: var(--dim); }
.hudm .grpw .cap { width: 120px; height: 5px; border-radius: 3px; background: #04060a; overflow: hidden; margin-top: 4px; }
.hudm .grpw .cap i { display: block; height: 100%; width: 100%; transform-origin: 0 50%; transform: scaleX(0); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .grpw .opt { display: flex; align-items: center; gap: 7px; font-size: 11px; color: var(--dim); }
.hudm .grpw .inv { margin-left: auto; display: flex; gap: 6px; }
.hudm .grpw .inv .fld { width: 150px; }
.hudm .grpw .gbody { display: flex; min-height: 0; height: 430px; }
.hudm .grpw .cards { flex: 1; min-width: 0; padding: 12px; overflow-y: auto; display: grid; grid-template-columns: 1fr 1fr; gap: 8px; align-content: start; scrollbar-width: thin; }
.hudm .grpw .card { position: relative; display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .grpw .card:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .grpw .card.sel { border-color: var(--ac); box-shadow: inset 0 0 14px color-mix(in srgb, var(--ac) 15%, transparent); }
.hudm .grpw .card.dead { opacity: .6; }
.hudm .grpw .card.empty { cursor: default; justify-content: center; align-items: center; color: var(--dim); font-size: 11px; border-style: dashed; min-height: 74px; text-align: center; }
.hudm .grpw .badge { position: relative; width: 40px; height: 40px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800;
  color: var(--pcls, var(--tx)); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid color-mix(in srgb, var(--pcls, var(--fr)) 60%, var(--fr)); }
.hudm .grpw .card.you .badge { border-color: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .grpw .crown { position: absolute; top: -9px; left: 50%; transform: translateX(-50%); color: #ffd75a; display: flex; }
.hudm .grpw .crown svg.ic { width: 15px; height: 15px; fill: currentColor; stroke: #000; stroke-width: 1; }
.hudm .grpw .info { flex: 1; min-width: 0; }
.hudm .grpw .nm { display: flex; align-items: center; gap: 6px; font-size: 13px; font-weight: 600; color: #fff; }
.hudm .grpw .nm > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .grpw .nm small { flex: none; font-size: 10px; font-weight: 500; color: var(--dim); }
.hudm .grpw .nm .st { margin-left: auto; flex: none; font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .grpw .nm .st.dead { color: var(--bad-soft); border-color: color-mix(in srgb, var(--bad) 50%, transparent); }
.hudm .grpw .nm .st.away { color: var(--warn); border-color: color-mix(in srgb, var(--warn) 45%, transparent); }
.hudm .grpw .bars { display: flex; flex-direction: column; gap: 3px; margin: 6px 0 5px; }
.hudm .grpw .b { position: relative; height: 9px; border-radius: 2px; background: #04060a; overflow: hidden; border: 1px solid rgba(0,0,0,.8); }
.hudm .grpw .b i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); transition: transform .4s; }
.hudm .grpw .b.hp i { background: linear-gradient(90deg, #6a0c14, #c8303a); } .hudm .grpw .b.mp i { background: linear-gradient(90deg, #12306e, #3a7ee0); }
.hudm .grpw .b span { position: absolute; inset: 0; font-size: 8px; line-height: 9px; text-align: center; color: #fff; text-shadow: 0 0 2px #000; font-variant-numeric: tabular-nums; }
.hudm .grpw .loc { display: flex; align-items: center; gap: 6px; font-size: 10px; color: var(--dim); min-height: 15px; }
.hudm .grpw .loc svg.ic { color: var(--ac); width: 12px; height: 12px; fill: currentColor; stroke: none; }
.hudm .grpw .loc .far { color: var(--warn); }
.hudm .grpw .bf { display: flex; gap: 2px; margin-left: auto; }
.hudm .grpw .bf i { width: 15px; height: 15px; border-radius: 3px; background: #000 center / contain no-repeat; image-rendering: pixelated; border: 1px solid var(--fr); }
.hudm .grpw .side { width: 236px; flex: none; display: flex; flex-direction: column; gap: 10px; padding: 12px; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .grpw .slbl { display: flex; justify-content: space-between; margin-bottom: 6px; }
.hudm .grpw .who { font-size: 13px; font-weight: 700; color: #fff; margin-bottom: 6px; overflow: hidden; text-overflow: ellipsis; }
.hudm .grpw .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.hudm .grpw .acts .btn { height: 26px; padding: 0 6px; }
.hudm .grpw .hint { font-size: 10px; color: var(--dim); line-height: 1.4; }
.hudm .grpw .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .grpw .log { flex: 1; min-height: 60px; overflow-y: auto; scrollbar-width: thin; }
.hudm .grpw .log div { display: flex; gap: 8px; padding: 4px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .grpw .log div span { margin-left: auto; color: var(--dim); white-space: nowrap; }
/* Narrow layer: one card column, the side panel under the cards. */
.hudm.narrow .grpw { width: 500px; }
.hudm.narrow .grpw .inv { margin-left: 0; }
.hudm.narrow .grpw .gbody { flex-direction: column; height: 520px; }
.hudm.narrow .grpw .cards { grid-template-columns: 1fr; }
.hudm.narrow .grpw .side { width: auto; height: 190px; flex-direction: row; border-left: 0; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm.narrow .grpw .side > * { flex: 1; min-width: 0; }
`;

/** What a card shows for one member (from the status, the MapObj in view, or the user). */
interface Live { cls: number; level: number; hp: number; mp: number; buffs: readonly number[]; dead: boolean }

interface Card {
  readonly name: string;
  readonly el: HTMLElement; readonly badge: HTMLElement; readonly tag: HTMLElement; readonly sub: HTMLElement; readonly st: HTMLElement;
  readonly hp: HTMLElement; readonly hpText: HTMLElement; readonly mp: HTMLElement; readonly mpText: HTMLElement;
  readonly arrow: SVGSVGElement; readonly where: HTMLElement; readonly bf: HTMLElement;
  cls: number; buffKey: string; arrowKey: string;
}

class GroupWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private picked = "";
  private note = "";
  private rosterKey = "\u0000";
  private cards: Card[] = [];
  private allowPending = false;
  private logKey = -1;

  // — DOM, built once —
  private readonly countText = h("b");
  private readonly capFill = h("i");
  private readonly allow: ReturnType<typeof toggleSwitch>;
  private readonly invInput = typingField(h("input", "fld"));
  private readonly invBtn: Btn;
  private readonly cardsBox = h("div", "cards");
  private readonly emptyCard = h("div", "card empty");
  private readonly whoLbl = h("div", "lbl slbl");
  private readonly who = h("div", "who");
  private readonly acts = h("div", "acts");
  private readonly actHint = h("div", "hint");
  private readonly noteEl = h("div", "note");
  private readonly logBox = h("div", "log");
  private readonly btn: { whisper: Btn; kick: Btn & { reset(): void }; leave: Btn };

  constructor(private readonly ui: ModernUiHost, private readonly session: GroupSession) {
    super(ui.rt, "grpw", 210, 50);
    injectCss("win-group", CSS);
    this.m = ui.ctx.models;
    const sig = this.signal, g = this.m.group;

    // Head: count + capacity, Allow group, invite by name
    this.allow = toggleSwitch(g.allowGroup, (on) => this.setAllow(on), { label: "Allow group", title: "Let other players invite you to a group" });
    this.invInput.placeholder = "Invite by name";
    this.invInput.maxLength = 20;
    this.invBtn = button("Invite", () => this.invite(), { cls: "main", icon: "plus" });
    const form = h("form", "inv", this.invInput, this.invBtn.el);
    form.addEventListener("submit", (e) => { e.preventDefault(); this.invite(); }, { signal: sig });
    const head = h("div", "head",
      h("div", "count", this.countText, h("span", "", " members"), h("div", "cap", this.capFill)),
      h("label", "opt", this.allow.el, "Allow group"), form);

    // Side: the picked member's actions, the note, the group log
    this.btn = {
      whisper: button("Whisper", () => { if (this.picked) this.ui.chat.prefill(`/${this.picked} `); }, { icon: "chat", title: "Whisper (/name in the chat box)" }),
      kick: confirmTwice("Kick", () => this.kick(), { cls: "warn", confirmLabel: "Click again", title: "Remove from the group (leader only)", signal: sig }),
      leave: button("Leave group", () => void this.leave(), { cls: "warn", icon: "logout", title: "Leave the group (this also turns Allow group off)" }),
    };
    this.acts.append(this.btn.whisper.el, this.btn.kick.el, this.btn.leave.el);
    const side = h("div", "side",
      h("div", "", this.whoLbl, this.who, this.acts, this.actHint),
      h("div", "", this.noteEl, h("div", "lbl slbl", "Group log"), this.logBox));
    this.el.append(h("div", "panel", this.titleBar("GROUP", "Close (P)"), head, h("div", "gbody", this.cardsBox, side)));

    // Models: re-render when shown, else on the next open (dirty-when-closed). In-view HP / distances: 2 Hz while shown.
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(g.changed.on(() => { this.allowPending = false; this.invalidate(); }));
    on(g.statusChanged.on(() => this.invalidate()));
    on(session.changed.on(() => this.invalidate()));
    this.everyShown(500, () => this.updateCards());
  }

  private me(): string { return this.ui.ctx.player.user?.name ?? ""; }

  protected override closed(): void { this.btn.kick.reset(); }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const g = this.m.group, list = g.list, me = this.me();
    const n = list.length;
    setText(this.countText, n ? `${n} / ${MAX_GROUP}` : "No group");
    setStyle(this.capFill, "transform", `scaleX(${(n / MAX_GROUP).toFixed(3)})`);
    if (!this.allowPending) { this.allow.set(g.allowGroup); this.allow.lock(false); }
    const leader = g.isLeader(me);
    if (!leader) this.invBtn.setDisabled(true, "Only the group leader can invite");
    else if (n >= MAX_GROUP) this.invBtn.setDisabled(true, "Your group already has the maximum number of members");
    else this.invBtn.setDisabled(false);
    this.invInput.disabled = !leader || n >= MAX_GROUP;

    // Cards: rebuilt only when the roster changed
    const key = list.join("\u0001");
    if (key !== this.rosterKey) {
      this.rosterKey = key;
      this.cards = list.map((name, i) => this.buildCard(name, i === 0, name === me));
      const free = MAX_GROUP - n;
      setText(this.emptyCard, n === 0
        ? "You are not in a group. Invite a player by name to start one; they must allow group invites."
        : free > 0 ? `${free} open slot${free === 1 ? "" : "s"} · the leader invites by name` : "The group is full.");
      this.cardsBox.replaceChildren(...this.cards.map((c) => c.el), this.emptyCard);
      if (this.picked && !list.includes(this.picked)) this.picked = "";
      this.btn.kick.reset();
    }
    if (!this.picked && n) this.picked = me && list.includes(me) ? me : list[0]!;
    for (const c of this.cards) c.el.classList.toggle("sel", c.name === this.picked);
    this.updateCards();
    this.renderSide();
  }

  private buildCard(name: string, leader: boolean, you: boolean): Card {
    const tag = h("span");
    const badge = h("div", "badge", tag);
    if (leader) { const c = h("div", "crown", svg(CROWN)); c.title = "Leader"; badge.append(c); }
    const sub = h("small"), st = h("span", "st");
    const hp = h("i"), hpText = h("span"), mp = h("i"), mpText = h("span");
    const arrow = svg(ARROW);
    arrow.style.display = "none";
    const where = h("span"), bf = h("div", "bf");
    const el = h("div", "card" + (you ? " you" : ""), badge,
      h("div", "info", h("div", "nm", h("span", "", name), sub, st),
        h("div", "bars", h("div", "b hp", hp, hpText), h("div", "b mp", mp, mpText)),
        h("div", "loc", arrow, where, bf)));
    el.setAttribute("role", "button");
    el.addEventListener("click", () => { if (this.picked !== name) { this.picked = name; this.note = ""; this.btn.kick.reset(); this.invalidate(); } }, { signal: this.signal });
    return { name, el, badge, tag, sub, st, hp, hpText, mp, mpText, arrow, where, bf, cls: -1, buffKey: "", arrowKey: "" };
  }

  private inView(name: string): MapObj | null {
    for (const ob of this.ui.ctx.world.objects.values()) if (ob.race === ObjectType.Player && !ob.ownerName && ob.name === name) return ob;
    return null;
  }

  /** The member's class / level / bars / buffs: the user, else S.GroupMemberStatus, else the MapObj in view. */
  private live(name: string, you: boolean): Live {
    const ctx = this.ui.ctx;
    if (you) {
      const p = ctx.player, s = p.stats?.stats;
      const maxHp = s?.[Stat.HP] ?? 0, maxMp = s?.[Stat.MP] ?? 0;
      return {
        cls: p.user?.look.class ?? -1, level: p.level,
        hp: maxHp > 0 ? Math.min(100, p.hp / maxHp * 100) : -1, mp: maxMp > 0 ? Math.min(100, p.mp / maxMp * 100) : -1,
        buffs: p.buffs.filter((b) => b.Visible).map((b) => b.Type), dead: p.user?.dead ?? false,
      };
    }
    const st = this.m.group.memberStatus.get(name);
    const ob = this.inView(name);
    if (st) return { cls: st.cls, level: st.level, hp: st.hpPercent, mp: st.mpPercent, buffs: st.buffs, dead: (ob?.dead ?? false) || st.hpPercent === 0 };
    return {
      cls: ob ? ob.look.class : -1, level: ob?.level ?? 0,
      hp: ob && ob.healthPercent >= 0 ? ob.healthPercent : -1, mp: -1, buffs: [], dead: ob?.dead ?? false,
    };
  }

  /** Writes what changed on each card (text / style setters skip equal values). */
  private updateCards(): void {
    const g = this.m.group, me = this.me(), user = this.ui.ctx.player.user, myMap = this.ui.ctx.world.mapTitle;
    for (const c of this.cards) {
      const you = c.name === me;
      const l = this.live(c.name, you);
      if (l.cls !== c.cls) {
        c.cls = l.cls;
        setText(c.tag, l.cls >= 0 ? classTag(l.cls) : "?");
        if (l.cls >= 0) c.badge.style.setProperty("--pcls", classVar(l.cls)); else c.badge.style.removeProperty("--pcls");
      }
      setText(c.sub, l.cls >= 0 ? `${className(l.cls)}${l.level ? ` ${l.level}` : ""}` : l.level ? `Lv ${l.level}` : "");
      const bar = (fill: HTMLElement, text: HTMLElement, pct: number) => {
        setStyle(fill, "transform", `scaleX(${(Math.max(0, pct) / 100).toFixed(3)})`);
        setText(text, pct >= 0 ? `${Math.round(pct)}%` : "");
      };
      bar(c.hp, c.hpText, l.hp);
      bar(c.mp, c.mpText, l.mp);
      // Where: another map's title, else distance and direction on this map
      const map = g.memberMaps.get(c.name), loc = g.locations.get(c.name);
      let text = "", far = false, ang: number | null = null;
      if (you) text = myMap;
      else if (map && map !== myMap) { text = map; far = true; }
      else if (loc && user) {
        const dx = loc.X - user.location.X, dy = loc.Y - user.location.Y, d = Math.max(Math.abs(dx), Math.abs(dy));
        if (d > 0) { ang = Math.atan2(dx, -dy); text = `${d} ${d === 1 ? "cell" : "cells"} ${DIRS[(Math.round(ang / (Math.PI / 4)) + 8) % 8]}`; }
        else text = "Here";
      } else if (map) text = map;
      setText(c.where, text);
      c.where.classList.toggle("far", far);
      const arrowKey = ang === null ? "" : ang.toFixed(2);
      if (arrowKey !== c.arrowKey) {
        c.arrowKey = arrowKey;
        c.arrow.style.display = ang === null ? "none" : "";
        c.arrow.style.transform = ang === null ? "" : `rotate(${arrowKey}rad)`;
      }
      const status = l.dead ? "Dead" : you ? "You" : far ? "Away" : loc || this.inView(c.name) ? "Nearby" : "";
      setText(c.st, status);
      c.st.hidden = !status;
      c.st.classList.toggle("dead", l.dead);
      c.st.classList.toggle("away", far && !l.dead);
      c.el.classList.toggle("dead", l.dead);
      const buffs = l.buffs.slice(0, MAX_BUFFS), bk = buffs.join(",");
      if (bk !== c.buffKey) {
        c.buffKey = bk;
        c.bf.replaceChildren(...buffs.map((b) => {
          const i = h("i");
          i.title = pretty(BuffTypeName[b] ?? "");
          const [lib, idx] = buffIcon(b);
          void libPicture(lib, idx).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
          return i;
        }));
      }
    }
  }

  private renderSide(): void {
    const g = this.m.group, me = this.me(), list = g.list;
    const grouped = list.length > 0, leader = g.isLeader(me);
    const name = this.picked, you = !name || name === me;
    setText(this.whoLbl, !grouped ? "No group" : you ? (list[0] === me ? "You · leader" : "You") : list[0] === name ? "Leader" : "Member");
    setText(this.who, grouped ? name || me : me);
    this.btn.whisper.el.hidden = you || !grouped;
    this.btn.kick.el.hidden = you || !grouped || !leader;
    this.btn.leave.el.hidden = !you || !grouped;
    this.btn.leave.setDisabled(this.allowPending, "Waiting for the server");
    setText(this.actHint, !grouped
      ? "Invite a player by name to form a group; you become its leader."
      : you
        ? "Leaving turns Allow group off; turn it back on to be invited again."
        : leader ? "Kick removes them from the group." : "Only the leader can invite or remove members.");
    setText(this.noteEl, this.note);
    // The log: rebuilt only when it grew (append-only; newest first)
    const log = this.session.log;
    if (log.length !== this.logKey) {
      this.logKey = log.length;
      const rows = [...log].reverse().map((l) => h("div", "", l.text, h("span", "", fmtTimeOfDay(l.at))));
      this.logBox.replaceChildren(...(rows.length ? rows : [h("div", "", "Nothing yet.")]));
    }
  }

  // — Actions (requests through the model: nothing changes until the server answers) —

  private say(text: string): void {
    this.note = text;
    setText(this.noteEl, text);
  }

  /** GroupDialog.AddMember's answer: C.AddMember (the server checks the leader, the size and the target). */
  private invite(): void {
    const g = this.m.group, me = this.me();
    const name = this.invInput.value.trim();
    if (!name) { this.invInput.focus({ preventScroll: true }); return; }
    if (g.list.length >= MAX_GROUP) { this.say("Your group already has the maximum number of members."); return; }
    if (!g.isLeader(me)) { this.say("You are not the leader of your group."); return; }
    if (name.toLowerCase() === me.toLowerCase()) { this.say("You cannot group with yourself."); return; }
    if (g.list.some((n) => n.toLowerCase() === name.toLowerCase())) { this.say(`${name} is already in the group.`); return; }
    g.addMember(name);
    this.invInput.value = "";
    this.say(`Invite sent to ${name}.`);
  }

  /** GroupDialog.DelMember's answer: C.DelMember (leader only; S.DeleteMember removes the card). */
  private kick(): void {
    const name = this.picked;
    if (!name || name === this.me()) return;
    this.m.group.removeMember(name);
    this.say(`Removing ${name}…`);
  }

  /** The Allow group switch: C.SwitchGroup; locked until S.SwitchGroup. Off while grouped asks first (it leaves). */
  private setAllow(on: boolean): void {
    const g = this.m.group;
    this.allow.set(g.allowGroup); // shows the server's state until the echo
    if (on === g.allowGroup || this.allowPending) return;
    if (!on && g.list.length > 0) { void this.leave(); return; }
    this.send(on ? "Group invites are allowed." : "Group invites are refused.");
  }

  /** Leave = C.SwitchGroup(false): the server takes you out of the group (PlayerObject.SwitchGroup -> LeaveGroup). */
  private async leave(): Promise<void> {
    const g = this.m.group;
    if (!g.list.length || this.allowPending) return;
    const lead = g.isLeader(this.me()) && g.list.length > 1;
    const ok = await this.ui.rt.popups.ask(
      `Leave the group?${lead ? " The next member becomes the leader." : ""}\nThis also turns Allow group off, so nobody can invite you until you turn it back on.`,
      { title: "Leave group", kind: "warning", labels: { yes: "Leave", no: "Stay" } });
    if (!ok || this.signal.aborted || !g.list.length || !g.allowGroup || this.allowPending) return;
    this.send("Leaving the group…");
  }

  /** Flips Allow group on the server and locks the switch until the echo. */
  private send(note: string): void {
    this.allowPending = true;
    this.allow.lock(true, "Waiting for the server");
    this.m.group.toggleAllowGroup();
    this.say(note);
    this.invalidate();
  }
}

registerWindow(GROUP_WINDOW, (host) => new GroupWindow(host, groupSession(host)), {
  // The invite toast and the log run from UI start (the window itself is built on its first open).
  watch: (host) => { groupSession(host); },
});
