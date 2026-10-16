// Ported from play/hudTerritory.ts: the new UI's guild territories window over the real Territories model. Desktop
// counterpart: GuildTerritoryDialog (Client/MirScenes/Dialogs/GuildTerritoryDialog .cs) + GameScene.GuildTerritoryPage;
// the classic port is src/ui/guildTerritoryWindow.ts (status rules: "None" = available, begin > 0 = sale pending,
// price > 0 = for sale, else unavailable).
//
// Changes from the mock:
//  - the list is the server's (S.GuildTerritoryPage, 7 rows a page, no page number on the wire): TerritoryBook below
//    asks for every page (C.GuildTerritoryPage) and files each reply by the request order, or by its first row's GT index
//    once that is known, so the window lists every territory at once;
//  - opens from the game menu (C.GuildTerritoryPage needs no NPC) and when the server sends a page while the window is
//    hidden (an NPC's [@GUILDTERRITORY] page calls GetGuildTerritories(0)): the registry entry's watch hook;
//  - Rent / Extend / List for sale / Cancel sale / Teleport send C.GuildTerritoryAction through Territories.action (the
//    server runs the NPC scripts' BuyGT / ExtendGT / GTSale / GTCancelSale / TeleportGT checks); Buy sends
//    C.PurchaseGuildTerritory through Territories.purchase. Rent / Extend / Teleport / Buy need the territory NPC page
//    on the server (PlayerObject.IsAtNpcPage("[@GUILDTERRITORY]")), so they are enabled only while that page is open
//    (NpcSession.open + lastPageKey), else the window says "Speak to ..."; List for sale / Cancel sale need the player
//    standing on the guild's territory (the server checks it; the client cannot know a map's GT flag). The window shows
//    the client-side refusals the server would send (leader only, one territory per guild, guild funds) on the buttons
//    and leaves the rest to the server: results arrive as chat lines, then fresh pages (nothing changes locally);
//  - prices and lease length from S.ServerRules (BuyGTGold, ExtendGT, GTDays; hidden until it arrives); the minimum
//    sale price 2,000,000 is the server's constant (GuildTerritoryListForSale);
//  - lease days: ClientGTMap.days is the server's (Now - GTRent).Days snapshot (negative = days left), set when the
//    server loads or a purchase completes, so it is shown as "about N days" and only when negative;
//  - "Mail leader" opens the mail window's compose (composeTo(Leader), like the classic Mail button);
//  - HIDDEN (Crystal sends no data for them): the floor blueprint, amenities and monsters, the lease timeline, the
//    handover countdown (ClientGTMap.begin is only a pending flag), the "View as member" switch and the lab buttons.
// Narrow layout (a layer below 1040 px): a narrower list and detail column.

import type { ClientGTMap } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { Emitter } from "../../../game/emitter.ts";
import { serverRules } from "../../../game/serverRules.ts";
import { TERRITORY_ROWS, TerritoryAction, type Territories, type TerritoryActionKind } from "../../../game/territories.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import { UiWindow, button, chips, fmt, h, injectCss, parseAmount, plural, setStyle, setText, type Btn, type IconName } from "../core/index.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.territories). */
export const TERRITORIES_WINDOW = "territories";

const NONE = "None";                 // the server's owner / leader name of an unowned territory
const NPC_PAGE = "@GUILDTERRITORY";  // NPCScript.GuildTerritoryKey without the brackets (NpcSession.lastPageKey)
const MIN_SALE = 2_000_000;          // PlayerObject.GuildTerritoryListForSale
const MAX_SALE = 0x7fffffff;         // GTSale's int.TryParse (C.GuildTerritoryAction refuses above int.MaxValue)
const MAX_PENDING = 16;              // unanswered page requests kept before the queue is dropped (a dead player gets none)

// — The page book (DOM-free) —

/** Every territory page, filed from S.GuildTerritoryPage replies. One per UI runtime (territoryBook), built by the
 *  window's watch hook at UI start so a server-sent page is filed before the (lazily built) window exists. */
