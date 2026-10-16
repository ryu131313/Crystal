// Ported from play/hudGuild.ts (GuildMock): the new UI's guild hub over the real guild model (src/game/guild.ts, the
// GuildDialog state + GameScene's guild handlers) and Inventory.guildStorage. Desktop counterpart: GuildDialog
// (Client/MirScenes/Dialogs/GuildDialog.cs); the classic port is src/ui/guildWindow.ts (its rules are kept: who may edit
// what, the throttles, the texts). Toggle with Guilds (G) or the menu; Esc closes it.
//
// Changes from the mock:
//  - banner from S.GuildStatus (name, level, EXP / MaxExperience, members / MaxMembers, fund, spare points, your rank);
//    the crest shows the guild's initials (Crystal keeps no emblem), no founder / founded / weekly-fund lines;
//  - Overview: notice board (C.RequestGuildInfo 0 through GuildState.requestNotice; Edit only with CanChangeNotice,
//    Save sends C.EditGuildNotice and stays read-only until S.GuildNoticeChange answers), who is online, recent log;
//  - Members: the roster is GuildState.ranks (S.GuildMemberChange 255, asked with requestMembers while a page that
//    shows it is open) enriched by S.GuildMemberDetails (class, level; requestMemberDetails on open / Refresh, 5 s
//    throttle). Until the details arrive the rows show names, ranks and online / last login only. Rank changes use the
//    DYNAMIC rank list: Promote / Demote / "Set rank" send C.EditGuildMember 2 for ranks at or below your own
//    (CanChangeRank), Kick sends ChangeType 1 (CanKick), Invite ChangeType 0 (CanRecruit), each after a popup and with
//    the desktop's 5 s throttle; the row stays locked until the server's answer (no local moves). Leave = "@LEAVEGUILD";
//  - Ranks (CanChangeRank only, as the desktop's Rank button): every rank with its option toggles (C.EditGuildMember 5,
//    300 ms apart; the toggle shows the model's value and waits for S.GuildMemberChange 7), Rename (3, 3-20 chars, 5 s
//    throttle) and New rank (4, up to 255);
//  - Buffs: S.GuildBuffList (GuildBuffInfo / GuildBuff): GuildSkill icons (Icon, +1 active, +2 not obtained), the real
//    stats text, requirements, Obtain (points) / Activate (guild gold) via C.GuildBuffUpdate with the desktop's checks
//    as disabled reasons. ActiveTimeRemaining is whole minutes at send time (the server counts it down once a minute and
//    sends nothing until it ends), so the countdown runs from the time this UI received it (GuildBuffClock, started by
//    the registry watch hook at UI start);
//  - Storage (CanStoreItem or CanRetrieveItem): the 112 GuildStorage cells (core/itemCells.ts grid "guild"; the drag
//    routes check the rank's rights), the list asked with C.GuildStorageItemChange 3 when the page opens; gold deposit
//    (amount up to Inventory.gold) and leader-only withdraw (C.GuildStorageGoldChange 0 / 1). The server refuses
//    storage use outside safe zones (it says so in chat); nothing moves until it answers;
//  - Wars: S.GuildWarList with countdowns (wars are declared through an NPC script: no button);
//  - Log: C.RequestGuildLog / S.GuildLog (5 s throttle) with kind filters.
// HIDDEN (no system in Crystal): alliances (and the unused CanAlterAlliance bit), member location / joined date /
// contribution, emblem upload, the five fixed ranks.
// Narrow layout (a layer below 1040 px): icon-only side menu, single-column pages, the storage grid 8 wide.

import type { GuildBuff, GuildBuffInfo, GuildRank } from "../../../protocol/server.ts";
import { GUILD_LOG_KIND } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { StatName } from "../../../protocol/enums.generated.ts";
import { GuildRankOptions as Opt, type GuildState } from "../../../game/guild.ts";
import { GUILD_STORAGE_SLOTS } from "../../../game/inventory.ts";
import { dateTimeBinaryToMs } from "../../../game/accountDetails.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, chips, classVar, className, fmt, fmtAgo, fmtClock, fmtCompact, h, injectCss, later, pct, plural,
  pretty, searchBox, setStyle, setText, signed, svg, svgNode, toggleSwitch, typingField, type Btn, type IconName,
} from "../core/index.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS). */
export const GUILD_WINDOW = "guild";

type Page = "Overview" | "Members" | "Ranks" | "Buffs" | "Storage" | "Wars" | "Log";
const PAGES: readonly [Page, IconName][] = [
  ["Overview", "home"], ["Members", "friends"], ["Ranks", "star"], ["Buffs", "spark"], ["Storage", "bank"], ["Wars", "kill"], ["Log", "scroll"],
];

/** GuildDialog's option labels by bit (1 << i); CanAlterAlliance (bit 5) is unused by Crystal and hidden. */
const OPTIONS: readonly [number, string, string][] = [
  [Opt.CanChangeRank, "Ranks", "Edit ranks and members' ranks"], [Opt.CanRecruit, "Recruit", "Recruit members"],
  [Opt.CanKick, "Kick", "Kick members"], [Opt.CanStoreItem, "Store", "Store items in the guild storage"],
  [Opt.CanRetrieveItem, "Retrieve", "Take items from the guild storage"], [Opt.CanChangeNotice, "Notice", "Change the notice"],
  [Opt.CanActivateBuff, "Buffs", "Obtain and activate guild buffs"],
];
const MAX_RANKS = 255;          // GuildDialog: "Add New" while Ranks.Count < 255
const MEMBER_MSG_GAP = 5000;    // GuildDialog.LastGuildMsg after a rank change / kick / recruit
const OPTION_GAP = 300;         // SwitchRankOption
const RANK_NAME_GAP = 5000;     // LastRankNameChange
const GOLD_GAP = 100;           // StorageAddGold / StorageRemoveGold
const BUFF_GAP = 100;           // RequestBuff
const PENDING_MS = 5000;        // a request with no answer unlocks its control after this

type LogFilter = "All" | "Members" | "Notice" | "Buffs" | "Wars" | "Gold";
const LOG_FILTER: Readonly<Record<number, LogFilter>> = {
  [GUILD_LOG_KIND.Join]: "Members", [GUILD_LOG_KIND.Leave]: "Members", [GUILD_LOG_KIND.Kick]: "Members", [GUILD_LOG_KIND.RankChange]: "Members",
  [GUILD_LOG_KIND.Notice]: "Notice", [GUILD_LOG_KIND.Buff]: "Buffs", [GUILD_LOG_KIND.War]: "Wars", [GUILD_LOG_KIND.Territory]: "Wars",
  [GUILD_LOG_KIND.Gold]: "Gold",
};
const LOG_ICON: Readonly<Record<LogFilter, IconName>> = { All: "scroll", Members: "friends", Notice: "memo", Buffs: "spark", Wars: "kill", Gold: "coin" };

// — The buff clock (DOM-free; one per UI runtime, fed from UI start) —

/** When this UI saw each active buff's ActiveTimeRemaining (whole minutes), so its countdown can run between packets. */
export class GuildBuffClock {
  private readonly seen = new Map<number, { rem: number; at: number }>();

  constructor(private readonly guild: GuildState, private readonly now: () => number, signal: AbortSignal) {
    const off = guild.changed.on(() => this.sync());
    signal.addEventListener("abort", off, { once: true });
    this.sync();
  }

  /** Records a new remaining time (a buff activated, or the login list), drops buffs that stopped. */
  private sync(): void {
    const buffs = this.guild.enabledBuffs, t = this.now();
    for (const id of [...this.seen.keys()]) if (!buffs.some((b) => b.Id === id && b.Active)) this.seen.delete(id);
    for (const b of buffs) {
      if (!b.Active) continue;
      const s = this.seen.get(b.Id);
      if (!s || s.rem !== b.ActiveTimeRemaining) this.seen.set(b.Id, { rem: b.ActiveTimeRemaining, at: t });
    }
  }

  /** Milliseconds left in an active buff (approximate: the server ends it on its own minute tick). */
  left(b: GuildBuff): number {
    const s = this.seen.get(b.Id);
    const rem = (s?.rem ?? b.ActiveTimeRemaining) * 60_000;
    return Math.max(0, rem - (s ? this.now() - s.at : 0));
  }
}

