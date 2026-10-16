// Ported from play/hudRelationship.ts: the new UI's relationship (marriage) window over the real Relationship model
// (src/game/relationship.ts: S.LoverUpdate lover name / date / map / married days, the marriage and divorce requests),
// the account allow toggles (S.AllowToggles: Marriage, LoverRecall), S.ServerRules (the MarriageSystem.ini numbers), the
// user's worn ring (Inventory equipment RingL, UserItem.WeddingRing) and buffs (the Lover buff). Desktop counterpart:
// RelationshipDialog + GameScene.MarriageRequest / DivorceRequest / LoverUpdate; the classic port is the
// RelationshipWindow in src/ui/relationshipWindow.ts (mail / whisper refusals kept).
//
// Server rules (PlayerObject.MarriageRequest / MarriageReply / DivorceRequest / DivorceReply, "RECALLLOVER", HumanObject's
// Lover buff): a proposal goes to the player standing on the cell you face, who must face you; both level
// MarriageLevelRequired+, neither married nor within MarriageCooldown days of a divorce, the partner allowing marriage;
// the partner answers. A divorce also needs you to face your lover, and your lover's reply. While both are online each
// has the Lover buff (+LoverEXPBonus % EXP). @recalllover needs WeddingRingRecall on the server, both wearing the
// wedding ring on the left ring finger, a map that allows recall, the lover allowing recall, and once a minute at most.
// Changes from the mock:
//  - no candidate list: the proposal goes to the player you face (shown when the client sees one there); the checklist
//    has only what the client knows (your level, the divorce cooldown, someone in front of you; the partner's level /
//    allow / married state are checked by the server, which answers in chat);
//  - Request divorce asks to confirm, then waits for the lover's answer (no one-click divorce);
//  - recall sends "@recalllover"; the ring check covers your own ring only; whisper pre-fills ":)" in the chat box;
//    mail opens the mail window's compose (composeTo, when the mail window has it);
//  - HIDDEN (no data): memories timeline, love letters, recall count, EXP together, the lover's ring, the recall
//    cooldown, ring replacement (an NPC service: "Speak to ..." hint); the lover's class / level show only while the
//    lover is in view.
// Incoming marriage / divorce requests are request toasts wired at UI start (the registry's watch hook).
// Narrow layout (a layer below 1040 px): one column.

import { AllowToggle, BuffType, EquipmentSlot, ObjectType } from "../../../protocol/enums.generated.ts";
import { pointMove, type MapObj } from "../../../game/objects.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import type { Relationship } from "../../../game/relationship.ts";
import { rulesFor, serverRules, type ServerRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, classTag, classVar, className, fmtDate, h, injectCss, pretty, svg, toggleSwitch,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.relationship). */
export const RELATIONSHIP_WINDOW = "relationship";

const DAY = 86_400_000;
/** Seconds an incoming marriage / divorce request toast stays up (it declines when it runs out). */
const REQUEST_SECONDS = 30;
/** DateTime(2000, 1, 1) in ms: LoverUpdate.Date before it means "never" (RelationshipDialog: Date < new DateTime(2000)). */
const Y2000 = Date.UTC(2000, 0, 1);