export class TerritoryBook {
  /** Rows or the length changed. */
  readonly changed = new Emitter<void>();
  private _rows: (ClientGTMap | undefined)[] = [];
  private _length = 0;
  private _received = false;
  /** Page numbers of the requests sent and not answered yet, oldest first (replies come back in order). */
  private readonly expect: number[] = [];
  /** Pages filed since the last full refresh. */
  private readonly loaded = new Set<number>();
  private _lastUnasked = false;
  /** The window was opened by an unasked page (an NPC); the window's shown() then skips its own full refresh. */
  serverOpened = false;

  constructor(private readonly model: Territories, private readonly send: (f: Uint8Array) => void, signal: AbortSignal) {
    const off = model.received.on(() => this.file());
    signal.addEventListener("abort", off, { once: true });
  }

  /** GT list in server order (holes = pages not received yet). */
  get rows(): readonly (ClientGTMap | undefined)[] { return this._rows; }
  /** GuildTerritoryDialog.Lenght (0 until the first reply). */
  get length(): number { return this._length; }
  /** At least one page arrived. */
  get received(): boolean { return this._received; }
  /** The last reply came unasked (the server's own GetGuildTerritories(0), e.g. at the territory NPC). */
  get lastUnasked(): boolean { return this._lastUnasked; }
  /** Requests are still out. */
  get loading(): boolean { return this.expect.length > 0; }
  private get pages(): number { return Math.max(1, Math.ceil(this._length / TERRITORY_ROWS)); }

  /** Asks for every page again (the window opened, or after a purchase). */
  refresh(): void {
    this.loaded.clear();
    for (let p = 0; p < this.pages; p++) this.request(p);
  }

  /** C.GuildTerritoryAction through the model. Except for Teleport the model re-requests its current page right after
   *  the action; the other pages are asked for here, so the whole list shows the result. False = nothing sent. */
  action(kind: TerritoryActionKind, index: number, price = 0): boolean {
    if (!this.model.action(kind, index, price)) return false;
    if (kind === TerritoryAction.Teleport) return true;
    this.queue(this.model.page);
    this.loaded.clear();
    for (let p = 0; p < this.pages; p++) if (p !== this.model.page) this.request(p);
    return true;
  }

  /** C.PurchaseGuildTerritory through the model (the server answers with chat lines only: every page is re-asked). */
  purchase(owner: string): void {
    this.model.purchase(owner);
    this.refresh();
  }

  /** C.GuildTerritoryPage for one page. The model's paging (prev / next) is the classic window's; this window lists every
   *  page at once, so it asks for pages by number and never moves the model's page (which stays 0: action() re-asks it). */
  private request(page: number): void {
    this.queue(page);
    this.send(ClientPackets.GuildTerritoryPage({ Page: page }));
  }

  private queue(page: number): void {
    if (this.expect.length >= MAX_PENDING) this.expect.length = 0;
    this.expect.push(page);
  }

  /** S.GuildTerritoryPage (the model has taken list and length). */
  private file(): void {
    const list = this.model.list;
    this._length = this.model.length;
    if (this._rows.length !== this._length) this._rows.length = this._length;
    let page = this.expect.shift();
    const unasked = page === undefined;
    this._lastUnasked = unasked;
    // Content wins over the queue: a reply whose first row is already filed at a page start is that page.
    if (list.length) {
      const at = this._rows.findIndex((r) => r?.index === list[0]!.index);
      if (at >= 0 && at % TERRITORY_ROWS === 0) page = at / TERRITORY_ROWS;
    }
    if (page === undefined) page = 0; // unasked: the NPC's GetGuildTerritories(0)
    const base = page * TERRITORY_ROWS;
    for (let i = 0; i < TERRITORY_ROWS && base + i < this._length; i++) this._rows[base + i] = list[i];
    this._received = true;
    this.loaded.add(page);
    if (unasked) {
      // A fresh list from the server: re-ask the other pages (an NPC opening the window sends only page 0).
      this.loaded.clear();
      this.loaded.add(page);
      for (let p = 0; p < this.pages; p++) if (p !== page) this.request(p);
    } else {
      // The list grew past what was asked for: fetch the missing pages.
      for (let p = 0; p < this.pages; p++) if (!this.loaded.has(p) && !this.expect.includes(p)) this.request(p);
    }
    this.changed.emit();
  }
}