/** The runtime's buff clock (built at UI start by the watch hook, or on first use). */
export function guildBuffClock(host: ModernUiHost): GuildBuffClock {
  return host.rt.service("guildBuffClock", () => new GuildBuffClock(host.ctx.models.guild, () => host.ctx.now(), host.rt.signal));
}

// — The window —

/** One roster row: GuildRank members, enriched by S.GuildMemberDetails (class / level null until they arrive). */
interface Row { name: string; rank: number; rankName: string; online: boolean; lastLogin: bigint; cls: number | null; level: number | null }
type SortKey = "name" | "cls" | "level" | "rank" | "seen";

const CSS = `
.hudm .gdwin { width: 880px; }
.hudm .gdwin .panel { height: 560px; display: flex; flex-direction: column; }
/* Banner */
.hudm .gdwin .banner { display: flex; align-items: center; gap: 16px; padding: 12px 18px; flex: none;
  background: radial-gradient(ellipse 50% 140% at 0% 50%, color-mix(in srgb, var(--ac) 18%, transparent), transparent 70%),
    linear-gradient(180deg, color-mix(in srgb, var(--pa) 60%, transparent), transparent);
  border-bottom: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .gdwin .emblem { width: 56px; height: 64px; flex: none; }
.hudm .gdwin .emblem path { fill: var(--pc); stroke: var(--ac); stroke-width: 2.5; }
.hudm .gdwin .emblem text { fill: #fff; font: 700 20px Georgia, serif; }
.hudm .gdwin .gname { min-width: 0; }
.hudm .gdwin .gname h2 { margin: 0; font-size: 20px; letter-spacing: .04em; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .gdwin .gname .sub { margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .gdwin .gname .sub b { color: var(--ac); }
.hudm .gdwin .gexp { width: 240px; margin-top: 6px; }
.hudm .gdwin .gexp .t { display: flex; justify-content: space-between; gap: 8px; font-size: 10px; color: var(--dim); margin-bottom: 3px; }
.hudm .gdwin .bar { height: 6px; border-radius: 3px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .gdwin .bar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .gdwin .bstats { margin-left: auto; display: flex; gap: 8px; }
.hudm .gdwin .bstat { min-width: 80px; padding: 6px 10px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--pb) 55%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .gdwin .bstat b { display: block; font-size: 14px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gdwin .bstat span { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .gdwin .bstat.hot b { color: var(--ac); }
/* Side menu + page */
.hudm .gdwin .gmain { flex: 1; min-height: 0; display: flex; }
.hudm .gdwin .nav { width: 140px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gdwin .nav > button { display: flex; align-items: center; gap: 9px; height: 30px; padding: 0 10px; cursor: pointer; border-radius: var(--rad); font: inherit;
  font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; background: none; text-align: left; }
.hudm .gdwin .nav > button:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .gdwin .nav > button.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gdwin .nav > button.on svg { color: var(--ac); }
.hudm .gdwin .nav i { margin-left: auto; font-style: normal; font-size: 10px; padding: 0 6px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .gdwin .nav .sp { flex: 1; }
.hudm .gdwin .nav .you { padding: 8px 6px 2px; font-size: 10px; color: var(--dim); border-top: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .gdwin .nav .you b { display: block; font-size: 12px; color: var(--ac); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .gdwin .nav .you .btn { margin-top: 6px; width: 100%; }
.hudm .gdwin .page { flex: 1; min-width: 0; min-height: 0; padding: 12px 14px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gdwin .card { border-radius: var(--rad); padding: 10px 12px; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .gdwin .card + .card, .hudm .gdwin .grid2 + .card { margin-top: 12px; }
.hudm .gdwin .ch { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .gdwin .ch .sp { flex: 1; }
.hudm .gdwin .ch .btn { height: 20px; font-size: 10px; letter-spacing: .04em; text-transform: none; }
.hudm .gdwin .grid2 { display: grid; grid-template-columns: 1.35fr 1fr; gap: 12px; align-items: start; }
.hudm .gdwin .cards4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 12px; }
.hudm .gdwin .kpi b { display: block; font-size: 19px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gdwin .kpi span { font-size: 10px; color: var(--dim); }
.hudm .gdwin .kpi small { display: block; margin-top: 4px; font-size: 10px; color: var(--acd); }
.hudm .gdwin .notice { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; line-height: 1.6; max-height: 260px; overflow-y: auto; scrollbar-width: thin; }
.hudm .gdwin textarea.fld { width: 100%; box-sizing: border-box; height: 220px; font-size: 12px; line-height: 1.6; }
.hudm .gdwin .who { display: flex; align-items: center; gap: 8px; padding: 4px 2px; font-size: 12px; }
.hudm .gdwin .who .m { margin-left: auto; font-size: 10px; color: var(--dim); }
.hudm .gdwin .dot { width: 7px; height: 7px; border-radius: 50%; flex: none; background: #59606e; }
.hudm .gdwin .dot.on { background: #4fe07a; }
.hudm .gdwin .tag { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); white-space: nowrap; }
.hudm .gdwin .tag.lead { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .gdwin .feed > div { display: flex; align-items: center; gap: 9px; padding: 6px 0; font-size: 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .gdwin .feed > div:last-child { border-bottom: 0; }
.hudm .gdwin .feed svg { color: var(--acd); }
.hudm .gdwin .feed .tx { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.hudm .gdwin .feed span.w { font-size: 10px; color: var(--dim); white-space: nowrap; }
.hudm .gdwin .note { min-height: 14px; margin-top: 8px; font-size: 11px; color: var(--ac); }
.hudm .gdwin .tools { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 10px; }
.hudm .gdwin .tools .ui-search { width: 200px; }
.hudm .gdwin .tools .sp { flex: 1; }
/* Members */
.hudm .gdwin .mgrid { display: grid; grid-template-columns: 1fr 220px; gap: 12px; align-items: start; }
.hudm .gdwin table { width: 100%; border-collapse: collapse; font-size: 12px; }
.hudm .gdwin th { position: sticky; top: 0; text-align: left; padding: 6px 8px; font-size: 10px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim);
  background: color-mix(in srgb, var(--pb) 90%, #000); white-space: nowrap; }
.hudm .gdwin th.sort { cursor: pointer; user-select: none; } .hudm .gdwin th.sort:hover { color: var(--tx); } .hudm .gdwin th.on { color: var(--ac); }
.hudm .gdwin td { padding: 5px 8px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); white-space: nowrap; }
.hudm .gdwin .mt tbody tr { cursor: pointer; }
.hudm .gdwin .mt tbody tr:hover td { background: color-mix(in srgb, var(--ac) 7%, transparent); }
.hudm .gdwin .mt tbody tr.sel td { background: color-mix(in srgb, var(--ac) 16%, transparent); }
.hudm .gdwin td.dim { color: var(--dim); font-size: 11px; }
.hudm .gdwin .nmc { display: inline-flex; align-items: center; gap: 7px; }
.hudm .gdwin .tblwrap { max-height: 380px; overflow: auto; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gdwin .mcard h3 { margin: 0 0 4px; font-size: 15px; color: #fff; display: flex; align-items: center; gap: 7px; overflow-wrap: anywhere; }
.hudm .gdwin .mcard .ln { display: flex; justify-content: space-between; gap: 8px; font-size: 11px; padding: 4px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .gdwin .mcard .ln span { color: var(--dim); } .hudm .gdwin .mcard .ln b { color: var(--tx); font-weight: 600; }
.hudm .gdwin .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; margin-top: 10px; }
.hudm .gdwin .acts .btn { padding: 0 6px; }
.hudm .gdwin .acts select.fld { grid-column: 1 / -1; width: 100%; }
/* Ranks */
.hudm .gdwin .perm th, .hudm .gdwin .perm td { text-align: center; }
.hudm .gdwin .perm th:first-child, .hudm .gdwin .perm td:first-child { text-align: left; }
.hudm .gdwin .perm td:first-child b { color: #fff; } .hudm .gdwin .perm td:first-child small { margin-left: 6px; color: var(--dim); }
.hudm .gdwin .perm .btn { height: 20px; padding: 0 8px; font-size: 10px; }
/* Buffs */
.hudm .gdwin .bgrid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; }
.hudm .gdwin .gbuff { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .gdwin .gbuff.act { border-color: var(--ac); }
.hudm .gdwin .gbuff > i { width: 36px; height: 36px; flex: none; border-radius: calc(var(--rad) * .8); background: center / auto no-repeat #000; image-rendering: pixelated; border: 1px solid var(--fr); }
.hudm .gdwin .gbuff .mid { flex: 1; min-width: 0; }
.hudm .gdwin .gbuff .mid b { display: block; font-size: 12px; color: var(--tx); }
.hudm .gdwin .gbuff .mid span { display: block; font-size: 10px; color: var(--dim); }
.hudm .gdwin .gbuff .mid span.fx { color: var(--acd); }
.hudm .gdwin .gbuff .st { font-size: 10px; text-align: right; color: var(--dim); min-width: 70px; }
.hudm .gdwin .gbuff .st b { display: block; font-size: 12px; color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .gdwin .gbuff .btn { height: 24px; padding: 0 10px; }
/* Storage */
.hudm .gdwin .sgrid { display: grid; grid-template-columns: repeat(14, 40px); gap: 4px; }
.hudm .gdwin .money { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 12px; }
.hudm .gdwin .money .fund { margin-left: auto; font-size: 12px; color: var(--dim); }
.hudm .gdwin .money .fund b { color: var(--gold); font-size: 14px; font-variant-numeric: tabular-nums; }
.hudm .gdwin .hint { margin-top: 8px; font-size: 10px; color: var(--dim); }
/* Wars */
.hudm .gdwin .drow { display: flex; align-items: center; gap: 10px; padding: 9px 4px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); font-size: 12px; }
.hudm .gdwin .drow:last-child { border-bottom: 0; }
.hudm .gdwin .drow svg { color: var(--bad); }
.hudm .gdwin .drow .nm { font-weight: 700; color: #fff; min-width: 0; overflow-wrap: anywhere; }
.hudm .gdwin .drow .timer { margin-left: auto; font-size: 11px; color: var(--acd); font-variant-numeric: tabular-nums; white-space: nowrap; }
.hudm .gdwin .nog { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; color: var(--dim); text-align: center; }
.hudm .gdwin .nog svg.ic { width: 40px; height: 40px; color: var(--ac); }
.hudm .gdwin .nog b { font-size: 15px; color: #fff; }
/* Narrow layer */
.hudm.narrow .gdwin { width: 640px; }
.hudm.narrow .gdwin .banner { flex-wrap: wrap; gap: 8px 12px; padding: 10px 12px; }
.hudm.narrow .gdwin .bstats { margin-left: 0; width: 100%; }
.hudm.narrow .gdwin .bstat { flex: 1; min-width: 0; }
.hudm.narrow .gdwin .nav { width: 46px; padding: 8px 4px; }
.hudm.narrow .gdwin .nav > button { padding: 0; justify-content: center; }
.hudm.narrow .gdwin .nav > button span, .hudm.narrow .gdwin .nav i, .hudm.narrow .gdwin .nav .you { display: none; }
.hudm.narrow .gdwin .grid2, .hudm.narrow .gdwin .mgrid, .hudm.narrow .gdwin .bgrid { grid-template-columns: 1fr; }
.hudm.narrow .gdwin .cards4 { grid-template-columns: repeat(2, 1fr); }
.hudm.narrow .gdwin .sgrid { grid-template-columns: repeat(8, 40px); }
.hudm.narrow .gdwin .tblwrap { max-height: 260px; }
`;

