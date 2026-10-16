// Ported from play/hudMentor.ts: the new UI's mentor window over the real Mentor model (src/game/relationship.ts:
// S.MentorUpdate partner name / level / online / banked EXP, the web S.MentorDetails start / end dates + partner class,
// the C.FindMentor finder -> S.MentorCandidates), the account allow toggles (S.AllowToggles) and S.ServerRules (the
// MentorSystem.ini numbers). Desktop counterpart: MentorDialog + GameScene.MentorRequest / MentorUpdate; the classic port
// is the MentorWindow in src/ui/relationshipWindow.ts (its rules are kept: the higher level is the mentor, the banked
// EXP shows to the mentor only, Add asks for a name, Remove asks to confirm).
//
// Server rules (PlayerObject.AddMentor / MentorReply / MentorBreak / GainExp, HumanObject's Mentor / Mentee buffs): only
// the would-be MENTEE asks (C.AddMentor with the mentor's name); the mentor must be the same class, at least
// MentorLevelGap levels higher, allow mentor requests and be free; a mentorship lasts MentorLength days and ends early
// when the mentee's level + gap passes the mentor's; while both are online the mentor has the Mentor buff
// (+MentorDamageBoost % damage) and the mentee the Mentee buff, whose +MentorExpBoost % EXP only counts while the mentor
// is in the mentee's group, on the same map, in range and alive; MenteeExpBank % of the mentee's EXP is banked for the
// mentor and moved over when the bond ends only if the other one is online then; whoever cancels (C.CancelMentor) waits
// MentorLength days before a new mentorship. Changes from the mock:
//  - one real bond (no "view as" switch): the role comes from the levels; no EXP / level simulation, no confetti, no
//    fake chat (Mentor chat is the "!#" prefix in the real chat box: a button pre-fills it);
//  - the find board is the real C.FindMentor (mentee side, 3 s throttle) and its Request button sends C.AddMentor; there
//    is no "find a mentee" board (a mentor cannot ask in Crystal: keep "Allow mentor requests" on instead);
//  - the term ring comes from S.MentorDetails (hidden until it arrives); rule numbers from S.ServerRules (generic
//    wording until it arrives); buff chips from the user's real Mentor / Mentee buffs, the EXP chip only while grouped
//    with the partner nearby (in view, alive);
//  - HIDDEN (no data): the partner's location, the cancel cooldown (not sent), the graduation track's start level.
// Incoming requests (S.MentorRequest) are request toasts wired at UI start (the registry's watch hook).
// Narrow layout (a layer below 1040 px): one column.

import { AllowToggle, BuffType, MirClassName, ObjectType } from "../../../protocol/enums.generated.ts";
import type { ClientMentorCandidate } from "../../../protocol/server.ts";
import { FIND_MENTOR_THROTTLE, type Mentor } from "../../../game/relationship.ts";
import { rulesFor, serverRules, type ServerRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, classTag, classVar, className, fmt, fmtDate, h, injectCss, later, progressRing, setText, svg,
  toggleSwitch, typingField, type Btn,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.mentor). */
export const MENTOR_WINDOW = "mentor";

const DAY = 86_400_000;
/** Seconds an incoming mentor request toast stays up (the desktop's box waits forever; the toast declines when it runs out). */
const REQUEST_SECONDS = 30;

