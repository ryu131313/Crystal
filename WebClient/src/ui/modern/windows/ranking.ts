// Ported from play/hudRanking.ts: the new UI's ranking window over the real Rankings model. Desktop counterpart:
// RankingDialog (Client/MirScenes/Dialogs/RankingDialog.cs) + GameScene.Rankings; the classic port is
// src/ui/rankingWindow.ts (its request rules are kept: a tab or the Online Only switch asks at once, scrolling asks
// 0.5 s after the last move; RankIndex = the first row shown; rank labels = RankIndex + row + 1, as the desktop).
//
// Changes from the mock:
//  - the boards are the server's: RankType 0 = overall, 1..5 = one per class (C.GetRanking -> S.Rankings, 20 rows from
//    RankIndex; Count = the board's size), with the server's Online Only flag; Prev / Next pages, the mouse wheel moves
//    the rows like the desktop's scroll bar (re-asked after 0.5 s); the shown rows keep the RankIndex of the request
//    that brought them, so labels never run ahead of the data;
//  - rows enriched with S.RankingsDetail (guild, online dot, EXP to the next level; per connection in the model), blank
//    until it arrives; the podium shows the board's first three while its first page is shown;
//  - "Your rank" is S.Rankings.MyRank (0 = not listed; class boards list only your own class); "Find me" asks for the
//    page around it and selects your row;
//  - the player card: name, class, level, guild, online, EXP, the rank on this board; Inspect sends C.Inspect with
//    Ranking = true (RankingRow.Inspect; a double-click on a row too), Whisper prefills the chat, Add friend sends a
//    friend request (C.AddFriend), Invite sends C.AddMember;
//  - the Guilds tab is the server's guild board (C.GetGuildRanking -> S.GuildRankings, 20 a page, by level then EXP;
//    the server rebuilds it at most every 5 minutes);
//  - HIDDEN (Crystal keeps no history or seasons): movement arrows, the sparkline, "best this season", the season
//    countdown, and the whole-board search (the boards are paged by the server).
// Narrow layout (a layer below 1040 px): no podium, a narrower card column.

import type { ClientGuildRanking, RankCharacterInfo } from "../../../protocol/server.ts";
import { GUILD_BOARD_PAGE_SIZE, RANK_TYPES } from "../../../game/rankings.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, className, classTag, classVar, debounce, fmt, h, injectCss, plural, setStyle, setText, svgNode, tabs,
  toggleSwitch, type Btn,
} from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.ranking; K reaches it as a UiCommand from game.ts). */
export const RANKING_WINDOW = "ranking";

const ROWS = 20;                    // RankingDialog's rows (the server sends at most 20 from RankIndex)
const REQUEST_DELAY = 500;          // RankingDialog.Move -> NextRequestTime
const WHEEL_ROWS = 3;
const GUILD_TAB = RANK_TYPES;       // the tab after the six RankTypes
const TAB_NAMES = ["Overall", ...[0, 1, 2, 3, 4].map(className), "Guilds"];