/** The runtime's territory book (built on first use). */
export function territoryBook(host: ModernUiHost): TerritoryBook {
  return host.rt.service("territoryBook", () => new TerritoryBook(host.ctx.models.territories, (f) => host.ctx.send(f), host.rt.signal));
}

// — The window —

type Status = "vacant" | "sale" | "owned" | "pend";
type Filter = "All" | "Vacant" | "For sale" | "Owned";
const FILTERS: readonly Filter[] = ["All", "Vacant", "For sale", "Owned"];

/** GuildTerritoryDialog.UpdateInterface's status (owned rows of the user's guild are marked separately). */
function statusOf(gt: ClientGTMap): Status {
  if (gt.Owner === NONE) return "vacant";
  if (gt.begin > 0) return "pend";
  return gt.price > 0 ? "sale" : "owned";
}
const STATUS_LABEL: Record<Status, string> = { vacant: "Available", sale: "For sale", owned: "Unavailable", pend: "Sale pending" };
const passes = (f: Filter, s: Status): boolean => f === "All" || (f === "Vacant" ? s === "vacant" : f === "For sale" ? s === "sale" : s !== "vacant");
/** Crest initials: the capitals ("GameMaster" GM), else the first two letters (text only, never markup). */
const crest = (name: string): string => { const caps = name.replace(/[^A-Z]/g, ""); return (caps.length >= 2 ? caps : name).slice(0, 2).toUpperCase(); };
/** "Leader and Leader2" (AndPlaceholder). */
const leaders = (gt: ClientGTMap): string => gt.Leader + (gt.Leader2 !== "" ? ` and ${gt.Leader2}` : "");
/** Days left from the server's (Now - GTRent).Days snapshot; null when it holds nothing usable. */
const daysLeft = (gt: ClientGTMap): number | null => (gt.Owner !== NONE && gt.days < 0 ? -gt.days : null);