class GuildWindow extends UiWindow {
  private readonly g: GuildState;
  private readonly m: ModernUiHost["ctx"]["models"];
  private page: Page = "Overview";
  private pageShown: Page | null = null;
  private readonly pages = new Map<Page, HTMLElement>();
  private readonly built = new Set<Page>();

  // dirty flags per region (model events set them; render() rebuilds only what changed)
  private rosterDirty = true;
  private rosterCache: Row[] = [];
  private noticeDirty = true;
  private buffsDirty = true;
  private warsDirty = true;
  private logDirty = true;
  private ranksDirty = true;

  // throttles / pending requests (server authoritative: controls wait for the answer)
  private lastMemberMsg = 0;
  private lastOption = 0;
  private lastRankName = 0;
  private lastGold = 0;
  private lastBuff = 0;
  private pendingMember = "";
  private pendingRank = -1;
  private pendingBuff = -1;
  private noticeSaving = false;
  private editing = false;
  private focusSearch = false;

  // members page state
  private query = "";
  private onlineOnly = false;
  private sort: { key: SortKey; dir: 1 | -1 } = { key: "rank", dir: 1 };
  private selected = "";

  // — Banner + nav (built once) —
  private readonly crest = svgNode("text", { x: 28, y: 40, "text-anchor": "middle" });
  private readonly nameEl = h("h2");
  private readonly levelEl = h("b");
  private readonly rankEl = h("b");
  private readonly expText = h("span");
  private readonly expFill = h("i");
  private readonly statMembers = h("b");
  private readonly statOnline = h("b");
  private readonly statFund = h("b");
  private readonly statPoints = h("b");
  private readonly navBtns = new Map<Page, { el: HTMLButtonElement; count: HTMLElement }>();
  private readonly youName = h("b");
  private readonly youRights = h("div");
  private readonly body = h("div", "page");
  private readonly mainView = h("div", "gmain");
  private readonly noGuild = h("div", "nog", svg("guild"), h("b", "", "You are not in a guild."),
    h("div", "", "Join one when a guild member invites you, or found your own at a guild NPC."));
  private readonly banner: HTMLElement;

  // — Overview —
  private readonly kpi = { online: h("b"), onlineSub: h("span"), onlineNote: h("small"), level: h("b"), levelNote: h("small"), fund: h("b"), buffs: h("b"), buffsNote: h("small") };
  private readonly noticeHead = h("div", "ch", "Notice board", h("span", "sp"));
  private readonly noticeView = h("div", "notice");
  private readonly noticeArea = typingField(h("textarea", "fld"));
  private noticeBtn!: Btn;
  private noticeCancel!: Btn;
  private readonly onlineCard = h("div", "card");
  private readonly recentFeed = h("div", "feed");

  // — Members —
  private readonly memberBody = h("tbody");
  private readonly memberHead = h("tr");
  private readonly memberCard = h("div", "card mcard");
  private readonly memberNote = h("div", "note");
  private memberSearch: ReturnType<typeof searchBox> | null = null;
  private inviteBtn!: Btn;
  private detailsBtn!: Btn;

  // — Ranks —
  private readonly rankBody = h("tbody");
  private newRankBtn!: Btn;
  private readonly rankNote = h("div", "note");

  // — Buffs —
  private readonly buffHead = h("div", "tools");
  private readonly buffGrid = h("div", "bgrid");
  private readonly buffTimers: { el: HTMLElement; buff: GuildBuff }[] = [];
  private readonly buffNote = h("div", "note");

  // — Storage —
  private readonly storeCells: ItemCell[] = [];
  private readonly storeUsed = h("span");
  private readonly fundText = h("b");
  private depositBtn!: Btn;
  private withdrawBtn!: Btn;
  private readonly storeNote = h("div", "note");

  // — Wars / Log —
  private readonly warList = h("div");
  private readonly warHead = h("div", "ch");
  private readonly warTimers: { el: HTMLElement; endsAt: number }[] = [];
  private logFilter: LogFilter = "All";
  private readonly logFeed = h("div", "feed");
  private readonly logNote = h("div", "note");