const CSS = `
.hudm .rkwin { width: 900px; }
.hudm .rkwin .panel { height: 610px; display: flex; flex-direction: column; }
.hudm .rkwin .tabrow { padding: 8px 14px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rkwin .rbody { flex: 1; min-height: 0; display: flex; }
.hudm .rkwin .board { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 10px 12px; gap: 8px; min-height: 0; }
/* Podium */
.hudm .rkwin .podium { display: grid; grid-template-columns: 1fr 1.15fr 1fr; gap: 10px; align-items: end; flex: none; padding-top: 12px; }
.hudm .rkwin .pod { position: relative; text-align: center; cursor: pointer; padding: 10px 8px 8px; border-radius: var(--rad) var(--rad) 0 0;
  background: linear-gradient(180deg, color-mix(in srgb, var(--m) 22%, transparent), color-mix(in srgb, var(--pb) 70%, transparent)); border: 1px solid color-mix(in srgb, var(--m) 60%, transparent); border-bottom-width: 3px; }
.hudm .rkwin .pod.p1 { --m: #f2c94c; padding-top: 18px; } .hudm .rkwin .pod.p2 { --m: #c9d3e0; } .hudm .rkwin .pod.p3 { --m: #d08a4a; }
.hudm .rkwin .pod.empty { visibility: hidden; }
.hudm .rkwin .pod .medal { position: absolute; top: -13px; left: 50%; transform: translateX(-50%); width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  font-size: 12px; font-weight: 900; color: #1a1a1a; background: radial-gradient(circle at 35% 35%, #fff, var(--m)); box-shadow: 0 0 0 2px #000; }
.hudm .rkwin .pod .badge { width: 44px; height: 44px; margin: 6px auto; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--m); }
.hudm .rkwin .pod b { display: block; font-size: 13px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .rkwin .pod span { font-size: 11px; color: var(--dim); }
.hudm .rkwin .pod .lv { margin-top: 3px; font: 700 17px Georgia, serif; color: var(--m); }
/* Tools */
.hudm .rkwin .tools { display: flex; gap: 8px; align-items: center; }
.hudm .rkwin .opt { display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--dim); }
.hudm .rkwin .tools .sp { flex: 1; }
.hudm .rkwin .tools .btn { height: 26px; }
.hudm .rkwin .pg { font-size: 11px; color: var(--dim); font-variant-numeric: tabular-nums; }
/* Table */
.hudm .rkwin .rows { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; }
.hudm .rkwin .rrow, .hudm .rkwin .head { display: grid; grid-template-columns: 50px 30px 1fr 120px 50px 110px; align-items: center; gap: 8px; padding: 0 8px; }
.hudm .rkwin .rrow { height: 30px; cursor: pointer; border-radius: var(--rad); font-size: 12px; }
.hudm .rkwin .rrow:nth-child(odd) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rkwin .rrow:hover { background: color-mix(in srgb, var(--ac) 9%, transparent); }
.hudm .rkwin .rrow.me { background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .rkwin .rrow.sel { background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .rkwin .head { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .rkwin .head span:first-child, .hudm .rkwin .rk { text-align: right; }
.hudm .rkwin .rk { font: 700 13px Georgia, serif; color: var(--tx); }
.hudm .rkwin .rrow.top .rk { color: var(--ac); }
.hudm .rkwin .cb { width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 8px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--cc, var(--fr)); position: relative; }
.hudm .rkwin .cb.on::after { content: ""; position: absolute; right: -2px; bottom: -2px; width: 8px; height: 8px; border-radius: 50%; background: var(--ok); border: 2px solid #000; }
.hudm .rkwin .nm { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .rkwin .nm b { color: #fff; font-weight: 600; }
.hudm .rkwin .nm small { margin-left: 6px; color: var(--dim); font-size: 10px; }
.hudm .rkwin .gtag { font-size: 10px; color: var(--acd); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .rkwin .lvl { font-weight: 700; text-align: center; font-variant-numeric: tabular-nums; }
.hudm .rkwin .xp { height: 5px; border-radius: 3px; background: #04060a; overflow: hidden; } .hudm .rkwin .xp i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .rkwin .xp.none { visibility: hidden; }
.hudm .rkwin .pin { flex: none; display: flex; align-items: center; gap: 10px; border-top: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); padding-top: 6px; font-size: 12px; }
.hudm .rkwin .pin b { color: #fff; }
/* Guild board */
.hudm .rkwin .grow, .hudm .rkwin .ghead { display: grid; grid-template-columns: 50px 30px 1fr 60px 90px 110px; align-items: center; gap: 8px; padding: 0 8px; }
.hudm .rkwin .grow { height: 34px; border-radius: var(--rad); font-size: 12px; }
.hudm .rkwin .grow:nth-child(odd) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rkwin .grow.me { box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .rkwin .ghead { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .rkwin .ghead span:first-child { text-align: right; }
.hudm .rkwin .grow .sh { width: 24px; height: 28px; }
.hudm .rkwin .grow .sh path { fill: color-mix(in srgb, var(--ac) 22%, var(--pb)); stroke: var(--ac); stroke-width: 1.5; }
.hudm .rkwin .grow .sh text { fill: #fff; font-size: 11px; font-weight: 700; }
/* Card */
.hudm .rkwin .side { width: 250px; flex: none; padding: 12px; display: flex; flex-direction: column; gap: 10px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rkwin .prof { text-align: center; }
.hudm .rkwin .prof .cb { width: 60px; height: 60px; margin: 4px auto 8px; font-size: 14px; border-width: 2px; }
.hudm .rkwin .prof .cb.on::after { width: 12px; height: 12px; }
.hudm .rkwin .prof h3 { margin: 0; font-size: 17px; color: #fff; overflow: hidden; text-overflow: ellipsis; } .hudm .rkwin .prof .sub { font-size: 11px; color: var(--dim); }
.hudm .rkwin .big { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .rkwin .big div { padding: 8px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--frl) 6%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .rkwin .big b { display: block; font: 700 19px Georgia, serif; color: #fff; } .hudm .rkwin .big span { font-size: 10px; color: var(--dim); }
.hudm .rkwin .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.hudm .rkwin .acts .btn { height: 26px; justify-content: center; }
.hudm .rkwin .note { font-size: 11px; color: var(--dim); text-align: center; }
/* Narrow layer */
.hudm.narrow .rkwin { width: 720px; }
.hudm.narrow .rkwin .podium { display: none; }
.hudm.narrow .rkwin .side { width: 200px; }
.hudm.narrow .rkwin .rrow, .hudm.narrow .rkwin .head { grid-template-columns: 42px 28px 1fr 90px 40px 70px; gap: 6px; }
.hudm.narrow .rkwin .grow, .hudm.narrow .rkwin .ghead { grid-template-columns: 42px 28px 1fr 48px 70px 70px; gap: 6px; }
.hudm.narrow .rkwin .nm small { display: none; }
`;

