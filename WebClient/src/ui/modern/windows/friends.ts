// Ported from play/hudFriends.ts (FriendsMock): the new UI's friends window, a modern FriendDialog (Client/MirScenes/
// Dialogs/FriendDialog.cs + MemoDialog; the classic port is src/ui/friendWindow.ts) over the friends model
// (src/game/friends.ts, phase 2.7: mutual friendships through requests).
//
// Changes from the mock:
//  - real data only: ClientFriend rows (name, class badge, level, guild, online / last seen from LastSeen =
//    DateTime.ToBinary, memo, blocked). Opening sends C.RefreshFriends like FriendDialog.Show (S.FriendUpdate is only
//    pushed at log-in and after the user's own edits, so the online state goes stale while the window is closed);
//  - tabs Friends / Requests / Blocked. Requests lists the pending incoming S.FriendRequest entries with their local
//    60 s expiry (Friends.requests prunes); Accept / Decline answer through Friends.reply (C.FriendRequestReply). The
//    same request also shows as a request toast while this window is closed: modernUi.ts wires that at UI start
//    (m.friends.request -> rt.popups.request), so this window does not add a second one. The tab is hidden when the
//    server's S.ServerRules says friend requests are off (adding is then the instant one-sided add);
//  - add box: on Friends a friend REQUEST (C.AddFriend Blocked=false; the outcome comes back as chat lines and
//    S.FriendRequestResult, shown in the note under the box); on Blocked an instant one-sided block (Blocked=true);
//  - memo: C.AddMemo, 1-200 characters; like Crystal it cannot be cleared, only replaced (Save is off while empty);
//  - Remove (both sides for a friend, one-sided for a block: PlayerObject.RemoveFriend) and Block (an existing friend
//    becomes a block and is removed from their list: PlayerObject.BlockPlayer) ask first (popups). Nothing changes
//    locally: the row's buttons stay locked until the server's S.FriendUpdate (5 s fallback);
//  - actions: Whisper (online only, prefills "/name " in the chat box), Group invite (C.AddMember through the group
//    model; off while offline or already grouped), Mail (the mail window's composeTo(name) when one is registered),
//    Inspect (only while the friend is in view: an on-screen player object of that name, C.Inspect through the
//    inspect model);
//  - the "Allow friend requests" toggle (Account.allowFriendRequests, S.AllowToggles) is shown with a link to Options,
//    where the switch lives;
//  - the bonds strip shows the real lover (Relationship) and mentor bond (Mentor) and opens their windows; it is
//    hidden without a bond.
// HIDDEN (Crystal has no system for them): location, status message, favourites, the Recent tab, the activity feed,
// "friends since" and the whisper thread (whispers live in the chat box).
// Narrow layout (below a 1040 px layer): a narrower window and list column.

import type { ClientFriend } from "../../../protocol/server.ts";
import { FriendRequestResults } from "../../../protocol/server.ts";
import { ObjectType } from "../../../protocol/enums.generated.ts";
import { dateTimeBinaryToMs } from "../../../game/accountDetails.ts";
import type { FriendRequest, FriendRequestResult } from "../../../game/friends.ts";
import { serverRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, asButton, button, className, classTag, classVar, fmt, fmtAgo, h, injectCss, later, searchBox, setText, svg,
  tabs, typingField, type Btn,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.friends; hotkey KeybindOptions.Friends via MENU_WINDOWS). */
export const FRIENDS_KEY = "friends";

/** PlayerObject.AddMemo refuses an empty memo or one over 200 characters. */
const MAX_MEMO = 200;
/** How long a sent remove / block / memo keeps the row locked when no S.FriendUpdate comes back. */
const PENDING_MS = 5000;

type Tab = "Friends" | "Requests" | "Blocked";