const CSS = `
.hudm .mnwin { width: 760px; }
.hudm.narrow .mnwin { width: 500px; }
.hudm .mnwin .panel { display: flex; flex-direction: column; max-height: 600px; overflow: hidden; }
.hudm .mnwin .ban { display: flex; align-items: center; gap: 14px; padding: 14px 18px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 50% 90% at 50% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .mnwin .who { flex: 1; min-width: 0; display: flex; align-items: center; gap: 10px; }
.hudm .mnwin .who.r { flex-direction: row-reverse; text-align: right; }
.hudm .mnwin .med { position: relative; width: 56px; height: 56px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  font-size: 13px; font-weight: 800; letter-spacing: .04em; color: #fff; background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%);
  border: 2px solid var(--cc, var(--ac)); box-shadow: 0 0 0 3px #000; }
.hudm .mnwin .med.off { border-color: var(--fr); color: var(--dim); }
.hudm .mnwin .med u { position: absolute; right: 1px; bottom: 1px; width: 11px; height: 11px; border-radius: 50%; border: 2px solid #000; background: var(--ok); }
.hudm .mnwin .med.off u { background: #555; } .hudm .mnwin .med u[hidden] { display: none; }
.hudm .mnwin .med em { position: absolute; top: -9px; left: 50%; translate: -50% 0; font-style: normal; font-size: 8px; letter-spacing: .1em; padding: 1px 6px; border-radius: 6px; color: #fff; background: var(--ac); }
.hudm .mnwin .who .nm { min-width: 0; } .hudm .mnwin .who b { display: block; font-size: 15px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .mnwin .who small { display: block; font-size: 11px; color: var(--dim); }
.hudm .mnwin .who .you { color: var(--ac); font-size: 10px; font-weight: 700; letter-spacing: .1em; }
.hudm .mnwin .mcen { width: 190px; flex: none; display: flex; flex-direction: column; align-items: center; gap: 6px; text-align: center; }
.hudm.narrow .mnwin .mcen { width: 130px; }
.hudm .mnwin .mcen .t { font-size: 11px; color: var(--dim); }
.hudm .mnwin .mbuffs { display: flex; flex-wrap: wrap; justify-content: center; gap: 4px; }
.hudm .mnwin .bf { display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 10px; font-size: 10px; font-weight: 700; border: 1px solid var(--fr); color: var(--dim); }
.hudm .mnwin .bf.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 28%, var(--pb)); }
.hudm .mnwin .bf svg { width: 12px; height: 12px; }
.hudm .mnwin .body { flex: 1; min-height: 0; display: flex; }
.hudm.narrow .mnwin .body { flex-direction: column; overflow-y: auto; }
.hudm .mnwin .col { padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; min-width: 0; }
.hudm .mnwin .col > * { flex-shrink: 0; }
.hudm .mnwin .col.a { width: 330px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mnwin .col.b { flex: 1; }
.hudm.narrow .mnwin .col { overflow: visible; } .hudm.narrow .mnwin .col.a { width: auto; border-right: 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mnwin .sl { display: flex; align-items: center; gap: 6px; }
.hudm .mnwin .sl svg { color: var(--ac); width: 14px; height: 14px; } .hudm .mnwin .sl > b { margin-left: auto; font-size: 11px; font-weight: 600; color: var(--tx); letter-spacing: 0; text-transform: none; }
.hudm .mnwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; line-height: 1.45; }
.hudm .mnwin .box b { color: var(--ac); }
.hudm .mnwin .bank { display: flex; align-items: center; gap: 12px; }
.hudm .mnwin .bank .big { font-size: 20px; font-weight: 800; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .mnwin .bank small { display: block; font-size: 10px; color: var(--dim); line-height: 1.4; }
.hudm .mnwin .perk { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 3px 0; }
.hudm .mnwin .perk u { width: 8px; height: 8px; border-radius: 50%; flex: none; background: var(--fr); } .hudm .mnwin .perk.on u { background: var(--ok); }
.hudm .mnwin .perk span { flex: 1; } .hudm .mnwin .perk em { font-style: normal; font-size: 10px; color: var(--dim); }
.hudm .mnwin .row { display: flex; flex-wrap: wrap; gap: 6px; } .hudm .mnwin .row .btn { flex: 1; }
.hudm .mnwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; }
.hudm .mnwin .rules { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .mnwin .rules div { padding: 6px 8px; border-radius: var(--rad); font-size: 11px; line-height: 1.35; color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .mnwin .rules b { display: block; font-size: 14px; color: var(--ac); }
.hudm .mnwin .find { display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--dim); }
.hudm .mnwin .find input { width: 64px; }
.hudm .mnwin .bd { display: flex; align-items: center; gap: 10px; padding: 5px 8px; border-radius: var(--rad); font-size: 12px; }
.hudm .mnwin .bd:nth-child(even) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .mnwin .bd .tg { width: 34px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 9px; font-weight: 800; border-radius: 6px;
  border: 1px solid var(--cc, var(--fr)); background: var(--pb); }
.hudm .mnwin .bd .n { flex: 1; min-width: 0; } .hudm .mnwin .bd .n b { color: #fff; } .hudm .mnwin .bd .n small { display: block; font-size: 10px; color: var(--dim); }
.hudm .mnwin .tog { display: flex; align-items: center; gap: 8px; font-size: 12px; }
`;

/** The user's side of a bond (MentorDialog.UpdateInterface: the higher level is the mentor). */
interface Side { name: string; cls: number; level: number; online: boolean; you: boolean; clsKnown: boolean }

class MentorWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly mentor: Mentor;
  /** The derived state the 1 s check compares (buffs, group, partner in view, own level): re-render when it changes. */
  private liveKey = "";
  /** The candidate a request was sent to (a note on its row; nothing changes until the server answers). */
  private sentTo = "";
  private searched = false;
  private cleared: (() => void) | null = null;

  // — DOM, built once —
  private readonly ban = h("div", "ban");
  private readonly ring = progressRing(80, 6);
  private readonly colA = h("div", "col a");
  private readonly colB = h("div", "col b");
  private readonly allowSw: ReturnType<typeof toggleSwitch>;
  private readonly allowRow: HTMLElement;
  private readonly minIn = typingField(h("input", "fld"));
  private readonly maxIn = typingField(h("input", "fld"));
  private readonly searchBtn: Btn;
  private readonly addBtn: Btn;
  private readonly board = h("div", "");
  private readonly boardNote = h("div", "note");
  private readonly findBox: HTMLElement;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "mnwin", null, 60);
    injectCss("win-mentor", CSS);
    this.m = ui.ctx.models;
    this.mentor = this.m.mentor;
    const acct = this.m.account;

    // Allow mentor requests: C.SetAllowToggle (server answers S.AllowToggles); an older server only has the C.AllowMentor flip.
    this.allowSw = toggleSwitch(acct.allowMentor, () => {
      this.allowSw.set(acct.allowMentor); // server authoritative: the switch moves on S.AllowToggles
      if (acct.togglesKnown) acct.setToggle(AllowToggle.Mentor, !acct.allowMentor);
      else this.mentor.allowMentor();
    }, { small: true, label: "Allow mentor requests" });
    this.allowRow = h("div", "tog", this.allowSw.el, "Allow mentor requests");

    // Find board (mentee side): C.FindMentor over a level range; Request = C.AddMentor.
    for (const i of [this.minIn, this.maxIn]) { i.type = "number"; i.min = "1"; i.max = "65535"; }
    this.searchBtn = button("Search", () => this.search(), { cls: "main", icon: "search", title: "Online players who could mentor you (same class, high enough level, accepting requests)" });
    this.addBtn = button("Add by name…", () => void this.addByName(), { icon: "plus", title: "Ask a player to be your mentor (C.AddMentor)" });
    this.listen(this.minIn, "keydown", (e) => { if (e.key === "Enter") this.search(); });
    this.listen(this.maxIn, "keydown", (e) => { if (e.key === "Enter") this.search(); });
    this.findBox = h("div", "", h("div", "find", "Level", this.minIn, "to", this.maxIn, this.searchBtn.el), this.boardNote, this.board, h("div", "row", this.addBtn.el));

    this.el.append(h("div", "panel", this.titleBar("MENTORSHIP", "Close"), this.ban, h("div", "body", this.colA, this.colB)));

    // Model events: re-render when shown, else on the next open (dirty-when-closed).
    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    const inv = () => this.invalidate();
    on(this.mentor.changed.on(() => { this.sentTo = ""; this.invalidate(); }));
    on(this.mentor.candidatesReceived.on(() => { this.searched = true; this.renderBoard(); }));
    on(acct.togglesChanged.on(() => this.allowSw.set(acct.allowMentor)));
    on(serverRules(ui.ctx.session).changed.on(inv));
    on(this.m.group.changed.on(inv));
    // Buffs, the partner coming into view, the user's level: no event of their own here, so a cheap check once a second
    // while shown (the term ring moves by days: the same tick redraws it).
    this.everyShown(1000, () => { const k = this.stateKey(); if (k !== this.liveKey) this.invalidate(); });
  }

  protected override shown(): void {
    this.allowSw.set(this.m.account.allowMentor);
    if (!this.minIn.value) this.defaultRange();
    this.invalidate();
  }

  protected override closed(): void { this.cleared?.(); this.cleared = null; }

  // — Derived state —

  private rules(): ServerRules | null { return rulesFor(this.ui.ctx.session); }
  private me(): { name: string; cls: number; level: number } {
    const u = this.ui.ctx.player.user;
    return { name: u?.name ?? "", cls: u?.look.class ?? 0, level: this.ui.ctx.player.level };
  }
  private hasBuff(t: number): boolean { return this.ui.ctx.player.buffs.some((b) => b.Type === t); }
  /** The partner in view (MapControl.Objects; a hero has an owner), alive. Stands in for "same map, in range, alive". */
  private partnerNearby(): boolean {
    const n = this.mentor.mentorName;
    if (!n) return false;
    for (const o of this.ui.ctx.world.objects.values()) if (o.race === ObjectType.Player && !o.ownerName && !o.isUser && o.name === n) return !o.dead;
    return false;
  }
  private grouped(): boolean { return this.mentor.mentorName !== "" && this.m.group.has(this.mentor.mentorName); }
  private stateKey(): string {
    return [this.me().level, this.hasBuff(BuffType.Mentor), this.hasBuff(BuffType.Mentee), this.grouped(), this.partnerNearby(),
      this.mentor.hasDetails ? Math.floor(Date.now() / 60_000) : 0].join("|");
  }

  /** The level range of a search: from the lowest level the server accepts (your level + MentorLevelGap) up. */
  private defaultRange(): void {
    const gap = this.rules()?.MentorLevelGap ?? 0;
    this.minIn.value = String(Math.min(65535, this.me().level + gap));
    this.maxIn.value = String(Math.min(65535, this.me().level + gap + 30));
  }

  // — Actions —

  private search(): void {
    if (this.mentor.mentorLevel !== 0) return;
    const lo = Number(this.minIn.value) || 1, hi = Number(this.maxIn.value) || 65535;
    if (!this.mentor.findMentor(lo, hi)) { setText(this.boardNote, "Searching too fast: wait a moment."); return; }
    setText(this.boardNote, "Searching…");
    // The server's 3 s throttle: the button rests meanwhile (C.FindMentor sooner only earns a chat line).
    this.searchBtn.setDisabled(true, "Wait a moment between searches");
    this.cleared?.();
    this.cleared = later(() => { this.cleared = null; this.searchBtn.setDisabled(this.mentor.mentorLevel !== 0, "You already have a mentorship"); }, FIND_MENTOR_THROTTLE, this.signal);
  }

  private request(name: string): void {
    if (!this.mentor.canAdd()) return;
    this.mentor.addMentor(name);
    this.sentTo = name;
    this.renderBoard();
  }

  private async addByName(): Promise<void> {
    if (!this.mentor.canAdd()) return;
    const name = await this.ui.rt.popups.input({ title: "Add mentor", text: "Please enter the name of the person you would like to be your Mentor.", max: 20 });
    if (name && this.mentor.mentorLevel === 0) { this.mentor.addMentor(name); this.sentTo = name; this.renderBoard(); }
  }

  private async cancel(): Promise<void> {
    if (!this.mentor.canCancel()) return;
    const r = this.rules(), partner = this.mentor.mentorName, online = this.mentor.mentorOnline;
    const days = r ? `${r.MentorLength} day${r.MentorLength === 1 ? "" : "s"}` : "a while";
    const bank = online
      ? `The banked EXP is paid now, as ${partner} is online.`
      : `${partner} is offline, so the EXP banked this session is lost.`;
    const yes = await this.ui.rt.popups.ask(`Cancelling a mentorship early starts a cooldown: you can't start another one for ${days}.\n${bank}\n\nEnd the mentorship with ${partner}?`,
      { title: "End mentorship", kind: "danger" });
    if (yes && this.mentor.mentorName === partner) this.mentor.cancelMentor();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    this.liveKey = this.stateKey();
    const r = this.rules(), me = this.me(), mt = this.mentor, bond = mt.mentorLevel !== 0;
    const amMentor = bond && me.level > mt.mentorLevel;
    const meSide: Side = { name: me.name, cls: me.cls, level: me.level, online: true, you: true, clsKnown: true };
    const partner: Side = { name: mt.mentorName, cls: mt.hasDetails ? mt.partnerClass : me.cls, level: mt.mentorLevel, online: mt.mentorOnline, you: false, clsKnown: mt.hasDetails };
    const [mentorS, menteeS] = amMentor ? [meSide, partner] : [partner, meSide];
    const grouped = this.grouped(), nearby = this.partnerNearby();
    const expOn = bond && mt.mentorOnline && grouped && nearby && (amMentor || this.hasBuff(BuffType.Mentee));

    // Banner
    if (bond) {
      const term = mt.hasDetails ? this.term() : null;
      const dmg = r ? `+${r.MentorDamageBoost}% damage` : "Damage buff", exp = r ? `+${r.MentorExpBoost}% EXP` : "EXP buff";
      const dmgChip = h("span", "bf" + (mt.mentorOnline ? " on" : ""), svg("sword", ""), dmg);
      dmgChip.title = "The mentor's buff: on while you are both online";
      const expChip = h("span", "bf" + (expOn ? " on" : ""), svg("star", ""), exp);
      expChip.title = "The mentee's EXP bonus: only while the mentor is in the mentee's group, nearby and alive";
      this.ring.el.hidden = !term;
      if (term) {
        this.ring.set(term.frac, term.left, "left");
        this.ring.el.title = `${fmtDate(mt.mentorDateMs)} – ${fmtDate(mt.endsAtMs)}`;
      }
      this.ban.replaceChildren(this.medal(mentorS, "MENTOR"), h("div", "mcen", this.ring.el,
        term ? null : h("div", "t", mt.mentorOnline ? "Mentorship active" : "Partner offline"), h("div", "mbuffs", dmgChip, expChip)),
      this.medal(menteeS, "MENTEE", true));
    } else {
      const empty = h("div", "who r", h("div", "med off", "?", h("em", "", "MENTOR")), h("div", "nm", h("b", "", "Nobody yet"), h("small", "", "Find a mentor below")));
      this.ban.replaceChildren(this.medal(meSide, "YOU"), h("div", "mcen", h("div", "t", "No mentorship")), empty);
    }

    // Left column: the bond, or how to get one
    const a: HTMLElement[] = [];
    if (bond) {
      const mentee = menteeS, mentorLv = mentorS.level;
      if (r) {
        const grad = mentorLv - r.MentorLevelGap + 1;
        a.push(this.label("cap", "Graduation"), h("div", "box", `${mentee.you ? "You graduate" : `${mentee.name} graduates`} on reaching level `, h("b", "", String(grad)),
          ` (the mentor must stay ${r.MentorLevelGap}+ levels ahead). The mentorship then ends.`));
      }
      if (amMentor) {
        a.push(this.label("bank", "EXP bank"), h("div", "box bank", h("div", "big", fmt(mt.menteeExp)),
          h("small", "", `${r ? `${r.MenteeExpBank}% of ` : "Part of "}${mentee.name}'s EXP is banked for you and paid when the mentorship ends, if ${mentee.name} is online then.`)));
      }
      const perks: [boolean, string, string][] = [
        [mt.mentorOnline, r ? `Mentor buff: +${r.MentorDamageBoost}% damage` : "Mentor buff: more damage", "both online"],
        [expOn, r ? `Mentee buff: +${r.MentorExpBoost}% EXP` : "Mentee buff: more EXP", "grouped and nearby"],
      ];
      if (r?.MentorSkillBoost) perks.push([mt.mentorOnline && grouped && nearby, "Mentor skill training ×2", "grouped and nearby"]);
      if (!r || r.MenteeExpBank > 0) perks.push([true, r ? `Banking ${r.MenteeExpBank}% of the mentee's EXP` : "Banking the mentee's EXP", "always"]);
      const pk = h("div", "box");
      for (const [on, t, s] of perks) pk.append(h("div", "perk" + (on ? " on" : ""), h("u"), h("span", "", t), h("em", "", s)));
      a.push(this.label("star", "Perks"), pk);
      const g = this.m.group;
      const invite = button("Group invite", () => g.addMember(mt.mentorName), { cls: "main", icon: "group", title: `Invite ${mt.mentorName} to your group` });
      if (!mt.mentorOnline) invite.setDisabled(true, `${mt.mentorName} is offline`);
      else if (grouped) invite.setDisabled(true, "Already in your group");
      else if (!g.isLeader(me.name)) invite.setDisabled(true, "Only the group leader can invite");
      const talk = button("Mentor chat", () => this.ui.chat.prefill("!#"), { icon: "chat", title: "Talk to your mentor / mentee (the !# prefix in the chat box)" });
      const end = button("End mentorship", () => void this.cancel(), { cls: "warn", icon: "cross" });
      a.push(h("div", "row", invite.el, talk.el), h("div", "row", end.el));
    } else {
      a.push(h("div", "box", h("b", "", "You have no mentorship. "),
        "A lower-level player asks a higher-level player of the same class to be their mentor. To be a mentor, keep mentor requests allowed and wait for a request."));
    }
    a.push(this.allowRow);
    this.colA.replaceChildren(...a);

    // Right column: the term details and the rules, plus the find board while free
    const b: HTMLElement[] = [];
    if (bond && mt.hasDetails) {
      b.push(this.label("calendar", "Term"), h("div", "box", "Started ", h("b", "", fmtDate(mt.mentorDateMs)), ", ends ", h("b", "", fmtDate(mt.endsAtMs)),
        `. ${partner.name} is a level ${partner.level} ${className(partner.cls)}.`));
    }
    if (!bond) {
      this.searchBtn.setDisabled(this.cleared !== null, "Wait a moment between searches");
      b.push(this.label("book", "Find a mentor", r ? `${r.MentorLevelGap}+ levels above you, same class` : "same class, higher level"), this.findBox);
      this.renderBoard();
    }
    if (r) {
      const rules = h("div", "rules");
      const rows: [string, string][] = [
        [`${r.MentorLevelGap}+`, "levels between mentor and mentee (same class)"], [`${r.MentorLength} days`, "per mentorship; cancelling waits as long"],
        [`+${r.MentorDamageBoost}%`, "damage for the mentor while both are online"], [`+${r.MentorExpBoost}%`, "EXP for the mentee, grouped with the mentor nearby"],
        [`${r.MenteeExpBank}%`, "of the mentee's EXP banked for the mentor"],
      ];
      if (r.MentorSkillBoost) rows.push(["×2", "mentor skill training when grouped nearby"]);
      for (const [v, t] of rows) rules.append(h("div", "", h("b", "", v), t));
      b.push(this.label("info", "How it works"), rules);
    }
    this.colB.replaceChildren(...b);
  }

  /** Days left in the term and the elapsed fraction (S.MentorDetails). */
  private term(): { frac: number; left: string } | null {
    const s = this.mentor.mentorDateMs, e = this.mentor.endsAtMs;
    if (!s || !e || e <= s) return null;
    const now = Date.now(), leftMs = Math.max(0, e - now);
    const left = leftMs >= DAY ? `${Math.ceil(leftMs / DAY)}d` : `${Math.ceil(leftMs / 3_600_000)}h`;
    return { frac: (now - s) / (e - s), left };
  }

  private renderBoard(): void {
    if (!this.open || this.mentor.mentorLevel !== 0) return;
    const list: readonly ClientMentorCandidate[] = this.mentor.candidates;
    if (!this.searched) { setText(this.boardNote, "Search for online players who could mentor you."); this.board.replaceChildren(); return; }
    setText(this.boardNote, list.length ? `${list.length} player${list.length === 1 ? "" : "s"} found.` : "Nobody online fits that range right now.");
    this.board.replaceChildren(...list.map((c) => {
      const sent = this.sentTo === c.Name;
      const req = button(sent ? "Sent" : "Request", () => this.request(c.Name), { cls: sent ? "" : "main", title: `Ask ${c.Name} to be your mentor` });
      if (sent) req.setDisabled(true, "Request sent: wait for the answer");
      const tg = h("div", "tg", classTag(c.Class));
      tg.style.setProperty("--cc", classVar(c.Class));
      return h("div", "bd", tg, h("div", "n", h("b", "", c.Name), h("small", "", `Lv ${c.Level} ${className(c.Class)}`)), req.el);
    }));
  }

  private label(icon: Parameters<typeof svg>[0], text: string, right?: string): HTMLElement {
    return h("div", "lbl sl", svg(icon, ""), text, right ? h("b", "", right) : null);
  }

  private medal(p: Side, tag: string, right = false): HTMLElement {
    const u = h("u");
    u.hidden = p.you;
    const m = h("div", "med" + (p.online ? "" : " off"), p.clsKnown ? classTag(p.cls) : "?", u, h("em", "", tag));
    if (p.clsKnown) m.style.setProperty("--cc", classVar(p.cls));
    const sub = p.clsKnown ? `Lv ${p.level} ${className(p.cls)}` : `Lv ${p.level}`;
    return h("div", "who" + (right ? " r" : ""), m, h("div", "nm", p.you ? h("span", "you", "YOU") : null, h("b", "", p.name), h("small", "", sub),
      p.you ? null : h("small", "", p.online ? "Online" : "Offline")));
  }
}

registerWindow(MENTOR_WINDOW, (host) => new MentorWindow(host), {
  // GameScene.MentorRequest: a request toast (Accept / Decline; declines when it runs out), wired at UI start.
  watch: (host) => {
    const sig = host.rt.signal;
    return host.ctx.models.mentor.request.on(({ name, level }) => {
      const cls = MirClassName[host.ctx.player.user?.look.class ?? 0] ?? "";
      void host.rt.popups.request({ from: name, tag: "Mentor", text: `(level ${level}) asks you to teach them the ways of the ${cls}.`, seconds: REQUEST_SECONDS })
        .then((yes) => { if (!sig.aborted) host.ctx.models.mentor.answerMentor(yes); });
    });
  },
});