/** One board row, built once and refilled. */
interface Row {
  el: HTMLElement; rk: HTMLElement; cb: HTMLElement; name: HTMLElement; cls: HTMLElement; guild: HTMLElement; lvl: HTMLElement;
  xp: HTMLElement; fill: HTMLElement; id: bigint | null;
}
interface Pod { el: HTMLElement; badge: HTMLElement; name: HTMLElement; sub: HTMLElement; lv: HTMLElement; id: bigint | null }
interface GRow { el: HTMLElement; rk: HTMLElement; initial: SVGTextElement; name: HTMLElement; leader: HTMLElement; lvl: HTMLElement; members: HTMLElement; xp: HTMLElement; fill: HTMLElement }

class RankingWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private board = 0;                 // 0..5 = RankType, GUILD_TAB = guilds
  private onlineOnly = false;
  /** Per RankType: the wanted RankIndex, the one last sent, and the one the shown rows came with. */
  private readonly offset = new Array<number>(RANK_TYPES).fill(0);
  private readonly sentOffset = new Array<number>(RANK_TYPES).fill(0);
  private readonly shownOffset = new Array<number>(RANK_TYPES).fill(0);
  private readonly lastList: (readonly RankCharacterInfo[] | null)[] = new Array(RANK_TYPES).fill(null);
  private picked: bigint | null = null;
  private findMe = false;
  private guildPage = 0;
  private readonly requestSoon: (() => void) & { cancel(): void };

  // — DOM, built once —
  private readonly tabBar: ReturnType<typeof tabs>;
  private readonly playerBoard = h("div", "board");
  private readonly guildBoard = h("div", "board");
  private readonly podium = h("div", "podium");
  private readonly pods: Pod[] = [];
  private readonly rows: Row[] = [];
  private readonly rowsEl = h("div", "rows");
  private readonly online: ReturnType<typeof toggleSwitch>;
  private readonly findBtn: Btn;
  private readonly prevBtn: Btn;
  private readonly nextBtn: Btn;
  private readonly pageText = h("span", "pg");
  private readonly pinText = h("span");
  private readonly gRows: GRow[] = [];
  private readonly gRowsEl = h("div", "rows");
  private readonly gPrev: Btn;
  private readonly gNext: Btn;
  private readonly gPageText = h("span", "pg");
  private readonly side = h("div", "side");

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "rkwin", 62, 30);
    injectCss("win-ranking", CSS);
    this.m = ui.ctx.models;
    const sig = ui.rt.signal;
    this.requestSoon = debounce(() => this.requestNow(), REQUEST_DELAY, sig);

    this.tabBar = tabs(TAB_NAMES, 0, (i) => this.selectBoard(i));

    // Podium (2nd, 1st, 3rd)
    for (const [pos, cls] of [[2, "p2"], [1, "p1"], [3, "p3"]] as const) {
      const badge = h("div", "badge"), name = h("b"), sub = h("span"), lv = h("div", "lv");
      const el = h("div", "pod " + cls, h("div", "medal", String(pos)), badge, name, sub, lv);
      const pod: Pod = { el, badge, name, sub, lv, id: null };
      el.addEventListener("click", () => { if (pod.id !== null) this.pick(pod.id); }, { signal: sig });
      this.pods[pos - 1] = pod;
      this.podium.append(el);
    }

    // Tools: Online Only, Find me, pager
    this.online = toggleSwitch(false, (on) => {
      this.onlineOnly = on;
      this.offset[this.board] = 0;
      this.requestNow(); // the desktop's check box: RowOffset 0, request at once
    }, { small: true, label: "Online only" });
    this.findBtn = button("Find me", () => this.findMine(), { icon: "search", title: "Show the page with your rank" });
    this.prevBtn = button("", () => this.page(-1), { icon: "chevronLeft", title: "Previous 20" });
    this.nextBtn = button("", () => this.page(1), { icon: "chevronRight", title: "Next 20" });
    const tools = h("div", "tools", h("div", "opt", this.online.el, "Online only"), this.findBtn.el, h("div", "sp"), this.pageText, this.prevBtn.el, this.nextBtn.el);

    // Rows
    for (let i = 0; i < ROWS; i++) {
      const rk = h("span", "rk"), cb = h("div", "cb"), name = h("b"), cls = h("small"), guild = h("span", "gtag"), lvl = h("span", "lvl");
      const fill = h("i"), xp = h("div", "xp", fill);
      const el = h("div", "rrow", rk, cb, h("div", "nm", name, cls), guild, lvl, xp);
      el.hidden = true;
      const row: Row = { el, rk, cb, name, cls, guild, lvl, xp, fill, id: null };
      el.addEventListener("click", () => { if (row.id !== null) this.pick(row.id); }, { signal: sig });
      el.addEventListener("dblclick", () => { if (row.id !== null) this.inspect(row.id); }, { signal: sig });
      this.rows.push(row);
      this.rowsEl.append(el);
    }
    // Ranking_MouseWheel: one move per notch, the request 0.5 s after the last
    this.rowsEl.addEventListener("wheel", (e) => {
      if (this.board >= RANK_TYPES) return;
      e.preventDefault();
      this.move(e.deltaY > 0 ? WHEEL_ROWS : -WHEEL_ROWS);
    }, { passive: false, signal: sig });
    const head = h("div", "head", h("span", "", "Rank"), h("span"), h("span", "", "Player"), h("span", "", "Guild"), h("span", "", "Level"), h("span", "", "EXP"));
    this.playerBoard.append(this.podium, tools, head, this.rowsEl, h("div", "pin", this.pinText));

    // Guild board
    this.gPrev = button("", () => this.guildMove(-1), { icon: "chevronLeft", title: "Previous 20" });
    this.gNext = button("", () => this.guildMove(1), { icon: "chevronRight", title: "Next 20" });
    for (let i = 0; i < GUILD_BOARD_PAGE_SIZE; i++) {
      const shield = svgNode("svg", { viewBox: "0 0 28 32", class: "sh", "aria-hidden": "true" });
      const initial = svgNode("text", { x: 14, y: 20, "text-anchor": "middle" });
      shield.append(svgNode("path", { d: "M14 1l12 4v10c0 8-5 13-12 16C7 28 2 23 2 15V5z" }), initial);
      const rk = h("span", "rk"), name = h("b"), leader = h("small"), lvl = h("span", "lvl"), members = h("span", "gtag");
      const fill = h("i"), xp = h("div", "xp", fill);
      const el = h("div", "grow", rk, shield, h("div", "nm", name, leader), lvl, members, xp);
      el.hidden = true;
      this.gRows.push({ el, rk, initial, name, leader, lvl, members, xp, fill });
      this.gRowsEl.append(el);
    }
    this.guildBoard.append(h("div", "tools", h("span", "pg", "Guilds by level, then guild EXP"), h("div", "sp"), this.gPageText, this.gPrev.el, this.gNext.el),
      h("div", "ghead", h("span", "", "Rank"), h("span"), h("span", "", "Guild"), h("span", "", "Level"), h("span", "", "Members"), h("span", "", "EXP")), this.gRowsEl);
    this.guildBoard.hidden = true;

    this.el.append(h("div", "panel", this.titleBar("RANKING", "Close (K)"), h("div", "tabrow", this.tabBar.el),
      h("div", "rbody", this.playerBoard, this.guildBoard, this.side)));

    // Model events (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    const r = this.m.rankings;
    on(r.changed.on(() => this.received()));
    on(r.detailsChanged.on(() => this.invalidate()));
    on(r.guildBoardChanged.on((e) => { if (this.board === GUILD_TAB && e.page !== this.guildPage) this.guildPage = e.page; this.invalidate(); }));
    on(this.m.guild.changed.on(() => { if (this.board === GUILD_TAB) this.invalidate(); }));
    on(this.m.friends.changed.on(() => this.invalidate()));
    on(this.m.group.changed.on(() => this.invalidate()));
  }

  /** RankingDialog.Show: request the current tab. */
  protected override shown(): void {
    if (this.board === GUILD_TAB) this.m.rankings.requestGuildPage(this.guildPage);
    else this.requestNow();
  }

  protected override closed(): void { this.requestSoon.cancel(); }

  // — Requests —

  private get me(): string { return this.ui.ctx.player.user?.name ?? ""; }

  /** RankingDialog.RequestRanks for the current tab at its offset. */
  private requestNow(): void {
    this.requestSoon.cancel();
    const t = this.board;
    if (t >= RANK_TYPES) return;
    this.sentOffset[t] = this.offset[t]!;
    this.m.rankings.request(t, this.offset[t]!, this.onlineOnly);
    this.invalidate(); // the pager shows the wanted range at once
  }

  /** S.Rankings applied: the changed lists take the offset of their request. */
  private received(): void {
    const r = this.m.rankings;
    for (let t = 0; t < RANK_TYPES; t++) {
      const l = r.list(t);
      if (l !== this.lastList[t]) { this.lastList[t] = l; this.shownOffset[t] = this.sentOffset[t]!; }
    }
    this.invalidate();
  }

  /** RankingDialog.SelectRank (+ the guild tab). */
  private selectBoard(i: number): void {
    this.board = i;
    this.picked = null;
    this.findMe = false;
    if (i === GUILD_TAB) { this.requestSoon.cancel(); this.m.rankings.requestGuildPage(this.guildPage); }
    else { this.offset[i] = 0; this.requestNow(); }
    this.invalidate();
  }

  private maxOffset(): number { return Math.max(0, this.m.rankings.count - ROWS); }

  /** RankingDialog.Move: the rows move now, the request follows 0.5 s after the last move. */
  private move(rows: number): void {
    const t = this.board, next = Math.min(this.maxOffset(), Math.max(0, this.offset[t]! + rows));
    if (next === this.offset[t]) return;
    this.offset[t] = next;
    this.requestSoon();
    this.invalidate();
  }

  private page(dir: number): void { this.move(dir * ROWS); }

  /** "Find me": the page around the user's rank; the row is selected when it arrives. */
  private findMine(): void {
    const t = this.board, mine = this.m.rankings.myRank(t);
    if (mine <= 0) return;
    this.offset[t] = Math.min(this.maxOffset(), Math.max(0, mine - 1 - Math.floor(ROWS / 2)));
    this.findMe = true;
    this.requestNow();
  }

  private guildMove(dir: number): void {
    const total = this.m.rankings.guildTotal, last = total > 0 ? Math.floor((total - 1) / GUILD_BOARD_PAGE_SIZE) : 0;
    const next = Math.min(last, Math.max(0, this.guildPage + dir));
    if (next === this.guildPage) return;
    this.guildPage = next;
    this.m.rankings.requestGuildPage(next);
    this.invalidate();
  }

  private pick(id: bigint): void {
    if (this.picked === id) return;
    this.picked = id;
    this.invalidate();
  }

  /** RankingRow.Inspect: C.Inspect with Ranking = true, ObjectID = (uint)PlayerId (the inspect window opens on the reply). */
  private inspect(id: bigint): void {
    this.m.inspect.inspect(Number(BigInt.asUintN(32, id)), true);
  }

  // — Render —

  protected override render(): void {
    const guilds = this.board === GUILD_TAB;
    this.tabBar.select(this.board);
    this.playerBoard.hidden = guilds;
    this.guildBoard.hidden = !guilds;
    if (guilds) { this.renderGuilds(); this.renderGuildCard(); return; }
    this.renderBoard();
    this.renderCard();
  }

  private renderBoard(): void {
    const r = this.m.rankings, t = this.board, list = r.list(t), off = this.shownOffset[t]!, me = this.me;
    const count = r.count;
    for (let i = 0; i < ROWS; i++) {
      const row = this.rows[i]!, info = i < count ? list[i] : undefined;
      row.el.hidden = !info;
      if (!info) { row.id = null; continue; }
      const rank = off + i + 1, ex = r.extra(info.PlayerId);
      row.id = info.PlayerId;
      if (this.findMe && info.Name === me) { this.findMe = false; this.picked = info.PlayerId; }
      setText(row.rk, `#${rank}`);
      setText(row.cb, classTag(info.Class));
      setStyle(row.cb, "--cc", classVar(info.Class));
      row.cb.classList.toggle("on", !!ex?.Online);
      setText(row.name, info.Name);
      setText(row.cls, className(info.Class));
      setText(row.guild, ex ? ex.GuildName || "—" : "");
      setText(row.lvl, String(info.level));
      row.xp.classList.toggle("none", !ex);
      setStyle(row.fill, "width", ex ? `${(ex.ExpPerTenThousand / 100).toFixed(1)}%` : "0%");
      row.el.title = ex ? `EXP ${(ex.ExpPerTenThousand / 100).toFixed(2)}% · double-click to inspect` : "Double-click to inspect";
      row.el.classList.toggle("top", rank <= 3);
      row.el.classList.toggle("me", info.Name === me);
      row.el.classList.toggle("sel", info.PlayerId === this.picked);
    }

    // Podium: the board's first three while its first page is shown
    const podOn = off === 0 && list.length > 0;
    this.podium.hidden = !podOn;
    if (podOn) {
      for (let p = 0; p < 3; p++) {
        const pod = this.pods[p]!, info = list[p];
        pod.el.classList.toggle("empty", !info);
        pod.id = info?.PlayerId ?? null;
        if (!info) continue;
        const ex = r.extra(info.PlayerId);
        setText(pod.badge, classTag(info.Class));
        setText(pod.name, info.Name);
        setText(pod.sub, ex?.GuildName ? `${className(info.Class)} · ${ex.GuildName}` : className(info.Class));
        setText(pod.lv, `Lv ${info.level}`);
      }
    }

    // Pager (the wanted range, so it moves at once) and the user's rank
    const want = this.offset[t]!;
    setText(this.pageText, count > 0 ? `${fmt(want + 1)}-${fmt(Math.min(count, want + ROWS))} of ${fmt(count)}` : "Nobody listed");
    this.prevBtn.setDisabled(want <= 0, "First page");
    this.nextBtn.setDisabled(want >= this.maxOffset(), "Last page");
    const mine = r.myRank(t);
    setText(this.pinText, mine > 0 ? `Your rank on this board: #${fmt(mine)}` : t === 0 ? "You are not listed yet." : "You are not listed on this board.");
    this.findBtn.setDisabled(mine <= 0, "You are not listed on this board");
  }

  /** The picked player's card, else the user's own summary. */
  private renderCard(): void {
    const r = this.m.rankings, t = this.board, me = this.me;
    const info = this.picked !== null ? r.details.get(this.picked) : undefined;
    if (!info) {
      const mine = r.myRank(t);
      this.side.replaceChildren(
        h("div", "prof", h("div", "cb", "YOU"), h("h3", "", me || "You"), h("div", "sub", TAB_NAMES[t]!)),
        h("div", "big", h("div", "", h("b", "", mine > 0 ? `#${fmt(mine)}` : "–"), h("span", "", "your rank")),
          h("div", "", h("b", "", fmt(this.ui.ctx.player.level)), h("span", "", "your level"))),
        h("div", "note", "Click a player to see their card; double-click to inspect."));
      return;
    }
    const ex = r.extra(info.PlayerId), list = r.list(t), at = list.findIndex((x) => x.PlayerId === info.PlayerId);
    const rank = at >= 0 ? this.shownOffset[t]! + at + 1 : 0;
    const badge = h("div", "cb" + (ex?.Online ? " on" : ""), classTag(info.Class));
    setStyle(badge, "--cc", classVar(info.Class));
    const sub = [className(info.Class), ex ? ex.GuildName || "No guild" : "", ex ? (ex.Online ? "online" : "offline") : ""].filter(Boolean).join(" · ");
    const parts: HTMLElement[] = [
      h("div", "prof", badge, h("h3", "", info.Name), h("div", "sub", sub)),
      h("div", "big", h("div", "", h("b", "", rank > 0 ? `#${fmt(rank)}` : "–"), h("span", "", `${TAB_NAMES[t]!.toLowerCase()} rank`)),
        h("div", "", h("b", "", String(info.level)), h("span", "", ex ? `level · ${(ex.ExpPerTenThousand / 100).toFixed(1)}% EXP` : "level"))),
    ];
    if (info.Name === me) parts.push(h("div", "note", "That's you."));
    else {
      const name = info.Name;
      const isFriend = this.m.friends.list.some((f) => f.Name.toLowerCase() === name.toLowerCase() && !f.Blocked);
      const inGroup = this.m.group.has(name);
      const friend = button("Add friend", () => this.m.friends.add(name, false), { icon: "friends", title: "Send a friend request" });
      if (isFriend) friend.setDisabled(true, "Already your friend");
      const invite = button("Invite", () => this.m.group.addMember(name), { icon: "group", title: "Invite to your group" });
      if (inGroup) invite.setDisabled(true, "Already in your group");
      else if (!this.m.group.isLeader(me)) invite.setDisabled(true, "Only the group leader can invite");
      else if (ex && !ex.Online) invite.setDisabled(true, "Offline");
      parts.push(h("div", "acts",
        button("Inspect", () => this.inspect(info.PlayerId), { icon: "eye", title: "Inspect this character" }).el,
        button("Whisper", () => this.ui.chat.prefill(`/${name} `), { icon: "chat" }).el,
        friend.el, invite.el));
    }
    this.side.replaceChildren(...parts);
  }

  private renderGuilds(): void {
    const r = this.m.rankings, rows = r.guildPage(this.guildPage), mine = this.m.guild.name, base = this.guildPage * GUILD_BOARD_PAGE_SIZE;
    for (let i = 0; i < this.gRows.length; i++) {
      const g = this.gRows[i]!, info: ClientGuildRanking | undefined = rows?.[i];
      g.el.hidden = !info;
      if (!info) continue;
      setText(g.rk, `#${base + i + 1}`);
      g.initial.textContent = info.Name.charAt(0).toUpperCase(); // a guild name is player data: text only
      setText(g.name, info.Name);
      setText(g.leader, info.Leader ? `led by ${info.Leader}` : "");
      setText(g.lvl, `Lv ${info.Level}`);
      setText(g.members, plural(info.Members, "member"));
      setStyle(g.fill, "width", `${(info.ExpPerTenThousand / 100).toFixed(1)}%`);
      g.el.title = `Guild EXP ${(info.ExpPerTenThousand / 100).toFixed(2)}% to the next level`;
      g.el.classList.toggle("me", mine !== "" && info.Name === mine);
    }
    const total = r.guildTotal, last = total > 0 ? Math.floor((total - 1) / GUILD_BOARD_PAGE_SIZE) : 0;
    setText(this.gPageText, !rows ? "Loading…" : total > 0 ? `${fmt(base + 1)}-${fmt(Math.min(total, base + GUILD_BOARD_PAGE_SIZE))} of ${fmt(total)}` : "No guilds ranked");
    this.gPrev.setDisabled(this.guildPage <= 0, "First page");
    this.gNext.setDisabled(this.guildPage >= last, "Last page");
  }

  /** The user's guild beside the guild board (its rank when it is on a received page). */
  private renderGuildCard(): void {
    const g = this.m.guild, r = this.m.rankings;
    if (!g.name) {
      this.side.replaceChildren(h("div", "prof", h("div", "cb", "–"), h("h3", "", "No guild")), h("div", "note", "Join or found a guild to appear on this board."));
      return;
    }
    let rank = 0;
    const pages = r.guildTotal > 0 ? Math.ceil(r.guildTotal / GUILD_BOARD_PAGE_SIZE) : 0;
    for (let p = 0; p < pages && !rank; p++) {
      const rows = r.guildPage(p);
      const at = rows ? rows.findIndex((x) => x.Name === g.name) : -1;
      if (at >= 0) rank = p * GUILD_BOARD_PAGE_SIZE + at + 1;
    }
    this.side.replaceChildren(
      h("div", "prof", h("div", "cb", g.name.charAt(0).toUpperCase()), h("h3", "", g.name), h("div", "sub", "Your guild")),
      h("div", "big", h("div", "", h("b", "", rank > 0 ? `#${fmt(rank)}` : "–"), h("span", "", "guild rank")),
        h("div", "", h("b", "", String(g.level)), h("span", "", "guild level"))),
      h("div", "note", rank > 0 ? "The board is rebuilt every few minutes." : "Not on the pages seen so far."));
  }
}

registerWindow(RANKING_WINDOW, (host) => new RankingWindow(host));