const RESULT_TEXT: Readonly<Record<number, (n: string) => string>> = {
  [FriendRequestResults.Accepted]: (n) => `${n} accepted your friend request.`,
  [FriendRequestResults.Declined]: (n) => `${n} declined your friend request.`,
  [FriendRequestResults.Expired]: (n) => `Your friend request to ${n} expired.`,
  [FriendRequestResults.Offline]: (n) => `${n} is not online.`,
  [FriendRequestResults.ListFull]: (n) => `${n}'s friend list is full.`,
  [FriendRequestResults.NotAccepting]: (n) => `${n} is not accepting friend requests.`,
};

const CSS = `
.hudm .frwin { width: 700px; }
.hudm .frwin .panel { height: 520px; display: flex; flex-direction: column; overflow: hidden; }
/* Bonds strip */
.hudm .frwin .bonds { display: flex; gap: 10px; padding: 8px 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .frwin .bond { flex: 1; min-width: 0; display: flex; align-items: center; gap: 10px; padding: 6px 10px; border-radius: var(--rad);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .frwin .bond > svg { width: 20px; height: 20px; color: var(--ac); }
.hudm .frwin .bond .t { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .frwin .bond .t b { display: block; font-size: 12px; color: #fff; overflow: hidden; text-overflow: ellipsis; }
.hudm .frwin .bond .btn { height: 22px; padding: 0 9px; font-size: 10px; }
/* Body */
.hudm .frwin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .frwin .left { width: 290px; flex: none; display: flex; flex-direction: column; padding: 8px 8px 10px 12px; min-height: 0;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .frwin .ui-search { margin: 8px 0 4px; }
.hudm .frwin .ui-search input { width: 100%; box-sizing: border-box; }
.hudm .frwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .frwin .grp { display: flex; align-items: center; gap: 6px; height: 24px; margin-top: 4px; padding: 0 4px; cursor: pointer; user-select: none;
  font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .frwin .grp .chev { transition: transform .15s; font-size: 10px; width: 10px; }
.hudm .frwin .grp.shut .chev { transform: rotate(-90deg); }
.hudm .frwin .grp span:last-child { margin-left: auto; letter-spacing: 0; color: var(--dim); }
.hudm .frwin .row { display: flex; align-items: center; gap: 9px; padding: 6px 6px 6px 8px; margin-bottom: 3px; cursor: pointer; border: 1px solid transparent; border-radius: var(--rad);
  background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .frwin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .frwin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .frwin .row.off { opacity: .62; }
.hudm .frwin .row.req { cursor: default; }
.hudm .frwin .av { position: relative; width: 30px; height: 30px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  font-size: 9px; font-weight: 800; letter-spacing: .04em; color: var(--tx); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--cc, var(--fr)); }
.hudm .frwin .av::after { content: ""; position: absolute; right: -1px; bottom: -1px; width: 9px; height: 9px; border-radius: 50%; background: #59606e; border: 2px solid color-mix(in srgb, var(--pb) 90%, #000); }
.hudm .frwin .av.on::after { background: var(--ok); }
.hudm .frwin .av.nodot::after { display: none; }
.hudm .frwin .row .mid { flex: 1; min-width: 0; }
.hudm .frwin .row .nm { display: flex; align-items: center; gap: 6px; font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; }
.hudm .frwin .row .nm small { font-size: 10px; font-weight: 500; color: var(--dim); }
.hudm .frwin .row .sub { margin-top: 2px; font-size: 10px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .frwin .row .sub em { font-style: normal; color: color-mix(in srgb, var(--tx) 70%, var(--dim)); }
.hudm .frwin .row .btn { height: 22px; padding: 0 9px; font-size: 10px; }
.hudm .frwin .add { display: flex; gap: 6px; margin-top: 8px; }
.hudm .frwin .add input { flex: 1; min-width: 0; height: 26px; box-sizing: border-box; }
.hudm .frwin .add .btn { height: 26px; flex: none; }
.hudm .frwin .note { font-size: 10px; color: var(--dim); margin-top: 4px; min-height: 13px; }
.hudm .frwin .note.ok { color: var(--ac); }
.hudm .frwin .allow { display: flex; align-items: center; gap: 6px; margin-top: 4px; font-size: 10px; color: var(--dim); }
.hudm .frwin .allow b { color: var(--tx); font-weight: 600; } .hudm .frwin .allow b.off { color: var(--warn); }
.hudm .frwin .allow a { margin-left: auto; color: var(--acd); cursor: pointer; text-decoration: underline; }
.hudm .frwin .empty { padding: 26px 8px; text-align: center; color: var(--dim); font-size: 12px; }
/* Profile */
.hudm .frwin .detail { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .frwin .dscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 12px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .frwin .prof { display: flex; gap: 14px; align-items: center; }
.hudm .frwin .prof .av { width: 56px; height: 56px; font-size: 14px; border-width: 2px; }
.hudm .frwin .prof .av::after { width: 13px; height: 13px; right: 1px; bottom: 1px; }
.hudm .frwin .prof .who { min-width: 0; }
.hudm .frwin .prof h3 { margin: 0; font-size: 18px; color: #fff; display: flex; align-items: center; gap: 8px; overflow: hidden; }
.hudm .frwin .prof .pill { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .frwin .prof .pill.on { color: var(--ok); border-color: color-mix(in srgb, var(--ok) 60%, transparent); background: color-mix(in srgb, var(--ok) 15%, transparent); }
.hudm .frwin .prof .pill.blk { color: var(--bad-soft); border-color: color-mix(in srgb, var(--bad) 60%, transparent); }
.hudm .frwin .prof .meta { margin-top: 4px; font-size: 11px; color: var(--dim); }
.hudm .frwin .prof .meta b { color: var(--tx); font-weight: 600; }
.hudm .frwin .mlbl { display: flex; align-items: center; gap: 6px; margin-bottom: 5px; }
.hudm .frwin .mlbl svg { width: 13px; height: 13px; color: var(--acd); }
.hudm .frwin .mlbl small { margin-left: auto; font-size: 10px; font-weight: 500; letter-spacing: 0; text-transform: none; color: var(--dim); }
.hudm .frwin textarea.fld { width: 100%; box-sizing: border-box; height: 64px; font-size: 12px; line-height: 1.45; }
.hudm .frwin .msave { display: flex; align-items: center; gap: 8px; margin-top: 5px; font-size: 10px; color: var(--dim); }
.hudm .frwin .msave .btn { margin-left: auto; height: 24px; }
.hudm .frwin .actions { display: flex; flex-wrap: wrap; gap: 5px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .frwin .actions .btn { height: 26px; padding: 0 10px; white-space: nowrap; }
.hudm .frwin .actions .sp { flex: 1; }
.hudm.narrow .frwin { width: 600px; }
.hudm.narrow .frwin .left { width: 236px; }
.hudm.narrow .frwin .dscroll { padding: 12px 12px 8px; }
.hudm.narrow .frwin .actions { padding: 8px 12px 10px; }
.hudm.narrow .frwin .bond .btn { display: none; }
`;

class FriendsWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private tab: Tab = "Friends";
  private tabBar: ReturnType<typeof tabs> | null = null;
  private tabKey = "";
  private tabList: Tab[] = [];
  private query = "";
  /** The selected entry (ClientFriend.Index) on the Friends / Blocked tab. */
  private picked: number | null = null;
  private readonly shut = new Set<string>();
  /** Entries with a remove / block / memo on its way to the server (locked until S.FriendUpdate). */
  private readonly pending = new Map<number, () => void>();
  /** The unsaved memo text of the selected entry (kept across re-renders). */
  private memoDraft: { index: number; text: string } | null = null;
  private note = "";
  private noteOk = false;
  /** The selected friend's in-view player object id (Inspect), refreshed by the 1 s tick. */
  private inViewId = -1;
  /** The request rows' countdown labels (Requests tab), refreshed by the 1 s tick. */
  private reqTimers: { el: HTMLElement; r: FriendRequest }[] = [];

  // — DOM, built once —
  private readonly bonds = h("div", "bonds");
  private readonly left = h("div", "left");
  private readonly search: ReturnType<typeof searchBox>;
  private readonly list = h("div", "list");
  private readonly addRow = h("div", "add");
  private readonly addIn = typingField(h("input", "fld"));
  private readonly addBtn: Btn;
  private readonly noteEl = h("div", "note");
  private readonly allowEl = h("div", "allow");
  private readonly allowState = h("b");
  private readonly detail = h("div", "detail");
  private inspectBtn: Btn | null = null;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "friends frwin", 120, 50);
    injectCss("win-friends", CSS);
    this.m = ui.ctx.models;
    const m = this.m, sig = this.signal;

    this.search = searchBox("Search names, guilds, memos…", (q) => { this.query = q.toLowerCase(); this.invalidate(); }, { signal: sig });
    this.addIn.maxLength = 20;
    this.addIn.addEventListener("keydown", (e) => { if (e.key === "Enter") this.add(); }, { signal: sig });
    this.addBtn = button("Add", () => this.add(), { icon: "plus" });
    this.addRow.append(this.addIn, this.addBtn.el);
    const toOptions = asButton(h("a", "", "Options"), "Open Options");
    toOptions.addEventListener("click", () => { this.ui.openWindow("options", true); }, { signal: sig });
    this.allowEl.append("Accepting friend requests:", this.allowState, toOptions);
    this.allowEl.title = "The switch is in Options (Allow friend requests)";
    this.left.append(this.search.el, this.list, this.addRow, this.noteEl, this.allowEl);
    this.el.append(h("div", "panel", this.titleBar("FRIENDS", "Close (F)"), this.bonds, h("div", "cols", this.left, this.detail)));

    // Model events: invalidate (re-rendered on the next frame while open, else on the next open).
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(m.friends.changed.on(() => {
      for (const unlock of this.pending.values()) unlock();
      this.pending.clear();
      this.invalidate();
    }));
    on(m.friends.requestsChanged.on(() => this.invalidate()));
    on(m.friends.result.on(({ name, result }) => this.showResult(name, result)));
    on(m.account.togglesChanged.on(() => this.invalidate()));
    on(serverRules(ui.ctx.session).changed.on(() => this.invalidate()));
    on(m.relationship.changed.on(() => this.invalidate()));
    on(m.mentor.changed.on(() => this.invalidate()));
    on(m.group.changed.on(() => this.invalidate()));
    on(ui.rt.windowsChanged.on((k) => { if (k === "mail" || k === "options" || k === "relationship" || k === "mentor") this.invalidate(); }));

    // While shown: request countdowns, local expiry (Friends.requests prunes) and the Inspect button's in-view check.
    this.everyShown(1000, () => this.tick());
  }

  /** FriendDialog.Show: C.RefreshFriends (the server answers S.FriendUpdate). */
  protected override shown(): void {
    this.m.friends.refresh();
  }

  protected override closed(): void {
    this.memoDraft = null;
  }

  // — Helpers —

  private get requestsOn(): boolean {
    return serverRules(this.ui.ctx.session).rules?.FriendRequestsEnabled ?? true;
  }

  private entries(blocked: boolean): ClientFriend[] {
    return this.m.friends.list.filter((f) => f.Blocked === blocked);
  }

  private matches(...fields: string[]): boolean {
    const q = this.query;
    return !q || fields.some((s) => s.toLowerCase().includes(q));
  }

  private setNote(text: string, ok = false): void {
    this.note = text;
    this.noteOk = ok;
    setText(this.noteEl, text);
    this.noteEl.classList.toggle("ok", ok);
  }

  private showResult(name: string, result: FriendRequestResult): void {
    const text = RESULT_TEXT[result];
    if (text) this.setNote(text(name), result === FriendRequestResults.Accepted);
  }

  /** A class badge (MirClass tag, class colour ring); `dot` = online state. */
  private avatar(cls: number, online: boolean | null): HTMLElement {
    const av = h("div", "av" + (online === null ? " nodot" : online ? " on" : ""), classTag(cls));
    av.style.setProperty("--cc", classVar(cls));
    av.setAttribute("aria-hidden", "true");
    return av;
  }

  private lastSeen(f: ClientFriend): string {
    if (f.Online) return "Online";
    if (f.LastSeen === 0n) return "Offline";
    const ms = dateTimeBinaryToMs(f.LastSeen);
    return Number.isFinite(ms) && ms > 0 ? `Last seen ${fmtAgo(ms)}` : "Offline";
  }

  /** The friend's player object on screen (not a hero, not the user), or -1. */
  private inView(name: string): number {
    for (const ob of this.ui.ctx.world.objects.values()) {
      if (ob.race === ObjectType.Player && !ob.isUser && !ob.ownerName && !ob.dead && ob.name === name) return ob.id;
    }
    return -1;
  }

  /** Locks an entry until the server's S.FriendUpdate (or PENDING_MS). */
  private lock(index: number): void {
    this.pending.get(index)?.();
    const cancel = later(() => { if (this.pending.get(index) === cancel) { this.pending.delete(index); this.invalidate(); } }, PENDING_MS, this.signal);
    this.pending.set(index, cancel);
    this.invalidate();
  }

  private tick(): void {
    // Friends.requests drops expired ones (emits requestsChanged -> invalidate)
    const reqs = this.m.friends.requests;
    if (this.tab === "Requests") {
      const now = this.ui.ctx.now();
      for (const t of this.reqTimers) setText(t.el, `${Math.max(0, Math.ceil((t.r.expires - now) / 1000))} s left`);
    }
    const ri = this.tabList.indexOf("Requests");
    if (ri >= 0) this.tabBar?.badge(ri, reqs.length);
    if (this.tab !== "Requests" && this.picked !== null) {
      const f = this.m.friends.list.find((x) => x.Index === this.picked);
      const id = f && !f.Blocked && f.Online ? this.inView(f.Name) : -1;
      if (id !== this.inViewId) { this.inViewId = id; this.updateInspect(); }
    }
  }

  private updateInspect(): void {
    this.inspectBtn?.setDisabled(this.inViewId < 0, "Only while they are in view");
  }

  // — Actions —

  /** AddButton: a friend request (Friends tab) or a block (Blocked tab), by name. */
  private add(): void {
    const name = this.addIn.value.trim();
    if (!name) return;
    const blocked = this.tab === "Blocked";
    const known = this.m.friends.list.find((f) => f.Name.toLowerCase() === name.toLowerCase());
    if (known && known.Blocked === blocked) {
      this.setNote(blocked ? `${known.Name} is already blocked.` : `${known.Name} is already your friend.`);
      return;
    }
    this.m.friends.add(name, blocked);
    this.addIn.value = "";
    if (blocked) this.setNote(`Blocking ${name}…`);
    else if (this.requestsOn) this.setNote(`Friend request sent to ${name}. They have 60 s to accept.`, true);
    else this.setNote(`Adding ${name}…`);
  }

  private remove(f: ClientFriend): void {
    const text = f.Blocked
      ? `Unblock ${f.Name}?`
      : `Remove ${f.Name} from your friends? You are removed from their list too.`;
    void this.ui.rt.popups.ask(text, { title: f.Blocked ? "Unblock" : "Remove friend", kind: f.Blocked ? "question" : "warning" }).then((yes) => {
      if (!yes || this.signal.aborted || this.pending.has(f.Index)) return;
      if (!this.m.friends.list.some((x) => x.Index === f.Index)) return;
      this.m.friends.remove(f.Index);
      this.lock(f.Index);
    });
  }

  private block(f: ClientFriend): void {
    void this.ui.rt.popups.ask(`Block ${f.Name}? They are removed from your friends and you from theirs.`, { title: "Block", kind: "warning" }).then((yes) => {
      if (!yes || this.signal.aborted || this.pending.has(f.Index)) return;
      this.m.friends.add(f.Name, true);
      this.lock(f.Index);
    });
  }

  /** MemoDialog.OKButton: line breaks as the desktop's text box sends them. */
  private saveMemo(f: ClientFriend, text: string): void {
    const memo = text.trim();
    if (!memo || memo.length > MAX_MEMO || this.pending.has(f.Index)) return;
    this.m.friends.setMemo(f.Index, memo.replace(/\r?\n/g, "\r\n"));
    this.memoDraft = null;
    this.lock(f.Index);
  }

  /** EmailButton: MailComposeLetterDialog.ComposeMail(Name) through the mail window's composeTo when it has one. */
  private composeMail(name: string): void {
    const w = this.ui.rt.window<UiWindow & { composeTo?: (name: string) => void }>("mail");
    if (w && typeof w.composeTo === "function") w.composeTo(name);
    else this.ui.openWindow("mail", true);
  }

  // — Render —

  protected override render(): void {
    // Tabs: Requests only while the server uses friend requests
    const list: Tab[] = ["Friends", ...(this.requestsOn ? ["Requests" as const] : []), "Blocked"];
    if (!list.includes(this.tab)) this.tab = "Friends";
    const key = list.join(",");
    if (key !== this.tabKey || !this.tabBar) {
      this.tabKey = key;
      this.tabList = list;
      this.tabBar?.el.remove();
      this.tabBar = tabs(list.map((t) => t.toUpperCase()), list.indexOf(this.tab), (i) => {
        this.tab = list[i]!;
        this.picked = null;
        this.memoDraft = null;
        this.invalidate();
      });
      this.left.prepend(this.tabBar.el);
    } else this.tabBar.select(list.indexOf(this.tab));
    const reqs = this.m.friends.requests;
    const ri = list.indexOf("Requests");
    if (ri >= 0) this.tabBar.badge(ri, reqs.length);

    // Add box: request / block by name
    const blockedTab = this.tab === "Blocked";
    this.addRow.hidden = this.tab === "Requests";
    this.addIn.placeholder = blockedTab ? "Block a player by name" : this.requestsOn ? "Send a friend request by name" : "Add a friend by name";
    this.addBtn.setLabel(blockedTab ? "Block" : this.requestsOn ? "Request" : "Add");
    setText(this.noteEl, this.note);
    this.noteEl.classList.toggle("ok", this.noteOk);

    // Allow friend requests (S.AllowToggles; the switch lives in Options)
    const acc = this.m.account;
    this.allowEl.hidden = !this.requestsOn || !acc.togglesKnown;
    setText(this.allowState, acc.allowFriendRequests ? "On" : "Off");
    this.allowState.classList.toggle("off", !acc.allowFriendRequests);
    (this.allowEl.lastElementChild as HTMLElement).hidden = !this.ui.rt.hasWindow("options");

    this.renderBonds();
    this.renderList(reqs);
    this.renderDetail();
  }

  private renderBonds(): void {
    const rel = this.m.relationship, mentor = this.m.mentor;
    const parts: HTMLElement[] = [];
    const bond = (icon: "heartFull" | "mentor", title: string, line: string, win: string) => {
      const el = h("div", "bond", svg(icon, ""), h("div", "t", h("b", "", title), line));
      if (this.ui.rt.hasWindow(win)) el.append(button("Open", () => this.ui.openWindow(win, true)).el);
      parts.push(el);
    };
    if (rel.loverName) {
      const days = rel.marriedDays > 0 ? `Together ${fmt(rel.marriedDays)} ${rel.marriedDays === 1 ? "day" : "days"}` : "Married";
      bond("heartFull", `Married to ${rel.loverName}`, rel.mapName ? `${days} · in ${rel.mapName}` : days, "relationship");
    }
    if (mentor.mentorName) {
      const theyTeach = mentor.mentorLevel > this.ui.ctx.player.level;
      bond("mentor", `${theyTeach ? "Mentored by" : "Mentoring"} ${mentor.mentorName}`,
        `Level ${mentor.mentorLevel} · ${mentor.mentorOnline ? "online" : "offline"}`, "mentor");
    }
    this.bonds.replaceChildren(...parts);
    this.bonds.hidden = parts.length === 0;
  }

  private renderList(reqs: readonly FriendRequest[]): void {
    const keep = this.list.scrollTop;
    this.list.replaceChildren();
    this.reqTimers = [];
    if (this.tab === "Requests") {
      const rows = reqs.filter((r) => this.matches(r.name, className(r.cls)));
      if (!rows.length) this.list.append(h("div", "empty", reqs.length ? "No one matches." : "No pending requests."));
      const now = this.ui.ctx.now();
      for (const r of rows) {
        const left = h("span", "", `${Math.max(0, Math.ceil((r.expires - now) / 1000))} s left`);
        this.reqTimers.push({ el: left, r });
        const ok = button("Accept", () => this.m.friends.reply(r.name, true), { cls: "main" });
        const no = button("Decline", () => this.m.friends.reply(r.name, false));
        this.list.append(h("div", "row req", this.avatar(r.cls, null),
          h("div", "mid", h("div", "nm", r.name, h("small", "", `${className(r.cls)} ${r.level}`)), h("div", "sub", "wants to be friends · ", left)),
          no.el, ok.el));
      }
      this.list.scrollTop = keep;
      return;
    }
    const blocked = this.tab === "Blocked";
    const all = this.entries(blocked);
    const pool = all.filter((f) => this.matches(f.Name, f.GuildName, f.Memo, className(f.Class)))
      .sort((a, b) => a.Name.localeCompare(b.Name));
    const groups: [string, ClientFriend[]][] = blocked
      ? [["Blocked", pool]]
      : [["Online", pool.filter((f) => f.Online)], ["Offline", pool.filter((f) => !f.Online)]];
    for (const [g, items] of groups) {
      if (!items.length) continue;
      const head = asButton(h("div", "grp" + (this.shut.has(g) ? " shut" : ""), h("span", "chev", "▾"), g, h("span", "", String(items.length))));
      head.setAttribute("aria-expanded", String(!this.shut.has(g)));
      head.addEventListener("click", () => { if (this.shut.has(g)) this.shut.delete(g); else this.shut.add(g); this.invalidate(); });
      this.list.append(head);
      if (this.shut.has(g)) continue;
      for (const f of items) this.list.append(this.friendRow(f));
    }
    if (!pool.length) {
      const max = serverRules(this.ui.ctx.session).rules?.MaxFriends ?? 0;
      this.list.append(h("div", "empty", all.length ? "No one matches."
        : blocked ? "You have not blocked anyone." : `No friends yet.${max ? ` You can have up to ${max}.` : ""}`));
    }
    this.list.scrollTop = keep;
  }

  private friendRow(f: ClientFriend): HTMLElement {
    const sub = f.Blocked
      ? h("div", "sub", f.Memo ? h("em", "", f.Memo) : "Blocked")
      : h("div", "sub", f.Online ? (f.GuildName || "Online") : this.lastSeen(f), f.Memo ? " · " : "", f.Memo ? h("em", "", f.Memo) : "");
    const row = asButton(h("div", "row" + (f.Index === this.picked ? " sel" : "") + (f.Online || f.Blocked ? "" : " off"),
      this.avatar(f.Class, f.Blocked ? null : f.Online), h("div", "mid", h("div", "nm", f.Name, h("small", "", `${className(f.Class)} ${f.Level}`)), sub)));
    row.addEventListener("click", () => {
      if (this.picked === f.Index) return;
      this.picked = f.Index;
      this.memoDraft = null;
      this.invalidate();
    });
    return row;
  }

  private renderDetail(): void {
    const keep = this.detail.querySelector(".dscroll")?.scrollTop ?? 0;
    this.detail.replaceChildren();
    this.inspectBtn = null;
    if (this.tab === "Requests") {
      this.detail.append(h("div", "empty", this.m.friends.requests.length
        ? "Accept a request to add each other as friends. A request runs out after 60 seconds."
        : "Friend requests other players send you show here (and as a prompt while this window is closed)."));
      return;
    }
    const pool = this.entries(this.tab === "Blocked").sort((a, b) => a.Name.localeCompare(b.Name));
    const f = pool.find((x) => x.Index === this.picked) ?? pool.find((x) => x.Online) ?? pool[0];
    if (!f) {
      this.picked = null;
      this.detail.append(h("div", "empty", this.tab === "Blocked" ? "Blocked players cannot send you friend requests." : "Send a friend request by name below the list."));
      return;
    }
    if (this.picked !== f.Index) { this.picked = f.Index; this.memoDraft = null; }
    const busy = this.pending.has(f.Index);

    // Profile
    const pill = h("span", "pill" + (f.Blocked ? " blk" : f.Online ? " on" : ""), f.Blocked ? "Blocked" : f.Online ? "Online" : "Offline");
    const meta: (string | HTMLElement)[] = [h("b", "", `Level ${f.Level} ${className(f.Class)}`)];
    if (!f.Online && !f.Blocked) meta.push(" · ", this.lastSeen(f));
    const prof = h("div", "prof", this.avatar(f.Class, f.Blocked ? null : f.Online),
      h("div", "who", h("h3", "", f.Name, pill), h("div", "meta", ...meta),
        h("div", "meta", f.GuildName ? h("span", "", "Guild ", h("b", "", f.GuildName)) : "No guild")));

    // Memo (C.AddMemo: 1-200 characters; it cannot be cleared, only replaced)
    const memo = typingField(h("textarea", "fld"));
    memo.maxLength = MAX_MEMO;
    memo.placeholder = "Add a private note…";
    const saved = f.Memo.replace(/\r\n/g, "\n");
    memo.value = this.memoDraft?.index === f.Index ? this.memoDraft.text : saved;
    const count = h("span");
    const save = button("Save memo", () => this.saveMemo(f, memo.value), { icon: "check" });
    const sync = () => {
      const t = memo.value.trim();
      setText(count, `${memo.value.length} / ${MAX_MEMO}`);
      save.setDisabled(busy || !t || memo.value === saved, busy ? "Saving…" : !t ? "A memo cannot be cleared, only replaced" : "No changes");
    };
    memo.addEventListener("input", () => { this.memoDraft = { index: f.Index, text: memo.value }; sync(); });
    sync();
    const memoBox = h("div", "", h("div", "lbl mlbl", svg("memo", ""), "Memo", h("small", "", "only you see this")), memo, h("div", "msave", count, save.el));

    const scroll = h("div", "dscroll", prof, memoBox);
    this.detail.append(scroll, this.actions(f, busy));
    scroll.scrollTop = keep;
  }

  private actions(f: ClientFriend, busy: boolean): HTMLElement {
    const bar = h("div", "actions");
    if (f.Blocked) {
      bar.append(h("div", "sp"), button("Unblock", () => this.remove(f), { icon: "unlock", disabled: busy }).el);
      return bar;
    }
    const whisper = button("Whisper", () => this.ui.chat.prefill(`/${f.Name} `), { cls: "main", icon: "chat", title: "Whisper (/name in the chat box)" });
    if (!f.Online) whisper.setDisabled(true, "Player is not online");
    const group = button("Invite", () => this.m.group.addMember(f.Name), { icon: "group", title: "Invite to group" });
    if (!f.Online) group.setDisabled(true, "Player is not online");
    else if (this.m.group.has(f.Name)) group.setDisabled(true, "Already in your group");
    bar.append(whisper.el, group.el);
    if (this.ui.rt.hasWindow("mail")) bar.append(button("Mail", () => this.composeMail(f.Name), { icon: "mail", title: "Send mail" }).el);
    this.inViewId = f.Online ? this.inView(f.Name) : -1;
    this.inspectBtn = button("Inspect", () => { if (this.inViewId >= 0) this.m.inspect.inspect(this.inViewId); }, { icon: "eye" });
    this.updateInspect();
    bar.append(this.inspectBtn.el, h("div", "sp"),
      button("Block", () => this.block(f), { icon: "lock", disabled: busy }).el,
      button("Remove", () => this.remove(f), { cls: "warn", icon: "trash", disabled: busy }).el);
    return bar;
  }
}

registerWindow(FRIENDS_KEY, (host) => new FriendsWindow(host));