  constructor(private readonly ui: ModernUiHost, private readonly clock: GuildBuffClock) {
    super(ui.rt, "gdwin", 52, 40);
    injectCss("win-guild", CSS);
    this.m = ui.ctx.models;
    this.g = this.m.guild;
    const sig = this.signal;

    // Banner: crest (initials), name, level, EXP, stats
    const emblem = svgNode("svg", { viewBox: "0 0 56 64", class: "emblem", "aria-hidden": "true" });
    emblem.append(svgNode("path", { d: "M28 2l24 8v19c0 16-10 27-24 33C14 56 4 45 4 29V10z" }), this.crest);
    const bstat = (label: string, el: HTMLElement, hot = false) => h("div", "bstat" + (hot ? " hot" : ""), el, h("span", "", label));
    this.banner = h("div", "banner", emblem,
      h("div", "gname", this.nameEl, h("div", "sub", "Level ", this.levelEl, " guild · you are ", this.rankEl),
        h("div", "gexp", h("div", "t", h("span", "", "Guild EXP"), this.expText), h("div", "bar", this.expFill))),
      h("div", "bstats", bstat("Members", this.statMembers), bstat("Online", this.statOnline), bstat("Fund", this.statFund), bstat("Buff points", this.statPoints, true)));

    // Side menu
    const nav = h("div", "nav");
    for (const [p, icon] of PAGES) {
      const count = h("i");
      count.hidden = true;
      const el = h("button", "", svg(icon), h("span", "", p === "Wars" ? "Wars" : p), count);
      el.type = "button";
      el.title = p;
      el.addEventListener("click", () => this.go(p, true), { signal: sig });
      nav.append(el);
      this.navBtns.set(p, { el, count });
    }
    const leave = button("Leave guild", () => void this.leave(), { icon: "logout", title: "Leave the guild (@LEAVEGUILD; not while at war)" });
    nav.append(h("div", "sp"), h("div", "you", "You are", this.youName, this.youRights, leave.el));
    this.mainView.append(nav, this.body);
    this.el.append(h("div", "panel", this.titleBar("GUILD", "Close (G)"), this.banner, this.mainView, this.noGuild));

    // Model events: mark the regions, render when shown (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.g.view.on((v) => {
      this.rosterDirty = this.ranksDirty = true;
      if (v.kind === "notice" || v.kind === "status") {
        this.noticeDirty = true;
        this.editing = this.noticeSaving = false; // NoticeChange / StatusChanged leave edit mode
      }
      if (v.kind === "ranks" || v.kind === "rank" || v.kind === "members") { this.pendingMember = ""; this.pendingRank = -1; }
      if (v.kind === "status" && v.left) this.toggle(false); // GameScene.GuildStatus: no guild hides the dialog
    }));
    on(this.g.changed.on(() => {
      this.buffsDirty = true; this.pendingBuff = -1;
      this.invalidate();
      this.askMembers(); // a join / leave inside the open window asks again (the model throttles)
    }));
    on(this.g.memberDetailsChanged.on(() => { this.rosterDirty = true; this.invalidate(); }));
    on(this.g.warsChanged.on(() => { this.warsDirty = true; this.invalidate(); }));
    on(this.g.logChanged.on(() => { this.logDirty = true; this.invalidate(); }));
    on(this.m.inventory.subscribe(() => { if (this.page === "Storage") this.invalidate(); }));
    // Countdowns (active buffs, wars) only while shown, and only the shown page's timers
    this.everyShown(1000, () => this.tick());
  }

  protected override shown(): void {
    if (!this.g.name) return;
    if (!this.visible(this.page)) this.page = "Overview";
    this.enter(this.page);
    this.g.requestMemberDetails();
  }

  protected override closed(): void {
    this.editing = false;
    this.noticeSaving = false;
    const pick = this.m.itemPick;
    if (pick.cell?.grid === "guild") pick.clear();
  }

  // — Navigation —

  /** Which pages the user's rank may see (GuildDialog.StatusChanged / RefreshInterface). */
  private visible(p: Page): boolean {
    switch (p) {
      case "Ranks": return this.g.can(Opt.CanChangeRank);
      case "Storage": return this.g.can(Opt.CanStoreItem) || this.g.can(Opt.CanRetrieveItem);
      case "Buffs": return this.g.buffInfos.length > 0;
      default: return true;
    }
  }

  private go(p: Page, fromMenu = false): void {
    if (p === this.page) return;
    this.page = p;
    this.editing = false;
    this.focusSearch = fromMenu && p === "Members";
    this.enter(p);
    this.invalidate();
  }

  /** A page was opened: the requests its desktop page sends (LeftDialog / RightDialog). */
  private enter(p: Page): void {
    if (p === "Overview") { this.g.requestNotice(); this.g.requestLog(); }
    if (p === "Storage") this.ui.ctx.send(ClientPackets.GuildStorageItemChange({ Type: 3, From: 0, To: 0 }));
    if (p === "Log") this.g.requestLog();
    if (p === "Members") this.g.requestMemberDetails();
    this.askMembers();
  }

  /** RequestUpdateMembers while a page that shows the roster is open (5 s throttle + retry in the model). */
  private askMembers(): void {
    if (!this.open) return;
    this.g.requestMembers(() => this.open && (this.page === "Overview" || this.page === "Members" || this.page === "Ranks"));
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const inGuild = this.g.name !== "";
    this.banner.hidden = this.mainView.hidden = !inGuild;
    this.noGuild.hidden = inGuild;
    if (!inGuild) return;
    if (!this.visible(this.page)) { this.page = "Overview"; this.enter("Overview"); }
    this.renderBanner();
    this.renderNav();
    if (this.pageShown !== this.page) {
      this.pageShown = this.page;
      this.body.replaceChildren(this.pageEl(this.page));
      this.body.scrollTop = 0;
    }
    switch (this.page) {
      case "Overview": this.renderOverview(); break;
      case "Members": this.renderMembers(); break;
      case "Ranks": this.renderRanks(); break;
      case "Buffs": this.renderBuffs(); break;
      case "Storage": this.renderStorage(); break;
      case "Wars": this.renderWars(); break;
      case "Log": this.renderLog(); break;
    }
  }

  private renderBanner(): void {
    const g = this.g;
    setText(this.nameEl, g.name);
    setText(this.crest, initials(g.name));
    setText(this.levelEl, String(g.level));
    setText(this.rankEl, g.rankName || "a member");
    if (g.maxExperience > 0n) {
      setText(this.expText, `${fmt(g.experience)} / ${fmt(g.maxExperience)} (${pct(g.experience, g.maxExperience)})`);
      setStyle(this.expFill, "width", `${Math.min(100, Number(g.experience) / Number(g.maxExperience) * 100).toFixed(1)}%`);
    } else {
      setText(this.expText, "max level");
      setStyle(this.expFill, "width", "100%");
    }
    const roster = this.roster();
    setText(this.statMembers, g.maxMembers ? `${g.memberCount} / ${g.maxMembers}` : String(g.memberCount));
    setText(this.statOnline, roster.length ? String(roster.filter((r) => r.online).length) : "—");
    setText(this.statFund, fmtCompact(g.gold));
    setText(this.statPoints, String(g.sparePoints));
    setText(this.youName, g.rankName || "Member");
    const rights = OPTIONS.filter(([bit]) => g.can(bit)).length;
    setText(this.youRights, rights === OPTIONS.length ? "All permissions" : rights ? plural(rights, "permission") : "No permissions");
  }

  private renderNav(): void {
    const roster = this.roster();
    const activeBuffs = this.g.enabledBuffs.filter((b) => b.Active).length;
    for (const [p, b] of this.navBtns) {
      b.el.hidden = !this.visible(p);
      b.el.classList.toggle("on", p === this.page);
      const n = p === "Members" ? roster.length : p === "Buffs" ? activeBuffs : p === "Wars" ? this.g.wars.length : 0;
      b.count.hidden = n <= 0;
      setText(b.count, String(n));
    }
  }

  /** The page's element (built on its first visit). */
  private pageEl(p: Page): HTMLElement {
    let el = this.pages.get(p);
    if (el) return el;
    el = h("div");
    this.pages.set(p, el);
    switch (p) {
      case "Overview": this.buildOverview(el); break;
      case "Members": this.buildMembers(el); break;
      case "Ranks": this.buildRanks(el); break;
      case "Buffs": this.buildBuffs(el); break;
      case "Storage": this.buildStorage(el); break;
      case "Wars": this.buildWars(el); break;
      case "Log": this.buildLog(el); break;
    }
    this.built.add(p);
    return el;
  }

  // — Roster —

  /** GuildState.ranks members (live online flags) with S.GuildMemberDetails merged in by name; the details alone while
   *  the rank list has not arrived. Rebuilt only after a guild / details event. */
  private roster(): Row[] {
    if (!this.rosterDirty) return this.rosterCache;
    this.rosterDirty = false;
    const g = this.g, details = new Map(g.memberDetails.map((d) => [d.Name, d]));
    const rows: Row[] = [];
    if (g.ranks.length) {
      for (const r of g.ranks) for (const m of r.Members) {
        const d = details.get(m.Name);
        rows.push({ name: m.Name, rank: r.Index, rankName: r.Name, online: m.Online, lastLogin: m.LastLogin, cls: d ? d.Class : null, level: d ? d.Level : null });
      }
    } else {
      for (const d of g.memberDetails) {
        rows.push({ name: d.Name, rank: d.RankIndex, rankName: g.ranks[d.RankIndex]?.Name ?? "", online: d.Online, lastLogin: d.LastLogin, cls: d.Class, level: d.Level });
      }
    }
    this.rosterCache = rows;
    return rows;
  }

  private me(): string { return this.ui.ctx.player.user?.name ?? ""; }

  private seen(r: Row): string {
    if (r.online) return "Online";
    const ms = r.lastLogin !== 0n ? dateTimeBinaryToMs(r.lastLogin) : 0;
    return ms > 0 ? fmtAgo(ms) : "—";
  }

  private rankTag(r: { rank: number; rankName: string }): HTMLElement {
    return h("span", "tag" + (r.rank === 0 ? " lead" : ""), r.rankName || `Rank ${r.rank + 1}`);
  }

  // — Overview —

  private buildOverview(el: HTMLElement): void {
    const k = this.kpi;
    const card = (b: HTMLElement, label: Node | string, note: HTMLElement | null) => h("div", "card kpi", b, h("span", "", label), note);
    this.noticeBtn = button("Edit", () => this.noticeAction(), { title: "Edit the notice (your rank may change it)" });
    this.noticeCancel = button("Cancel", () => { this.editing = false; this.noticeDirty = true; this.invalidate(); });
    this.noticeHead.append(this.noticeCancel.el, this.noticeBtn.el);
    this.noticeArea.rows = 12;
    const feedCard = h("div", "card", h("div", "ch", "Recent activity"), this.recentFeed);
    el.append(h("div", "cards4", card(k.online, k.onlineSub, k.onlineNote), card(k.level, "guild level", k.levelNote),
      card(k.fund, "gold in the fund", null), card(k.buffs, "buffs active", k.buffsNote)),
      h("div", "grid2", h("div", "card", this.noticeHead, this.noticeView, this.noticeArea), this.onlineCard), feedCard);
  }

  private renderOverview(): void {
    const g = this.g, k = this.kpi, roster = this.roster();
    const online = roster.filter((r) => r.online);
    setText(k.online, roster.length ? String(online.length) : "—");
    setText(k.onlineSub, `members online of ${g.memberCount}`);
    setText(k.onlineNote, roster.length ? `${Math.round(online.length / Math.max(1, roster.length) * 100)}% active now` : "loading the roster…");
    setText(k.level, String(g.level));
    setText(k.levelNote, g.maxExperience > 0n && g.maxExperience > g.experience ? `${fmt(g.maxExperience - g.experience)} EXP to level ${g.level + 1}` : "");
    setText(k.fund, fmtCompact(g.gold));
    setText(k.buffs, String(g.enabledBuffs.filter((b) => b.Active).length));
    setText(k.buffsNote, plural(g.sparePoints, "buff point") + " to spend");

    // Notice (view / edit / saving)
    const canEdit = g.can(Opt.CanChangeNotice);
    if (!canEdit) this.editing = false;
    this.noticeBtn.el.hidden = !canEdit;
    this.noticeCancel.el.hidden = !this.editing;
    this.noticeBtn.setLabel(this.noticeSaving ? "Saving…" : this.editing ? "Save" : "Edit");
    this.noticeBtn.setDisabled(this.noticeSaving, "Waiting for the server");
    this.noticeArea.hidden = !this.editing && !this.noticeSaving;
    this.noticeArea.readOnly = this.noticeSaving;
    this.noticeView.hidden = !this.noticeArea.hidden;
    if (this.noticeDirty && !this.editing && !this.noticeSaving) {
      this.noticeDirty = false;
      setText(this.noticeView, g.notice.length && g.notice.some((l) => l.trim()) ? g.notice.join("\n") : "No notice yet.");
    }

    // Who is online (up to 9)
    const kids: (Node | string)[] = [h("div", "ch", `Online now · ${online.length}`)];
    for (const r of online.slice(0, 9)) {
      kids.push(h("div", "who", h("span", "dot on"), r.name, this.rankTag(r),
        h("span", "m", r.level !== null && r.cls !== null ? `${className(r.cls)} ${r.level}` : "")));
    }
    if (online.length > 9) kids.push(h("div", "who", h("span", "m", `and ${online.length - 9} more`)));
    if (!online.length) kids.push(h("div", "empty-note", roster.length ? "Nobody else is online." : "Loading the roster…"));
    this.onlineCard.replaceChildren(...kids);

    if (this.logDirty || this.recentFeed.childElementCount === 0) {
      this.fillFeed(this.recentFeed, this.g.log.slice(-5).reverse(), "No activity recorded yet.");
    }
  }

  private noticeAction(): void {
    const g = this.g;
    if (!g.can(Opt.CanChangeNotice) || this.noticeSaving) return;
    if (!this.editing) {
      this.editing = true;
      this.noticeArea.value = g.notice.join("\n");
      this.invalidate();
      later(() => { if (this.open) this.noticeArea.focus({ preventScroll: true }); }, 0, this.signal);
      return;
    }
    const lines = this.noticeArea.value.split(/\r?\n/);
    if (lines.length > 200) { this.ui.chat.system("The notice can have at most 200 lines."); return; }
    // C.EditGuildNotice; the box stays read-only until S.GuildNoticeChange (or a timeout) answers
    this.ui.ctx.send(ClientPackets.EditGuildNotice({ notice: lines }));
    this.editing = false;
    this.noticeSaving = true;
    later(() => { if (this.noticeSaving) { this.noticeSaving = false; this.noticeDirty = true; this.invalidate(); } }, PENDING_MS, this.signal);
    this.invalidate();
  }

  // — Members —

  private buildMembers(el: HTMLElement): void {
    const sig = this.signal;
    this.memberSearch = searchBox("Search members, classes, ranks…", (q) => { this.query = q.toLowerCase(); this.renderMemberRows(); }, { signal: sig });
    const filter = chips([{ value: "all", label: "All" }, { value: "online", label: "Online" }], ["all"], (s) => {
      this.onlineOnly = s.has("online");
      this.renderMemberRows();
    }, { multi: false, allowNone: false });
    this.inviteBtn = button("Invite", () => void this.invite(), { icon: "plus", cls: "main", title: "Invite a player to the guild (they must be online)" });
    this.detailsBtn = button("Refresh", () => {
      if (!this.g.requestMemberDetails()) this.say(this.memberNote, "Updated moments ago: try again in a few seconds.");
    }, { icon: "refresh", title: "Ask the server for class, level and last login (every 5 s at most)" });
    const cols: [SortKey, string][] = [["name", "Name"], ["cls", "Class"], ["level", "Level"], ["rank", "Rank"], ["seen", "Last seen"]];
    for (const [key, label] of cols) {
      const th = h("th", "sort", label);
      th.dataset.key = key;
      th.addEventListener("click", () => {
        this.sort = { key, dir: this.sort.key === key ? (-this.sort.dir as 1 | -1) : 1 };
        this.renderMemberRows();
      }, { signal: sig });
      this.memberHead.append(th);
    }
    this.memberBody.addEventListener("click", (e) => {
      const tr = (e.target as HTMLElement).closest("tr");
      const name = tr?.dataset.name;
      if (!name || name === this.selected) return;
      this.selected = name;
      this.renderMemberRows();
    }, { signal: sig });
    el.append(h("div", "tools", this.memberSearch.el, filter.el, h("span", "sp"), this.detailsBtn.el, this.inviteBtn.el),
      h("div", "mgrid", h("div", "tblwrap", h("table", "mt", h("thead", "", this.memberHead), this.memberBody)), this.memberCard), this.memberNote);
  }

  private renderMembers(): void {
    const recruit = this.g.can(Opt.CanRecruit);
    this.inviteBtn.el.hidden = !recruit;
    this.inviteBtn.setDisabled(this.throttled(this.lastMemberMsg), "Wait a moment before the next guild change");
    this.renderMemberRows();
    if (this.focusSearch) {
      this.focusSearch = false;
      later(() => { if (this.open && this.page === "Members") this.memberSearch?.focus(); }, 0, this.signal);
    }
  }

  /** The table (filter / sort in place) and the selected member's card. */
  private renderMemberRows(): void {
    const roster = this.roster(), q = this.query;
    const rows = roster.filter((r) => (!this.onlineOnly || r.online)
      && (!q || r.name.toLowerCase().includes(q) || r.rankName.toLowerCase().includes(q) || (r.cls !== null && className(r.cls).toLowerCase().includes(q))));
    const { key, dir } = this.sort;
    const val = (r: Row): number | string => key === "name" ? r.name.toLowerCase() : key === "cls" ? r.cls ?? 99 : key === "level" ? r.level ?? -1
      : key === "rank" ? r.rank : r.online ? Number.MAX_SAFE_INTEGER : r.lastLogin === 0n ? 0 : dateTimeBinaryToMs(r.lastLogin);
    rows.sort((a, b) => {
      const va = val(a), vb = val(b);
      const c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
      return (c || a.name.localeCompare(b.name)) * dir;
    });
    for (const th of this.memberHead.children) {
      const k = (th as HTMLElement).dataset.key;
      th.classList.toggle("on", k === key);
      th.setAttribute("aria-sort", k === key ? (dir === 1 ? "ascending" : "descending") : "none");
    }
    const kids: HTMLElement[] = [];
    for (const r of rows) {
      const tr = h("tr", r.name === this.selected ? "sel" : "",
        h("td", "", h("span", "nmc", h("span", "dot" + (r.online ? " on" : "")), r.name)),
        h("td", "dim", r.cls !== null ? className(r.cls) : "…"),
        h("td", "", r.level !== null ? String(r.level) : "…"),
        h("td", "", this.rankTag(r)),
        h("td", "dim", this.seen(r)));
      if (r.cls !== null) (tr.children[1] as HTMLElement).style.color = classVar(r.cls);
      tr.dataset.name = r.name;
      kids.push(tr);
    }
    if (!kids.length) {
      const td = h("td", "dim", roster.length ? "No members match." : "Loading the roster…");
      td.colSpan = 5;
      kids.push(h("tr", "", td));
    }
    this.memberBody.replaceChildren(...kids);
    this.renderMemberCard(roster);
  }

  private renderMemberCard(roster: Row[]): void {
    const me = this.me();
    const r = roster.find((x) => x.name === this.selected) ?? roster.find((x) => x.name === me) ?? roster[0];
    if (!r) { this.memberCard.replaceChildren(h("div", "empty-note", "Pick a member.")); return; }
    this.selected = r.name;
    const g = this.g, self = r.name === me;
    const ln = (k: string, v: string) => h("div", "ln", h("span", "", k), h("b", "", v));
    const busy = this.pendingMember === r.name;
    const wait = this.throttled(this.lastMemberMsg);
    const editable = !self && g.can(Opt.CanChangeRank) && r.rank >= g.myRankId && g.ranks.length > 0;
    const why = busy ? "Waiting for the server" : wait ? "Wait a moment before the next guild change" : "";
    const acts: HTMLElement[] = [];
    if (!self) {
      const whisper = button("Whisper", () => this.ui.chat.prefill(`/${r.name} `), { icon: "chat", title: "Whisper (/name in the chat box)" });
      const groupBtn = button("Group", () => { this.m.group.addMember(r.name); this.say(this.memberNote, `Group invite sent to ${r.name}.`); }, { icon: "group", title: "Invite to your group" });
      if (!r.online) { whisper.setDisabled(true, `${r.name} is offline`); groupBtn.setDisabled(true, `${r.name} is offline`); }
      else if (this.m.group.has(r.name)) groupBtn.setDisabled(true, `${r.name} is in your group`);
      acts.push(whisper.el, groupBtn.el);
    }
    if (editable) {
      const up = r.rank - 1, down = r.rank + 1;
      const promote = button("Promote", () => void this.changeRank(r.name, up), { icon: "plus" });
      const demote = button("Demote", () => void this.changeRank(r.name, down), { icon: "minus" });
      if (why) { promote.setDisabled(true, why); demote.setDisabled(true, why); }
      else {
        if (up < g.myRankId || up < 0) promote.setDisabled(true, "Members can only be moved to ranks at or below your own");
        else promote.el.title = `Move to ${g.ranks[up]?.Name ?? "the rank above"}`;
        if (down >= g.ranks.length) demote.setDisabled(true, "Already in the lowest rank (add a rank on the Ranks page)");
        else demote.el.title = `Move to ${g.ranks[down]?.Name ?? "the rank below"}`;
      }
      const sel = h("select", "fld");
      sel.title = "Set rank";
      for (const rank of g.ranks) {
        if (rank.Index < g.myRankId) continue;
        const o = h("option", "", rank.Name);
        o.value = String(rank.Index);
        sel.append(o);
      }
      sel.value = String(r.rank);
      sel.disabled = !!why;
      sel.addEventListener("change", () => {
        const to = Number(sel.value);
        sel.value = String(r.rank); // the model's rank until the server answers
        void this.changeRank(r.name, to);
      });
      acts.push(promote.el, demote.el, sel);
    }
    if (self) acts.push(button("Leave guild", () => void this.leave(), { cls: "warn", icon: "logout", title: "Leave the guild (not while at war)" }).el);
    if (!self && g.can(Opt.CanKick) && r.rank >= g.myRankId) {
      const kick = button("Kick", () => void this.kick(r.name), { cls: "warn", icon: "cross" });
      if (why) kick.setDisabled(true, why);
      acts.push(kick.el);
    }
    this.memberCard.replaceChildren(
      h("h3", "", h("span", "dot" + (r.online ? " on" : "")), r.name),
      h("div", "", this.rankTag(r)),
      h("div", "", ln("Class", r.cls !== null ? className(r.cls) : "…"), ln("Level", r.level !== null ? String(r.level) : "…"),
        ln(r.online ? "Status" : "Last seen", this.seen(r))),
      h("div", "acts", ...acts));
  }

  /** GuildDialog.OnNewRank: Yes / No, then C.EditGuildMember 2 (the server moves the member and answers). */
  private async changeRank(name: string, to: number): Promise<void> {
    const g = this.g, rank = g.ranks[to];
    if (!rank || to < g.myRankId || this.throttled(this.lastMemberMsg) || this.pendingMember) return;
    if (!await this.ui.rt.popups.ask(`Are you sure you want to change the rank of ${name} to ${rank.Name}?`, { title: "Change rank", kind: "question" })) return;
    if (!this.g.name || this.throttled(this.lastMemberMsg)) return;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 2, RankIndex: rank.Index, Name: name, RankName: rank.Name }));
    this.memberSent(name);
  }

  /** GuildDialog.DeleteMember: Yes / No, then C.EditGuildMember 1. */
  private async kick(name: string): Promise<void> {
    if (name === this.me() || this.throttled(this.lastMemberMsg) || this.pendingMember) return;
    if (!await this.ui.rt.popups.ask(`Are you sure you want to kick ${name}?`, { title: "Kick member", kind: "danger" })) return;
    if (!this.g.name || this.throttled(this.lastMemberMsg)) return;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 1, RankIndex: 0, Name: name, RankName: "" }));
    this.memberSent(name);
  }

  /** GuildDialog.AddMember: the name, then C.EditGuildMember 0 (the server sends the player S.GuildInvite). */
  private async invite(): Promise<void> {
    if (!this.g.can(Opt.CanRecruit) || this.throttled(this.lastMemberMsg)) return;
    const name = await this.ui.rt.popups.input({ title: "Invite to the guild", text: "Who would you like to invite? They must be online and not in a guild.", min: 1, max: 20 });
    const n = name?.trim() ?? "";
    if (!n || !this.g.name || this.throttled(this.lastMemberMsg)) return;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 0, RankIndex: 0, Name: n, RankName: "" }));
    this.lastMemberMsg = this.ui.ctx.now() + MEMBER_MSG_GAP;
    this.say(this.memberNote, `Invitation sent to ${n}.`);
    this.unlockLater();
  }

  private memberSent(name: string): void {
    this.lastMemberMsg = this.ui.ctx.now() + MEMBER_MSG_GAP;
    this.pendingMember = name;
    this.unlockLater();
    this.invalidate();
  }

  /** Re-renders when the throttle / a pending request runs out (no answer: the control unlocks). */
  private unlockLater(): void {
    later(() => { this.pendingMember = ""; this.pendingRank = -1; this.pendingBuff = -1; this.invalidate(); }, PENDING_MS + 50, this.signal);
  }

  /** "@LEAVEGUILD" after a warning (the server refuses at war, and the last leader must be the last member). */
  private async leave(): Promise<void> {
    const g = this.g;
    if (!g.name) return;
    const ok = await this.ui.rt.popups.ask(`Leave ${g.name}?${g.myRankId === 0 ? "\nAs a leader you can leave only while another leader remains, or when you are the last member." : ""}`,
      { title: "Leave guild", kind: "danger", hold: 900, labels: { ok: "Leave" }, okCancel: true });
    if (ok && this.g.name) this.ui.ctx.send(ClientPackets.Chat({ Message: "@LEAVEGUILD" }));
  }

  // — Ranks —

  private buildRanks(el: HTMLElement): void {
    this.newRankBtn = button("New rank", () => void this.newRank(), { icon: "plus", cls: "main", title: "Add a rank under the existing ones" });
    const head = h("tr", "", h("th", "", "Rank"));
    for (const [, label, title] of OPTIONS) { const th = h("th", "", label); th.title = title; head.append(th); }
    head.append(h("th", "", ""));
    el.append(h("div", "card", h("div", "ch", "Ranks and permissions", h("span", "sp"), this.newRankBtn.el),
      h("div", "tblwrap", h("table", "perm", h("thead", "", head), this.rankBody)), this.rankNote,
      h("div", "hint", "Ranks above your own are read-only. Members can be moved only to ranks at or below yours. Changes apply when the server confirms them.")));
  }

  private renderRanks(): void {
    const g = this.g;
    this.newRankBtn.setDisabled(g.ranks.length >= MAX_RANKS || this.throttled(this.lastMemberMsg),
      g.ranks.length >= MAX_RANKS ? "No more rank slots available" : "Wait a moment before the next guild change");
    if (!this.ranksDirty && this.rankBody.childElementCount) return;
    this.ranksDirty = false;
    if (!g.ranks.length) { const td = h("td", "dim", "Loading the ranks…"); td.colSpan = OPTIONS.length + 2; this.rankBody.replaceChildren(h("tr", "", td)); return; }
    const rows: HTMLElement[] = [];
    for (const rank of g.ranks) {
      const editable = rank.Index >= g.myRankId;
      const tr = h("tr", "", h("td", "", h("b", "", rank.Name), h("small", "", String(rank.Members.length))));
      for (const [bit, label] of OPTIONS) {
        const on = (rank.Options & bit) !== 0;
        const t = toggleSwitch(on, () => this.toggleOption(rank, bit, t), { small: true, label: `${label} for ${rank.Name}` });
        if (!editable) t.lock(true, "Above your own rank");
        else if (this.pendingRank === rank.Index) t.lock(true, "Waiting for the server");
        tr.append(h("td", "", t.el));
      }
      const rename = button("Rename", () => void this.renameRank(rank), { title: "Rename this rank" });
      if (!editable) rename.setDisabled(true, "Above your own rank");
      tr.append(h("td", "", rename.el));
      rows.push(tr);
    }
    this.rankBody.replaceChildren(...rows);
  }

  /** GuildDialog.SwitchRankOption (ChangeType 5): "true" sets the bit, "false" clears it; the switch keeps the model's
   *  value until S.GuildMemberChange 7 answers. */
  private toggleOption(rank: GuildRank, bit: number, t: ReturnType<typeof toggleSwitch>): void {
    const on = (rank.Options & bit) !== 0;
    t.set(on);
    const now = this.ui.ctx.now();
    if (rank.Index < this.g.myRankId || this.pendingRank >= 0 || now < this.lastOption) return;
    this.lastOption = now + OPTION_GAP;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 5, RankIndex: rank.Index, Name: on ? "false" : "true", RankName: String(Math.log2(bit)) }));
    this.pendingRank = rank.Index;
    t.lock(true, "Waiting for the server");
    this.unlockLater();
  }

  /** GuildDialog.RanksChangeName (ChangeType 3): 3-20 characters, no backslash, 5 s apart. */
  private async renameRank(rank: GuildRank): Promise<void> {
    if (rank.Index < this.g.myRankId) return;
    if (this.throttled(this.lastRankName)) { this.say(this.rankNote, "Wait a few seconds before renaming again."); return; }
    const name = await this.ui.rt.popups.input({
      title: "Rename rank", text: `A new name for ${rank.Name} (3 to 20 characters).`, value: rank.Name, min: 3, max: 20,
      check: (v) => v.includes("\\") ? "No backslashes" : v.trim().length < 3 ? "At least 3 characters" : "",
    });
    if (name === null || !this.g.name || this.throttled(this.lastRankName)) return;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 3, RankIndex: rank.Index, Name: "", RankName: name.trim() }));
    this.lastRankName = this.ui.ctx.now() + RANK_NAME_GAP;
    this.say(this.rankNote, "Renaming…");
  }

  /** GuildDialog.OnRankSelect "Add New" (ChangeType 4 with "Rank-N"). */
  private async newRank(): Promise<void> {
    const g = this.g;
    if (g.ranks.length >= MAX_RANKS || this.throttled(this.lastMemberMsg)) return;
    if (!await this.ui.rt.popups.ask("Are you sure you want to create a new rank?", { title: "New rank", kind: "question" })) return;
    if (!this.g.name || this.throttled(this.lastMemberMsg)) return;
    this.ui.ctx.send(ClientPackets.EditGuildMember({ ChangeType: 4, RankIndex: 0, Name: "", RankName: `Rank-${g.ranks.length - 1}` }));
    this.lastMemberMsg = this.ui.ctx.now() + MEMBER_MSG_GAP;
    this.say(this.rankNote, "Adding a rank…");
    this.unlockLater();
  }

  // — Buffs —

  private buildBuffs(el: HTMLElement): void {
    el.append(this.buffHead, this.buffGrid, this.buffNote);
  }

  private renderBuffs(): void {
    const g = this.g;
    this.buffHead.replaceChildren(h("span", "", `Guild level ${g.level} · `, h("b", "", plural(g.sparePoints, "point")), " to spend · fund ", h("b", "gold", fmt(g.gold)), " gold"));
    if (!this.buffsDirty && this.buffGrid.childElementCount) { this.tick(); return; }
    this.buffsDirty = false;
    this.buffTimers.length = 0;
    const rows: HTMLElement[] = [];
    for (const info of g.buffInfos) rows.push(this.buffRow(info));
    if (!rows.length) rows.push(h("div", "empty-note", "This server has no guild buffs."));
    this.buffGrid.replaceChildren(...rows);
    this.tick();
  }

  /** One GuildBuffButton: icon (GuildDialog's GuildSkill rule), name, real stats, requirements, state and action. */
  private buffRow(info: GuildBuffInfo): HTMLElement {
    const g = this.g, buff = g.findBuff(info.Id);
    const active = !!buff?.Active;
    const pic = h("i");
    const idx = !buff ? info.Icon + 2 : active ? info.Icon + 1 : info.Icon;
    void libPicture(Lib.GuildSkill, idx).then((p) => { if (p) pic.style.backgroundImage = `url(${p.url})`; });
    const fx: string[] = [];
    for (const [k, v] of Object.entries(info.Stats)) {
      if (!v) continue;
      const stat = StatName[Number(k)] ?? `Stat${k}`;
      fx.push(`${pretty(stat)} ${signed(v)}${stat.includes("Percent") ? "%" : ""}`);
    }
    const req: string[] = [];
    if (info.LevelRequirement > 0) req.push(`guild level ${info.LevelRequirement}`);
    if (info.PointsRequirement > 0) req.push(plural(info.PointsRequirement, "point"));
    if (info.ActivationCost > 0) req.push(`${fmt(info.ActivationCost)} gold to activate`);
    req.push(info.TimeLimit > 0 ? `lasts ${plural(info.TimeLimit, "minute")}` : "permanent");
    const st = h("div", "st");
    let btn: Btn | null = null;
    if (!buff) {
      const low = info.LevelRequirement > g.level;
      st.append(h("b", "", low ? "Locked" : "—"), low ? "level too low" : "not obtained");
      btn = button("Obtain", () => this.requestBuff(info), { title: `Spend ${plural(info.PointsRequirement, "point")}` });
      const why = !g.can(Opt.CanActivateBuff) ? "Guild rank does not allow buff activation." : g.sparePoints < info.PointsRequirement ? "Insufficient points available."
        : low ? "Guild level too low." : "";
      if (why) btn.setDisabled(true, why);
    } else if (active) {
      const b = h("b", "", info.TimeLimit > 0 ? "" : "Active");
      st.append(b, info.TimeLimit > 0 ? "remaining" : "obtained");
      if (info.TimeLimit > 0) this.buffTimers.push({ el: b, buff });
    } else {
      st.append(h("b", "", "Ready"), "obtained");
      btn = button("Activate", () => this.requestBuff(info), { cls: "main", title: info.ActivationCost ? `Costs ${fmt(info.ActivationCost)} gold from the guild fund` : "" });
      const why = !g.can(Opt.CanActivateBuff) ? "Guild rank does not allow buff activation." : g.gold < info.ActivationCost ? "Insufficient guild funds." : "";
      if (why) btn.setDisabled(true, why);
    }
    if (btn && this.pendingBuff === info.Id) btn.setDisabled(true, "Waiting for the server");
    return h("div", "gbuff" + (active ? " act" : ""), pic,
      h("div", "mid", h("b", "", info.Name), fx.length ? h("span", "fx", fx.join(" · ")) : null, h("span", "", req.join(" · "))), st, btn?.el);
  }

  /** GuildDialog.RequestBuff: C.GuildBuffUpdate 1 (obtain) or 2 (activate); the row waits for S.GuildBuffList. */
  private requestBuff(info: GuildBuffInfo): void {
    const now = this.ui.ctx.now();
    if (this.pendingBuff >= 0 || now < this.lastBuff + BUFF_GAP) return;
    this.lastBuff = now;
    const buff = this.g.findBuff(info.Id);
    this.ui.ctx.send(ClientPackets.GuildBuffUpdate({ Action: buff ? 2 : 1, Id: info.Id }));
    this.pendingBuff = info.Id;
    this.buffsDirty = true;
    this.unlockLater();
    this.invalidate();
  }

  // — Storage —

  private buildStorage(el: HTMLElement): void {
    const grid = h("div", "sgrid");
    for (let i = 0; i < GUILD_STORAGE_SLOTS; i++) {
      const c = this.ui.cells.cell("guild", i, { size: 40 });
      this.storeCells.push(c);
      grid.append(c.el);
    }
    this.depositBtn = button("Deposit", () => void this.gold(0), { cls: "main", icon: "coin", title: "Put gold from your bag into the guild fund" });
    this.withdrawBtn = button("Withdraw", () => void this.gold(1), { icon: "coin", title: "Take gold from the guild fund (leaders only)" });
    el.append(h("div", "card", h("div", "ch", "Guild storage", h("span", "sp"), this.storeUsed), grid,
      h("div", "money", this.depositBtn.el, this.withdrawBtn.el, h("div", "fund", "Fund ", this.fundText, " gold")), this.storeNote,
      h("div", "hint", "Drag items between your bag and the storage. The guild storage works only in a safe zone; your rank decides who may store or take items.")));
  }

  private renderStorage(): void {
    const g = this.g, inv = this.m.inventory;
    let used = 0;
    for (let i = 0; i < GUILD_STORAGE_SLOTS; i++) if (inv.guildStorage[i]) used++;
    setText(this.storeUsed, `${used} / ${GUILD_STORAGE_SLOTS}`);
    setText(this.fundText, fmt(g.gold));
    this.withdrawBtn.el.hidden = g.myRankId !== 0; // GuildDialog: StorageGoldRemove only for the leader rank
    this.depositBtn.setDisabled(inv.gold <= 0, "You have no gold");
    this.withdrawBtn.setDisabled(g.gold <= 0, "The fund is empty");
  }

  /** GuildDialog.StorageAddGold / StorageRemoveGold: an amount, then C.GuildStorageGoldChange (gold moves when the server
   *  answers with S.LoseGold / S.GainedGold and S.GuildStorageGoldChange). */
  private async gold(type: 0 | 1): Promise<void> {
    if (this.ui.ctx.now() < this.lastGold) return;
    const max = type === 0 ? this.m.inventory.gold : this.g.gold;
    if (max <= 0 || (type === 1 && this.g.myRankId !== 0)) return;
    const amount = await this.ui.rt.popups.amount({ title: type === 0 ? "Deposit gold" : "Withdraw gold", text: type === 0 ? "Gold to put in the guild fund:" : "Gold to retrieve:",
      max, gold: true, min: 1, value: 0, leftLabel: type === 0 ? "gold left in your bag" : "gold left in the fund" });
    if (!amount || amount <= 0 || !this.g.name) return;
    this.lastGold = this.ui.ctx.now() + GOLD_GAP;
    this.ui.ctx.send(ClientPackets.GuildStorageGoldChange({ Type: type, Amount: amount }));
    this.say(this.storeNote, type === 0 ? `Depositing ${fmt(amount)} gold…` : `Withdrawing ${fmt(amount)} gold…`);
  }

  // — Wars —

  private buildWars(el: HTMLElement): void {
    el.append(h("div", "card", this.warHead, this.warList),
      h("div", "hint", "Wars are declared at a guild NPC. While at war your guild cannot recruit and members cannot leave."));
  }

  private renderWars(): void {
    if (!this.warsDirty && this.warList.childElementCount) { this.tick(); return; }
    this.warsDirty = false;
    const wars = this.g.wars;
    setText(this.warHead, `At war · ${wars.length}`);
    this.warTimers.length = 0;
    const rows: HTMLElement[] = [];
    for (const w of wars) {
      const t = h("span", "timer");
      this.warTimers.push({ el: t, endsAt: w.endsAt });
      rows.push(h("div", "drow", svg("kill"), h("span", "nm", w.guildName), t));
    }
    if (!rows.length) rows.push(h("div", "empty-note", "Your guild is not at war."));
    this.warList.replaceChildren(...rows);
    this.tick();
  }

  // — Log —

  private buildLog(el: HTMLElement): void {
    const filters: LogFilter[] = ["All", "Members", "Notice", "Buffs", "Wars", "Gold"];
    const ch = chips(filters.map((f) => ({ value: f, label: f })), [this.logFilter], (s) => {
      this.logFilter = s.values().next().value ?? "All";
      this.logDirty = true;
      this.invalidate();
    }, { multi: false, allowNone: false });
    const refresh = button("Refresh", () => { if (!this.g.requestLog()) this.say(this.logNote, "Updated moments ago: try again in a few seconds."); }, { icon: "refresh" });
    el.append(h("div", "tools", ch.el, h("span", "sp"), refresh.el), h("div", "card", h("div", "ch", "Activity"), this.logFeed), this.logNote);
  }

  private renderLog(): void {
    if (!this.logDirty && this.logFeed.childElementCount) return;
    this.logDirty = false;
    const f = this.logFilter;
    const rows = this.g.log.filter((e) => f === "All" || LOG_FILTER[e.Kind] === f).slice().reverse();
    this.fillFeed(this.logFeed, rows, f === "All" ? "No activity recorded yet." : "Nothing of this kind yet.");
  }

  private fillFeed(el: HTMLElement, rows: readonly { Time: bigint; Kind: number; Text: string }[], empty: string): void {
    const kids: HTMLElement[] = [];
    for (const e of rows) {
      const ms = e.Time !== 0n ? dateTimeBinaryToMs(e.Time) : 0;
      kids.push(h("div", "", svg(LOG_ICON[LOG_FILTER[e.Kind] ?? "All"]), h("span", "tx", e.Text), h("span", "w", ms > 0 ? fmtAgo(ms) : "")));
    }
    if (!kids.length) kids.push(h("div", "empty-note", empty));
    el.replaceChildren(...kids);
  }

  // — Countdowns (everyShown 1 s) —

  private tick(): void {
    if (this.page === "Buffs") for (const t of this.buffTimers) setText(t.el, fmtClock(this.clock.left(t.buff) / 1000));
    if (this.page === "Wars") {
      const now = this.ui.ctx.now();
      for (const t of this.warTimers) setText(t.el, t.endsAt > now ? `ends in ${fmtClock((t.endsAt - now) / 1000)}` : "ending…");
    }
  }

  // — Helpers —

  private throttled(until: number): boolean { return this.ui.ctx.now() < until; }

  private say(el: HTMLElement, text: string): void { setText(el, text); }
}

/** The crest's initials: the first letters of up to two words (or the first two characters). */
function initials(name: string): string {
  const words = name.split(/[\s_-]+/).filter(Boolean);
  const s = words.length > 1 ? words[0]![0]! + words[1]![0]! : name.slice(0, 2);
  return s.toUpperCase();
}

registerWindow(GUILD_WINDOW, (host) => new GuildWindow(host, guildBuffClock(host)), {
  // The buff clock must see GuildBuffList from login on, before the window is first built.
  watch: (host) => { guildBuffClock(host); },
});