const CSS = `
.hudm .rlwin { width: 760px; }
.hudm.narrow .rlwin { width: 500px; }
.hudm .rlwin .panel { display: flex; flex-direction: column; max-height: 600px; overflow: hidden; }
.hudm .rlwin .ban { display: flex; align-items: center; gap: 14px; padding: 14px 18px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 100% at 50% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .rlwin .who { flex: 1; min-width: 0; display: flex; align-items: center; gap: 10px; } .hudm .rlwin .who.r { flex-direction: row-reverse; text-align: right; }
.hudm .rlwin .med { position: relative; width: 56px; height: 56px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 13px; font-weight: 800; color: #fff;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 2px solid var(--cc, var(--ac)); box-shadow: 0 0 0 3px #000; }
.hudm .rlwin .med.off { border-color: var(--fr); color: var(--dim); }
.hudm .rlwin .med u { position: absolute; right: 1px; bottom: 1px; width: 11px; height: 11px; border-radius: 50%; border: 2px solid #000; background: var(--ok); }
.hudm .rlwin .med.off u { background: #555; } .hudm .rlwin .med u[hidden] { display: none; }
.hudm .rlwin .who .nm { min-width: 0; } .hudm .rlwin .who b { display: block; font-size: 15px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .rlwin .who small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rlwin .who .you { color: var(--ac); font-size: 10px; font-weight: 700; letter-spacing: .1em; }
.hudm .rlwin .hc { width: 190px; flex: none; display: flex; flex-direction: column; align-items: center; gap: 4px; text-align: center; }
.hudm.narrow .rlwin .hc { width: 130px; }
.hudm .rlwin .heart { position: relative; width: 60px; height: 60px; color: var(--ac); }
.hudm .rlwin .heart svg { width: 60px; height: 60px; fill: color-mix(in srgb, var(--ac) 70%, #000); stroke: #fff; stroke-width: 1; }
.hudm .rlwin .heart.beat { animation: hudm-rlbeat 1.4s ease-in-out infinite; }
@keyframes hudm-rlbeat { 0%, 40%, 100% { transform: scale(1); } 15% { transform: scale(1.1); } 25% { transform: scale(.98); } }
.hudm .rlwin .heart.off svg { fill: color-mix(in srgb, var(--pb) 80%, #000); stroke: var(--fr); }
.hudm .rlwin .heart.none svg { fill: color-mix(in srgb, var(--pb) 60%, #000); stroke: var(--fr); stroke-dasharray: 3 3; }
.hudm .rlwin .heart b { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding-bottom: 4px; font-size: 14px; font-weight: 800; color: #fff; text-shadow: 0 1px 3px #000; }
.hudm .rlwin .hc .t { font-size: 11px; color: var(--dim); } .hudm .rlwin .hc .t b { color: #fff; }
.hudm .rlwin .chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 9px; border-radius: 10px; font-size: 10px; font-weight: 700; border: 1px solid var(--fr); color: var(--dim); }
.hudm .rlwin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 25%, transparent); } .hudm .rlwin .chip svg { width: 12px; height: 12px; }
.hudm .rlwin .body { flex: 1; min-height: 0; display: flex; }
.hudm.narrow .rlwin .body { flex-direction: column; overflow-y: auto; }
.hudm .rlwin .col { padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; min-width: 0; }
.hudm .rlwin .col > * { flex-shrink: 0; }
.hudm .rlwin .col.a { width: 340px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); } .hudm .rlwin .col.b { flex: 1; }
.hudm.narrow .rlwin .col { overflow: visible; } .hudm.narrow .rlwin .col.a { width: auto; border-right: 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rlwin .sl { display: flex; align-items: center; gap: 6px; }
.hudm .rlwin .sl svg { color: var(--ac); width: 14px; height: 14px; } .hudm .rlwin .sl > b { margin-left: auto; font-size: 11px; font-weight: 600; color: var(--tx); letter-spacing: 0; text-transform: none; }
.hudm .rlwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; line-height: 1.45; }
.hudm .rlwin .box b { color: var(--tx); }
.hudm .rlwin .ck { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 3px 0; }
.hudm .rlwin .ck u { width: 16px; height: 16px; flex: none; border-radius: 50%; text-decoration: none; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800; color: #fff; }
.hudm .rlwin .ck.y u { background: color-mix(in srgb, var(--ok) 60%, #000); } .hudm .rlwin .ck.n u { background: color-mix(in srgb, var(--bad) 60%, #000); } .hudm .rlwin .ck.n { color: var(--dim); }
.hudm .rlwin .ck.q u { background: var(--fr); }
.hudm .rlwin .row { display: flex; flex-wrap: wrap; gap: 6px; } .hudm .rlwin .row .btn { flex: 1; }
.hudm .rlwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; } .hudm .rlwin .note b { color: var(--tx); }
.hudm .rlwin .stats { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; }
.hudm .rlwin .stats div { text-align: center; padding: 8px 4px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); font-size: 10px; color: var(--dim); }
.hudm .rlwin .stats b { display: block; font-size: 15px; color: #fff; }
.hudm .rlwin .tog { display: flex; align-items: center; gap: 8px; font-size: 12px; }
.hudm .rlwin .how > div + div { margin-top: 6px; }
`;

class RelationshipWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly rel: Relationship;
  /** The derived state the 1 s check compares (the faced player, the lover in view, buffs, ring, level). */
  private liveKey = "";

  // — DOM, built once —
  private readonly ban = h("div", "ban");
  private readonly colA = h("div", "col a");
  private readonly colB = h("div", "col b");
  private readonly marrySw: ReturnType<typeof toggleSwitch>;
  private readonly recallSw: ReturnType<typeof toggleSwitch>;
  private readonly marryRow: HTMLElement;
  private readonly recallRow: HTMLElement;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "rlwin", null, 60);
    injectCss("win-relationship", CSS);
    this.m = ui.ctx.models;
    this.rel = this.m.relationship;
    const acct = this.m.account;

    // C.SetAllowToggle (the server answers S.AllowToggles; the switch moves then). An older server only has
    // C.ChangeMarriage, which flips Marriage while single and LoverRecall while married.
    const toggle = (t: AllowToggle, cur: () => boolean, sw: () => ReturnType<typeof toggleSwitch>) => () => {
      sw().set(cur());
      if (acct.togglesKnown) acct.setToggle(t, !cur());
      else this.rel.changeMarriage();
    };
    this.marrySw = toggleSwitch(acct.allowMarriage, toggle(AllowToggle.Marriage, () => acct.allowMarriage, () => this.marrySw), { small: true, label: "Allow marriage requests" });
    this.recallSw = toggleSwitch(acct.allowLoverRecall, toggle(AllowToggle.LoverRecall, () => acct.allowLoverRecall, () => this.recallSw), { small: true, label: "Allow lover recall" });
    this.marryRow = h("div", "tog", this.marrySw.el, "Allow marriage requests");
    this.recallRow = h("div", "tog", this.recallSw.el, "Let my lover recall me");

    this.el.append(h("div", "panel", this.titleBar("RELATIONSHIP", "Close"), this.ban, h("div", "body", this.colA, this.colB)));

    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    const inv = () => this.invalidate();
    on(this.rel.changed.on(inv));
    on(acct.togglesChanged.on(() => { this.marrySw.set(acct.allowMarriage); this.recallSw.set(acct.allowLoverRecall); }));
    on(serverRules(ui.ctx.session).changed.on(inv));
    on(this.m.inventory.subscribe(() => { if (this.open && this.stateKey() !== this.liveKey) this.invalidate(); }));
    // The faced player, the lover coming into view, the Lover buff and the user's level have no event here: a cheap
    // check once a second while shown.
    this.everyShown(1000, () => { if (this.stateKey() !== this.liveKey) this.invalidate(); });
  }

  protected override shown(): void {
    this.marrySw.set(this.m.account.allowMarriage);
    this.recallSw.set(this.m.account.allowLoverRecall);
    this.invalidate();
  }

  // — Derived state —

  private rules(): ServerRules | null { return rulesFor(this.ui.ctx.session); }
  private married(): boolean { return this.rel.loverName !== ""; }
  /** LoverUpdate.Date as ms (the marriage date while married, else the divorce date); 0 = never. */
  private dateMs(): number {
    const ms = dateFromBinary(this.rel.date).getTime();
    return Number.isFinite(ms) && ms >= Y2000 ? ms : 0;
  }
  /** Wearing a wedding ring on the left ring finger (UserItem.WeddingRing set; the server matches it to the lover). */
  private ringOn(): boolean {
    const ring = this.m.inventory.equipment[EquipmentSlot.RingL];
    return !!ring && ring.WeddingRing !== -1;
  }
  private hasLoverBuff(): boolean { return this.ui.ctx.player.buffs.some((b) => b.Type === BuffType.Lover); }
  private others(): Iterable<MapObj> {
    const out: MapObj[] = [];
    for (const o of this.ui.ctx.world.objects.values()) if (o.race === ObjectType.Player && !o.ownerName && !o.isUser) out.push(o);
    return out;
  }
  /** The lover's map object while in view (class / level only come from there). */
  private loverInView(): MapObj | null {
    const n = this.rel.loverName;
    if (!n) return null;
    for (const o of this.others()) if (o.name === n) return o;
    return null;
  }
  /** The player on the cell the user faces (PlayerObject.MarriageRequest / DivorceRequest look there), and whether they
   *  face the user (Functions.FacingEachOther). */
  private faced(): { obj: MapObj; facing: boolean } | null {
    const u = this.ui.ctx.player.user;
    if (!u) return null;
    const front = pointMove(u.location, u.direction, 1);
    for (const o of this.others()) {
      if (o.location.X === front.X && o.location.Y === front.Y) return { obj: o, facing: o.direction === ((u.direction + 4) & 7) };
    }
    return null;
  }
  /** Ms until the divorce cooldown ends (0 = none or unknown). */
  private cooldownLeft(r: ServerRules | null): number {
    if (!r || this.married()) return 0;
    const d = this.dateMs();
    return d ? Math.max(0, d + r.MarriageCooldown * DAY - Date.now()) : 0;
  }
  private stateKey(): string {
    const f = this.faced(), l = this.loverInView();
    return [this.ui.ctx.player.level, this.ringOn(), this.hasLoverBuff(), f ? `${f.obj.name}:${f.facing}` : "",
      l ? `${l.look.class}:${l.level}:${l.dead}` : "", Math.floor(Date.now() / 60_000)].join("|");
  }

  // — Actions —

  private propose(): void { this.rel.requestMarriage(); }

  private async divorce(): Promise<void> {
    const lover = this.rel.loverName;
    if (!lover) { this.rel.requestDivorce(); return; } // the model's "You're not married." line
    const r = this.rules();
    const cd = r ? ` Neither of you can marry again for ${r.MarriageCooldown} day${r.MarriageCooldown === 1 ? "" : "s"}.` : "";
    const yes = await this.ui.rt.popups.ask(`Stand next to ${lover}, facing each other: ${lover} is asked to agree to the divorce.${cd}\n\nAsk ${lover} for a divorce?`,
      { title: "Request divorce", kind: "danger" });
    if (yes && this.rel.loverName === lover) this.rel.requestDivorce();
  }

  private recall(): void { this.m.chatLog.submit("@recalllover"); }

  /** MailButton: MailComposeLetterDialog.ComposeMail(LoverName). */
  private mail(): void {
    const w = this.ui.rt.window<UiWindow & { composeTo?: (name: string) => void }>("mail");
    if (!w) return;
    if (typeof w.composeTo === "function") w.composeTo(this.rel.loverName);
    else this.ui.openWindow("mail", true);
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    this.liveKey = this.stateKey();
    const r = this.rules(), rel = this.rel, married = this.married();
    const u = this.ui.ctx.player.user, myLevel = this.ui.ctx.player.level, myCls = u?.look.class ?? 0;
    const online = married && rel.mapName !== "", dateMs = this.dateMs();

    // Banner
    const me = this.medal(u?.name ?? "", myCls, `Lv ${myLevel} ${className(myCls)}`, true, true, false);
    if (married) {
      const l = this.loverInView();
      const sub = l ? `Lv ${l.level || "?"} ${className(l.look.class)}` : "";
      const lover = this.medal(rel.loverName, l ? l.look.class : -1, sub, online, false, true, online ? pretty(rel.mapName) : "Offline");
      const heart = h("div", "heart" + (online ? " beat" : " off"), svg("heartFull", ""), h("b", "", String(rel.marriedDays)));
      heart.title = `${rel.marriedDays} days together`;
      const buff = h("span", "chip" + (this.hasLoverBuff() ? " on" : ""), svg("star", ""), r ? `Lover buff +${r.LoverEXPBonus}% EXP` : "Lover buff");
      buff.title = "+EXP for both of you while you are both online";
      this.ban.replaceChildren(me, h("div", "hc", heart, h("div", "t", h("b", "", `${rel.marriedDays} days`), " together"), buff), lover);
    } else {
      const left = this.cooldownLeft(r);
      const t = left ? h("div", "t", h("b", "", `${Math.ceil(left / DAY)} days`), " before you can marry again") : h("div", "t", h("b", "", "Single"));
      this.ban.replaceChildren(me, h("div", "hc", h("div", "heart none", svg("heartFull", "")), t),
        h("div", "who r", h("div", "med off", "?"), h("div", "nm", h("b", "", "Nobody yet"), h("small", "", "Propose below"))));
    }

    // Left column
    const a: HTMLElement[] = [];
    if (married) {
      a.push(h("div", "stats", h("div", "", h("b", "", String(rel.marriedDays)), "days together"),
        h("div", "", h("b", "", dateMs ? fmtDate(dateMs) : "—"), "wedding day"), h("div", "", h("b", "", online ? "Online" : "Offline"), "your lover")));
      // Recall (@recalllover)
      const ring = this.ringOn(), serverOk = r ? r.WeddingRingRecall : null;
      const checks = h("div", "box",
        this.check(serverOk, serverOk === false ? "Wedding ring recall is off on this server" : "Wedding ring recall allowed on this server"),
        this.check(ring, "You wear the wedding ring on your left ring finger"),
        this.check(online, `${rel.loverName} is online`),
        h("div", "note", `${rel.loverName} must wear their wedding ring on the left too, allow recall, and the map must allow recall. Once a minute at most.`));
      const recall = button(`Recall ${rel.loverName}`, () => this.recall(), { cls: "main", icon: "recall", title: "@recalllover" });
      if (serverOk === false) recall.setDisabled(true, "Wedding ring recall is off on this server");
      else if (!ring) recall.setDisabled(true, "Wear your wedding ring on the left ring finger");
      else if (!online) recall.setDisabled(true, `${rel.loverName} is offline`);
      a.push(this.label("recall", "Recall lover", "@recalllover"), checks, h("div", "row", recall.el));
      const whisper = button("Lover chat", () => this.ui.chat.prefill(":)"), { icon: "chat", title: "Talk to your lover (the :) prefix in the chat box)" });
      if (!online) whisper.setDisabled(true, "Lover is not online");
      const mail = button("Mail", () => this.mail(), { icon: "mail", title: `Send ${rel.loverName} a letter` });
      const div = button("Request divorce", () => void this.divorce(), { cls: "warn", icon: "cross", title: "Needs you to face your lover, and their agreement" });
      a.push(h("div", "row", whisper.el, mail.el), h("div", "row", div.el), this.recallRow);
    } else {
      a.push(...this.proposal(r, myLevel));
      a.push(this.marryRow);
    }
    this.colA.replaceChildren(...a);

    // Right column: details and how it works
    const b: HTMLElement[] = [];
    if (married) {
      b.push(this.label("calendar", "Marriage"), h("div", "box",
        "Married to ", h("b", "", rel.loverName), dateMs ? ` on ${fmtDate(dateMs)}` : "", ` (${rel.marriedDays} days). `,
        online ? `${rel.loverName} is in ${pretty(rel.mapName)}.` : `${rel.loverName} is offline.`));
      b.push(h("div", "note", "A lost wedding ring can be replaced: speak to the marriage NPC."));
    } else if (dateMs) {
      b.push(h("div", "box", "Divorced on ", h("b", "", fmtDate(dateMs)), ` (${rel.marriedDays} days ago).`));
    }
    const lvl = r ? `level ${r.MarriageLevelRequired} or higher` : "high enough level";
    b.push(this.label("info", "How marriage works"), h("div", "box how",
      h("div", "", h("b", "", "Proposing: "), `both of you must be ${lvl}, standing next to each other and facing each other, and the other player must allow marriage requests.`),
      h("div", "", h("b", "", "Lover buff: "), r ? `+${r.LoverEXPBonus}% EXP for both while you are both online.` : "more EXP for both while you are both online."),
      h("div", "", h("b", "", "Wedding rings: "), r?.WeddingRingRecall === false ? "worn on the left ring finger. Ring recall is off on this server." : "worn on the left ring finger they let you use @recalllover."),
      h("div", "", h("b", "", "Chat: "), "start a line with :) to talk to your partner."),
      h("div", "", h("b", "", "Divorce: "), `face your partner and ask; your partner must agree. ${r ? `Neither of you can marry for ${r.MarriageCooldown} days after.` : ""}`)));
    this.colB.replaceChildren(...b);
  }

  /** Not married: the proposal checklist (only what the client knows) and the button. */
  private proposal(r: ServerRules | null, myLevel: number): HTMLElement[] {
    const f = this.faced(), left = this.cooldownLeft(r);
    const lvOk = r ? myLevel >= r.MarriageLevelRequired : null;
    const checks = h("div", "box",
      this.check(lvOk, r ? `You are level ${r.MarriageLevelRequired}+` : "You are of marrying level"),
      this.check(left === 0, left ? `Divorce cooldown: ${Math.ceil(left / DAY)} days left` : "No divorce cooldown"),
      this.check(!!f, f ? `Facing ${f.obj.name}` : "Stand next to the player you want to marry and face them"),
      f ? this.check(f.facing, f.facing ? `${f.obj.name} faces you` : `${f.obj.name} must face you`) : null,
      h("div", "note", "The other player's level, marriage setting and cooldown are checked by the server."));
    const p = button(f ? `Propose to ${f.obj.name}` : "Propose", () => this.propose(), { cls: "main", icon: "heartFull", title: "C.MarriageRequest to the player you face" });
    if (lvOk === false) p.setDisabled(true, `You must be level ${r!.MarriageLevelRequired} or higher`);
    else if (left) p.setDisabled(true, "You divorced too recently");
    else if (!f) p.setDisabled(true, "Face the player you want to marry");
    return [this.label("heart", "Propose"), checks, h("div", "row", p.el)];
  }

  /** A checklist line; ok = null shows "unknown" (no rule from the server yet). */
  private check(ok: boolean | null, text: string): HTMLElement {
    return h("div", "ck " + (ok === null ? "q" : ok ? "y" : "n"), h("u", "", ok === null ? "?" : ok ? "✓" : "✕"), text);
  }

  private label(icon: Parameters<typeof svg>[0], text: string, right?: string): HTMLElement {
    return h("div", "lbl sl", svg(icon, ""), text, right ? h("b", "", right) : null);
  }

  /** cls -1 = unknown (the lover out of view). */
  private medal(name: string, cls: number, sub: string, online: boolean, you: boolean, right: boolean, where?: string): HTMLElement {
    const u = h("u");
    u.hidden = you;
    const m = h("div", "med" + (online ? "" : " off"), cls >= 0 ? classTag(cls) : "?", u);
    if (cls >= 0) m.style.setProperty("--cc", classVar(cls));
    return h("div", "who" + (right ? " r" : ""), m, h("div", "nm", you ? h("span", "you", "YOU") : null, h("b", "", name), sub ? h("small", "", sub) : null,
      where ? h("small", "", where) : null));
  }
}

registerWindow(RELATIONSHIP_WINDOW, (host) => new RelationshipWindow(host), {
  // GameScene.MarriageRequest / DivorceRequest: request toasts (Accept / Decline; decline when they run out), wired at UI start.
  watch: (host) => {
    const rel = host.ctx.models.relationship, sig = host.rt.signal;
    const offs = [
      rel.marriageRequest.on(({ name }) => {
        void host.rt.popups.request({ from: name, tag: "Marriage", text: "has asked for your hand in marriage.", seconds: REQUEST_SECONDS, accept: "Marry", decline: "Refuse" })
          .then((yes) => { if (!sig.aborted) rel.answerMarriage(yes); });
      }),
      rel.divorceRequest.on(({ name }) => {
        void host.rt.popups.request({ from: name, tag: "Divorce", text: "has requested a divorce.", seconds: REQUEST_SECONDS, accept: "Agree", decline: "Refuse" })
          .then((yes) => { if (!sig.aborted) rel.answerDivorce(yes); });
      }),
    ];
    return () => { for (const off of offs) off(); };
  },
});
