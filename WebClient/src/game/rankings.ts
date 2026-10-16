// Rankings: RankingDialog's per-tab state (RankList, Rank, RankCount) and GameScene.Rankings / RankingDialog.RecieveRanks
// + RequestRanks. RankType 0..5 = overall, Warrior, Wizard, Taoist, Assassin, Archer.
//
// The details cache (GameScene.RankingList, PlayerId -> RankCharacterInfo) lives per Session, not per game: the server
// sends each RankCharacterInfo only once per connection (MirConnection.SentRankings, never cleared at log-out) and
// Listings afterwards carry only ids, while the game scene (and this model) is rebuilt on every log-in over the same
// connection. The desktop gets the same effect from a static dictionary (process lifetime = one connection there).
// rankingDetails(session) hands out that one map; the per-tab lists and the user's rank stay per game (a new character
// must not see the previous one's rank).
//
// Phase 2.5 (web-only): S.RankingsDetail (guild, online, EXP progress per PlayerId; sent right after S.Rankings for that
// page) merges into a second per-Session map, rankingExtras(session). The guild board (C.GetGuildRanking ->
// S.GuildRankings, 20 guilds a page) is a separate list; the desktop's RankType meanings are unchanged.

import { GUILD_RANKINGS_PAGE_SIZE, type ClientGuildRanking, type ClientRankingDetail, type RankCharacterInfo, type ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { Session } from "./session.ts";
import { Emitter } from "./emitter.ts";

/** RankingDialog.RankList.Length (AllButton .. ArchButton). */
export const RANK_TYPES = 6;

const details = new WeakMap<Session, Map<bigint, RankCharacterInfo>>();

/** GameScene.RankingList for this connection (created on first use, dropped with the Session). */
export function rankingDetails(session: Session): Map<bigint, RankCharacterInfo> {
  let map = details.get(session);
  if (!map) { map = new Map(); details.set(session, map); }
  return map;
}

const extras = new WeakMap<Session, Map<bigint, ClientRankingDetail>>();

/** S.RankingsDetail rows for this connection (PlayerId -> guild / online / EXP progress; created on first use). */
export function rankingExtras(session: Session): Map<bigint, ClientRankingDetail> {
  let map = extras.get(session);
  if (!map) { map = new Map(); extras.set(session, map); }
  return map;
}

/** Guild board rows per page (S.GuildRankings). */
export const GUILD_BOARD_PAGE_SIZE = GUILD_RANKINGS_PAGE_SIZE;

export class Rankings {
  /** S.Rankings applied (RankingDialog.RecieveRanks, before the scroll gap is recomputed). */
  readonly changed = new Emitter<void>();
  private readonly lists: RankCharacterInfo[][] = Array.from({ length: RANK_TYPES }, () => []);
  private readonly ranks: number[] = new Array<number>(RANK_TYPES).fill(0);
  private _count = 0;
  /** S.RankingsDetail applied (`rankType` = the page's RankType; the rows are in `extras`). */
  readonly detailsChanged = new Emitter<{ rankType: number }>();
  /** S.GuildRankings applied (`page` = the server-clamped page). */
  readonly guildBoardChanged = new Emitter<{ page: number }>();
  private readonly guildPages = new Map<number, readonly ClientGuildRanking[]>();
  private _guildTotal = 0;
  private readonly extraCache: Map<bigint, ClientRankingDetail>;

  /** @param cache rankingDetails(session). @param extraCache rankingExtras(session) (a private map when omitted). */
  constructor(private readonly send: (f: Uint8Array) => void, private readonly cache: Map<bigint, RankCharacterInfo>,
    extraCache?: Map<bigint, ClientRankingDetail>) {
    this.extraCache = extraCache ?? new Map();
  }

  /** RankingDialog.RankList[type] (the listings in rank order from the requested RankIndex). */
  list(rankType: number): readonly RankCharacterInfo[] { return this.lists[rankType] ?? []; }
  /** RankingDialog.Rank[type]: the user's rank (0 = Not Listed). */
  myRank(rankType: number): number { return this.ranks[rankType] ?? 0; }
  /** RankingDialog.RankCount (the last packet's Count, whatever its type). */
  get count(): number { return this._count; }
  /** GameScene.RankingList (PlayerId -> details), shared by every game on this connection. */
  get details(): ReadonlyMap<bigint, RankCharacterInfo> { return this.cache; }

  /** S.RankingsDetail rows by PlayerId (web; GuildName "" = no guild, ExpPerTenThousand 0..10000), shared per connection. */
  get extras(): ReadonlyMap<bigint, ClientRankingDetail> { return this.extraCache; }
  /** The extras of one listed player (undefined until its S.RankingsDetail arrived). */
  extra(playerId: bigint): ClientRankingDetail | undefined { return this.extraCache.get(playerId); }

  /** Guild board: the ranked guilds (S.GuildRankings.Total; 0 until the first page arrived). */
  get guildTotal(): number { return this._guildTotal; }
  /** Guild board page `page` (0-based; rank of row i = page * 20 + i + 1), or undefined when not received yet. */
  guildPage(page: number): readonly ClientGuildRanking[] | undefined { return this.guildPages.get(page); }

  /** C.GetGuildRanking (web): guild board page `page` (0-based; the server clamps it; its board cache is rebuilt at most
   *  every 5 minutes, so the UI may re-ask freely when a page is shown). */
  requestGuildPage(page: number): void {
    this.send(ClientPackets.GetGuildRanking({ Page: Math.max(0, Math.trunc(page)) | 0 }));
  }

  /** RankingDialog.RequestRanks: C.GetRanking from `rankIndex` (the first row shown). */
  request(rankType: number, rankIndex: number, onlineOnly: boolean): void {
    if (rankType > 6) return;
    this.send(ClientPackets.GetRanking({ RankType: rankType, RankIndex: rankIndex, OnlineOnly: onlineOnly }));
  }

  /** Server packets; true = consumed (the classic chain returned on S.Rankings). */
  handle(p: ServerPacket): boolean {
    if (p.type === "RankingsDetail") {
      for (let i = 0; i < p.Rows.length; i++) { const row = p.Rows[i]!; this.extraCache.set(row.PlayerId, row); }
      this.detailsChanged.emit({ rankType: p.RankType });
      return true;
    }
    if (p.type === "GuildRankings") {
      // A new Total means the board was rebuilt: the other cached pages are stale
      if (p.Total !== this._guildTotal) this.guildPages.clear();
      this._guildTotal = p.Total;
      this.guildPages.set(p.Page, p.Guilds);
      this.guildBoardChanged.emit({ page: p.Page });
      return true;
    }
    if (p.type !== "Rankings") return false;
    // GameScene.Rankings
    for (const info of p.ListingDetails) this.cache.set(info.PlayerId, info);
    const listings: RankCharacterInfo[] = [];
    for (const id of p.Listings) {
      const info = this.cache.get(id);
      if (info) listings.push(info); // the desktop would throw on an unknown id
    }
    // RankingDialog.RecieveRanks
    if (p.RankType < this.lists.length) {
      this.lists[p.RankType] = listings;
      this.ranks[p.RankType] = p.MyRank;
    }
    this._count = p.Count;
    this.changed.emit();
    return true;
  }
}