const CSS = `
.hudm .gtwin2 { width: 860px; }
.hudm .gtwin2 .panel { height: 560px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .gtwin2 .top { display: flex; align-items: center; gap: 14px; padding: 10px 16px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gtwin2 .crest { width: 40px; height: 40px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 13px; font-weight: 800; color: #fff;
  clip-path: polygon(50% 0, 100% 18%, 100% 62%, 50% 100%, 0 62%, 0 18%); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 70%, #fff), color-mix(in srgb, var(--ac) 45%, #000)); }
.hudm .gtwin2 .me { min-width: 0; } .hudm .gtwin2 .me b { display: block; font-size: 14px; color: #fff; } .hudm .gtwin2 .me span { font-size: 11px; color: var(--dim); }
.hudm .gtwin2 .stat { display: flex; flex-direction: column; padding: 0 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gtwin2 .stat small { font-size: 9px; font-weight: 700; letter-spacing: .14em; color: var(--dim); } .hudm .gtwin2 .stat b { font-size: 13px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gtwin2 .stat b.gold { color: var(--gold); }
.hudm .gtwin2 .top .sp { flex: 1; }
.hudm .gtwin2 .body { flex: 1; min-height: 0; display: flex; }
.hudm .gtwin2 .list { width: 380px; flex: none; display: flex; flex-direction: column; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gtwin2 .chipsrow { padding: 10px 12px 6px; }
.hudm .gtwin2 .rows { flex: 1; overflow-y: auto; padding: 4px 10px 10px; display: flex; flex-direction: column; gap: 5px; scrollbar-width: thin; }
.hudm .gtwin2 .er { display: grid; grid-template-columns: 34px 1fr auto; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); cursor: pointer;
  border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .gtwin2 .er:hover { border-color: color-mix(in srgb, var(--ac) 50%, var(--fr)); }
.hudm .gtwin2 .er.sel { border-color: var(--ac); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 22%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gtwin2 .er.mine { box-shadow: inset 3px 0 0 var(--gold); }
.hudm .gtwin2 .num { width: 34px; height: 34px; border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 14px; font-weight: 800; color: var(--ac);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .gtwin2 .er .nm { min-width: 0; } .hudm .gtwin2 .er .nm b { display: block; font-size: 12.5px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .gtwin2 .er .nm span { display: block; font-size: 10.5px; color: var(--dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .gtwin2 .tag { font-size: 9.5px; font-weight: 800; letter-spacing: .08em; padding: 2px 8px; border-radius: 9px; white-space: nowrap; border: 1px solid; text-transform: uppercase; }
.hudm .gtwin2 .tag.vacant { color: var(--ok); border-color: color-mix(in srgb, var(--ok) 50%, transparent); }
.hudm .gtwin2 .tag.sale { color: var(--gold); border-color: color-mix(in srgb, var(--gold) 50%, transparent); }
.hudm .gtwin2 .tag.owned { color: var(--dim); border-color: var(--fr); }
.hudm .gtwin2 .tag.pend { color: var(--ac); border-color: var(--ac); }
.hudm .gtwin2 .tag.mine { color: #000; background: var(--gold); border-color: var(--gold); }
.hudm .gtwin2 .det { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 14px 16px; gap: 12px; overflow-y: auto; scrollbar-width: thin; }
.hudm .gtwin2 .dh { display: flex; align-items: center; gap: 12px; }
.hudm .gtwin2 .dh h3 { margin: 0; font-size: 17px; color: #fff; letter-spacing: .04em; } .hudm .gtwin2 .dh small { display: block; font-size: 11px; color: var(--dim); }
.hudm .gtwin2 .dh .tag { margin-left: auto; }
.hudm .gtwin2 .card { border-radius: var(--rad); padding: 10px 12px; background: rgba(0,0,0,.22); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); font-size: 12px; }
.hudm .gtwin2 .card h4 { margin: 0 0 8px; font-size: 10px; letter-spacing: .16em; color: var(--ac); }
.hudm .gtwin2 .kv { display: grid; grid-template-columns: 120px 1fr; row-gap: 5px; } .hudm .gtwin2 .kv span { color: var(--dim); } .hudm .gtwin2 .kv b { color: #fff; font-weight: 600; }
.hudm .gtwin2 .kv b.gold { color: var(--gold); }
.hudm .gtwin2 .lb { height: 4px; margin-top: 8px; border-radius: 2px; background: #04060a; overflow: hidden; } .hudm .gtwin2 .lb i { display: block; height: 100%; width: 0; background: var(--ac); }
.hudm .gtwin2 .lb.low i { background: var(--bad-soft); }
.hudm .gtwin2 .acts { display: flex; flex-wrap: wrap; gap: 8px; }
.hudm .gtwin2 .acts .btn { height: 30px; }
.hudm .gtwin2 .why { font-size: 11px; color: var(--bad-soft); min-height: 14px; }
.hudm .gtwin2 .note { font-size: 11px; color: var(--dim); }
.hudm .gtwin2 .gate { font-size: 11px; color: var(--warn); }
/* Narrow layer */
.hudm.narrow .gtwin2 { width: 700px; }
.hudm.narrow .gtwin2 .list { width: 300px; }
.hudm.narrow .gtwin2 .top { flex-wrap: wrap; gap: 8px 12px; }
.hudm.narrow .gtwin2 .stat { padding: 0 10px; }
.hudm.narrow .gtwin2 .kv { grid-template-columns: 100px 1fr; }
`;

class TerritoriesWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private filter: Filter = "All";
  /** The selected territory (ClientGTMap.index; -1 = none, pick a default). */
  private sel = -1;
  private why = "";
  private chipsKey = "";

  // — DOM, built once —
  private readonly crestEl = h("div", "crest");
  private readonly guildName = h("b");
  private readonly guildRole = h("span");
  private readonly goldEl = h("b", "gold");
  private readonly mineEl = h("b");
  private readonly chipsBox = h("div", "chipsrow");
  private readonly rowsEl = h("div", "rows");
  private readonly det = h("div", "det");

  constructor(private readonly ui: ModernUiHost, private readonly book: TerritoryBook) {
    super(ui.rt, "gtwin2", 42, 40);
    injectCss("win-territories", CSS);
    this.m = ui.ctx.models;
    const top = h("div", "top", this.crestEl, h("div", "me", this.guildName, this.guildRole),
      h("div", "stat", h("small", "", "GUILD GOLD"), this.goldEl), h("div", "stat", h("small", "", "YOUR TERRITORY"), this.mineEl), h("div", "sp"),
      button("Refresh", () => this.book.refresh(), { icon: "refresh", title: "Ask the server for the list again" }).el);
    this.el.append(h("div", "panel", this.titleBar("GUILD TERRITORIES", "Close"), top,
      h("div", "body", h("div", "list", this.chipsBox, this.rowsEl), this.det)));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const sig = ui.rt.signal, on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    const inv = () => this.invalidate();
    on(book.changed.on(inv));
    on(this.m.guild.changed.on(inv));
    on(this.m.npcSession.opened.on(inv));
    on(this.m.npcSession.page.on(inv));
    on(this.m.npcSession.closed.on(inv));
    on(serverRules(ui.ctx.session).changed.on(inv));
  }

  protected override shown(): void {
    this.why = "";
    // An NPC-opened window already has page 0 and the book asks for the rest; a menu open asks for everything.
    if (this.book.serverOpened) this.book.serverOpened = false;
    else this.book.refresh();
  }

  // — State helpers —

  private get rules() { return serverRules(this.ui.ctx.session).rules; }
  private get myGuild(): string { return this.m.guild.name; }
  /** MyGuildRank == Ranks[0] (the server's leader check). */
  private get leader(): boolean { return this.myGuild !== "" && this.m.guild.myRankId === 0; }
  /** The guild's territory in the list (GuildObject.HasGT as far as the loaded pages tell). */
  private mine(): ClientGTMap | undefined {
    if (!this.myGuild) return undefined;
    for (const r of this.book.rows) if (r && r.Owner === this.myGuild) return r;
    return undefined;
  }
  /** The territory NPC page is open (PlayerObject.IsAtNpcPage("[@GUILDTERRITORY]"); the server checks the range). */
  private atNpc(): boolean {
    const s = this.m.npcSession;
    return s.open && s.lastPageKey.toUpperCase() === NPC_PAGE;
  }

  // — Render (only when something changed, once a frame at most) —

  protected override render(): void {
    const g = this.m.guild, rows = this.book.rows, mine = this.mine();
    setText(this.crestEl, this.myGuild ? crest(this.myGuild) : "–");
    setText(this.guildName, this.myGuild || "No guild");
    setText(this.guildRole, !this.myGuild ? "Join a guild to hold a territory" : this.leader ? `Leader · ${g.rankName}` : g.rankName || "Member");
    setText(this.goldEl, this.myGuild ? fmt(g.gold) : "–");
    const left = mine ? daysLeft(mine) : null;
    setText(this.mineEl, mine ? `${mine.Name || `No. ${rows.indexOf(mine) + 1}`}${left !== null ? ` · ~${plural(left, "day")}` : ""}` : "None");

    // Chips with counts (rebuilt only when a count changed)
    const counts: Record<Filter, number> = { All: 0, Vacant: 0, "For sale": 0, Owned: 0 };
    for (const r of rows) if (r) { const s = statusOf(r); for (const f of FILTERS) if (passes(f, s)) counts[f]++; }
    const key = [this.filter, ...FILTERS.map((f) => counts[f])].join(",");
    if (key !== this.chipsKey) {
      this.chipsKey = key;
      const ch = chips(FILTERS.map((f) => ({ value: f, label: `${f} ${counts[f]}` })), [this.filter], (sel) => {
        const next = sel.values().next().value ?? "All";
        if (next === this.filter) return;
        this.filter = next;
        this.chipsKey = "";
        this.invalidate();
      }, { multi: false, allowNone: false });
      this.chipsBox.replaceChildren(ch.el);
    }

    // Default selection: the guild's own, else the first available, else the first
    if (this.sel < 0 || !rows.some((r) => r?.index === this.sel)) {
      const pick = mine ?? rows.find((r) => r && r.Owner === NONE) ?? rows.find((r) => r);
      this.sel = pick ? pick.index : -1;
    }

    // The list (a handful of rows; rebuilt on change only)
    const items: HTMLElement[] = [];
    const gtDays = this.rules?.GTDays ?? 0;
    rows.forEach((r, i) => {
      if (!r) return;
      const s = statusOf(r);
      if (!passes(this.filter, s)) return;
      const own = r.Owner === this.myGuild && this.myGuild !== "";
      const days = daysLeft(r);
      const sub = r.Owner === NONE
        ? (this.rules ? `For rent · ${fmt(this.rules.BuyGTGold)} for ${plural(this.rules.GTDays, "day")}` : "For rent")
        : `${r.Owner} · ${leaders(r)}${days !== null ? ` · ~${plural(days, "day")}` : ""}${r.price > 0 ? ` · ${fmt(r.price)}` : ""}`;
      const el = h("div", "er" + (r.index === this.sel ? " sel" : "") + (own ? " mine" : ""),
        h("div", "num", String(i + 1)), h("div", "nm", h("b", "", r.Name || `Territory ${i + 1}`), h("span", "", sub)), this.tag(r, own));
      if (days !== null && gtDays > 0) {
        const fill = h("i");
        setStyle(fill, "width", `${Math.min(100, days / gtDays * 100).toFixed(0)}%`);
        const lb = h("div", "lb" + (days <= 5 ? " low" : ""), fill);
        lb.style.gridColumn = "2 / 4";
        el.append(lb);
      }
      el.addEventListener("click", () => { if (this.sel !== r.index) { this.sel = r.index; this.why = ""; this.invalidate(); } });
      items.push(el);
    });
    if (!items.length) {
      items.push(h("div", "empty-note", !this.book.received ? "Loading the territories…" : this.book.length === 0 ? "This server has no guild territories." : "None match the filter."));
    }
    this.rowsEl.replaceChildren(...items);
    this.renderDetail(rows.find((r) => r?.index === this.sel));
  }

  private tag(r: ClientGTMap, own: boolean): HTMLElement {
    const s = statusOf(r);
    if (own) return h("span", "tag " + (s === "owned" ? "mine" : s), s === "owned" ? "Yours" : s === "sale" ? "Yours · for sale" : "Keys pending");
    return h("span", "tag " + s, STATUS_LABEL[s]);
  }

  private renderDetail(gt: ClientGTMap | undefined): void {
    if (!gt) {
      this.det.replaceChildren(h("div", "note", this.book.received ? "Select a territory." : "Loading the territories…"));
      return;
    }
    const rows = this.book.rows, no = rows.indexOf(gt) + 1, rules = this.rules;
    const own = gt.Owner === this.myGuild && this.myGuild !== "", s = statusOf(gt);
    const head = h("div", "dh", h("div", "num", String(no)),
      h("div", "", h("h3", "", gt.Name || `Territory ${no}`), h("small", "", gt.Owner === NONE ? "Unclaimed" : `Held by ${gt.Owner}`)), this.tag(gt, own));

    // Facts
    const kv = h("div", "kv");
    const add = (k: string, v: string, cls = "") => kv.append(h("span", "", k), h("b", cls, v));
    if (gt.Owner === NONE) {
      if (rules) {
        add("Rent", `${fmt(rules.BuyGTGold)} guild gold`, "gold");
        add("Lease", plural(rules.GTDays, "day"));
        add("Extend", `${fmt(rules.ExtendGT)} for ${plural(rules.GTDays, "more day")}`);
      }
      add("Rules", "Guild leader only · one territory per guild");
    } else {
      add("Guild", gt.Owner);
      add("Leaders", leaders(gt));
      const days = daysLeft(gt);
      if (days !== null) add("Lease left", `about ${plural(days, "day")}`);
      if (gt.price > 0 && s !== "pend") add("Asking price", `${fmt(gt.price)} guild gold`, "gold");
      if (s === "pend") add("Keys", "Sale pending: the keys change hands 24 hours after a purchase");
      if (own && rules) add("Extend", `${fmt(rules.ExtendGT)} for ${plural(rules.GTDays, "more day")}`);
    }
    const card = h("div", "card", h("h4", "", gt.Owner === NONE ? "LEASE TERMS" : "LEASE"), kv);

    // Actions: refusals the client can tell are shown on the button (the server still checks everything)
    const acts = h("div", "acts");
    const npc = this.atNpc();
    const npcWhy = npc ? "" : "Speak to the guild territory keeper to do that.";
    const lead = !this.myGuild ? "You need to be in a guild." : !this.leader ? "Only the guild leader can do that." : "";
    const mine = this.mine();
    const g = this.m.guild;
    const btn = (label: string, why: string, run: () => void, o: { main?: boolean; icon?: IconName } = {}): Btn => {
      const b = button(label, () => run(), { cls: o.main ? "main" : "", icon: o.icon });
      if (why) b.setDisabled(true, why);
      acts.append(b.el);
      return b;
    };
    let gated = false;
    if (gt.Owner === NONE) {
      const cost = rules?.BuyGTGold ?? 0;
      const why = lead || (mine ? "Your guild already owns a territory." : rules && g.gold < cost ? "The guild's funds are insufficient." : "") || npcWhy;
      gated ||= !lead && !npc;
      btn(rules ? `Rent · ${fmt(cost)}` : "Rent", why, () => void this.rent(gt, no), { main: true, icon: "coin" });
    } else if (own) {
      gated ||= !npc;
      btn("Teleport in", npcWhy || (s === "pend" ? "The recent sale is still pending: the keys change hands 24 hours after a purchase." : ""),
        () => this.act(TerritoryAction.Teleport, -1), { main: true, icon: "walk" });
      const ext = rules?.ExtendGT ?? 0;
      btn(rules ? `Extend · ${fmt(ext)}` : "Extend", lead || (rules && g.gold < ext ? "Insufficient funds." : "") || npcWhy,
        () => void this.extend(), { icon: "calendar" });
      if (gt.price > 0 && s !== "pend") btn("Cancel sale", lead, () => this.act(TerritoryAction.CancelSale, -1), { icon: "cross" });
      else if (s !== "pend") btn("List for sale…", lead, () => void this.listForSale(gt, no), { icon: "tag" });
    } else if (s === "sale") {
      gated ||= !lead && !npc;
      btn(`Buy · ${fmt(gt.price)}`, lead || (mine ? "Your guild already owns a territory." : g.gold < gt.price ? "Insufficient funds." : "") || npcWhy,
        () => void this.buy(gt, no), { main: true, icon: "coin" });
    } else {
      acts.append(h("span", "note", s === "pend" ? "A sale is pending." : "Not for sale."));
    }
    if (gt.Owner !== NONE && gt.Leader !== "" && gt.Leader !== NONE && !own) {
      btn("Mail leader", "", () => this.mailLeader(gt.Leader), { icon: "mail" });
    }

    const notes: HTMLElement[] = [];
    if (gated) notes.push(h("div", "gate", "Renting, extending, buying and teleporting are done at the guild territory keeper: speak to them to use these buttons."));
    if (own) notes.push(h("div", "note", "Listing for sale and cancelling a sale need you to stand inside your territory."));
    this.det.replaceChildren(head, card, acts, h("div", "why", this.why), ...notes);
  }

  // — Actions (the server answers with chat lines and fresh pages; nothing changes here) —

  private act(kind: TerritoryActionKind, index: number, price = 0): void {
    if (!this.book.action(kind, index, price)) { this.why = "That request is not valid."; this.invalidate(); return; }
    this.why = "";
    this.invalidate();
  }

  private async rent(gt: ClientGTMap, no: number): Promise<void> {
    const r = this.rules;
    const ok = await this.ui.rt.popups.ask(r
      ? `Rent ${gt.Name || `territory ${no}`} for ${fmt(r.BuyGTGold)} guild gold? The lease runs ${plural(r.GTDays, "day")}.`
      : `Rent ${gt.Name || `territory ${no}`}?`, { title: "Rent territory", kind: "question", gold: r?.BuyGTGold });
    if (ok && this.open) this.act(TerritoryAction.Rent, gt.index);
  }

  private async extend(): Promise<void> {
    const r = this.rules;
    const ok = await this.ui.rt.popups.ask(r
      ? `Extend your territory's lease by ${plural(r.GTDays, "day")} for ${fmt(r.ExtendGT)} guild gold?`
      : "Extend your territory's lease?", { title: "Extend lease", kind: "question", gold: r?.ExtendGT });
    if (ok && this.open) this.act(TerritoryAction.Extend, -1);
  }

  private async listForSale(gt: ClientGTMap, no: number): Promise<void> {
    const v = await this.ui.rt.popups.input({
      title: "Sell territory", text: `Asking price for ${gt.Name || `territory ${no}`} in guild gold (at least ${fmt(MIN_SALE)}).\nStand inside your territory to list it.`,
      placeholder: fmt(MIN_SALE), max: 14, chars: "0-9,.kKmMbB",
      check: (s) => {
        const n = parseAmount(s);
        if (Number.isNaN(n)) return s ? "Enter an amount" : "";
        if (n < MIN_SALE) return `At least ${fmt(MIN_SALE)}`;
        if (n > MAX_SALE) return `At most ${fmt(MAX_SALE)}`;
        return "";
      },
    });
    if (v === null || !this.open) return;
    const n = parseAmount(v);
    if (Number.isNaN(n) || n < MIN_SALE || n > MAX_SALE) return;
    this.act(TerritoryAction.ListForSale, -1, n);
  }

  private async buy(gt: ClientGTMap, no: number): Promise<void> {
    const ok = await this.ui.rt.popups.ask(
      `Buy ${gt.Name || `territory ${no}`} from ${gt.Owner} for ${fmt(gt.price)} guild gold?\nThe keys change hands after 24 hours.`,
      { title: "Buy territory", kind: "question", gold: gt.price });
    if (!ok || !this.open) return;
    this.book.purchase(gt.Owner); // GuildTerritoryDialog.BuyButton: by the owning guild's name
    this.why = "";
    this.invalidate();
  }

  /** mailButton.Click: MailComposeLetterDialog.ComposeMail(Leader) through the mail window when it offers composeTo. */
  private mailLeader(name: string): void {
    const w = this.ui.rt.window<UiWindow & { composeTo?: (name: string) => void }>("mail");
    if (!w) { this.why = "Mail is not available."; this.invalidate(); return; }
    if (typeof w.composeTo === "function") w.composeTo(name);
    else this.ui.openWindow("mail", true);
  }
}

registerWindow(TERRITORIES_WINDOW, (host) => new TerritoriesWindow(host, territoryBook(host)), {
  watch: (host, open) => watchTerritories(host, open),
});

/** Builds the page book and opens the window when the server sends a page while it is hidden (an NPC's
 *  GetGuildTerritories(0); GameScene.GuildTerritoryPage shows the dialog). Run once per game by the watch hook; returns
 *  its removal. The book subscribes first, so the page is filed before the window opens. */
export function watchTerritories(host: ModernUiHost, open: (show?: boolean) => boolean): () => void {
  const book = territoryBook(host);
  const off = host.ctx.models.territories.received.on(() => {
    if (!book.lastUnasked || host.rt.isOpen(TERRITORIES_WINDOW)) return; // replies to the window's own requests never reopen it
    book.serverOpened = true;
    open(true);
  });
  host.rt.signal.addEventListener("abort", off, { once: true });
  return off;
}
