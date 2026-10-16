// Rankings: RankingDialog's per-tab state (RankList, Rank, RankCount) and GameScene.Rankings / RankingDialog.RecieveRanks
// + RequestRanks. RankType 0..5 = overall, Warrior, Wizard, Taoist, Assassin, Archer.
//
// The details cache (GameScene.RankingList, PlayerId -> RankCharacterInfo) lives per Session, not per game: the server
// sends each RankCharacterInfo only once per connection (MirConnection.SentRankings, never cleared at log-out) and
// Listings afterwards carry only ids, while the game scene (and this model) is rebuilt on every log-in over the same
// connection. The desktop gets the same effect from a static dictionary (process lifetime = one connection there).
// rankingDetails(session) hands out that one map; the per-tab lists and the user's rank stay per game (a new character
// must not see the previous one's rank).

import type { RankCharacterInfo, ServerPacket } from "../protocol/server.ts";
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

export class Rankings {
  /** S.Rankings applied (RankingDialog.RecieveRanks, before the scroll gap is recomputed). */
  readonly changed = new Emitter<void>();
  private readonly lists: RankCharacterInfo[][] = Array.from({ length: RANK_TYPES }, () => []);
  private readonly ranks: number[] = new Array<number>(RANK_TYPES).fill(0);
  private _count = 0;

  /** @param cache rankingDetails(session). */
  constructor(private readonly send: (f: Uint8Array) => void, private readonly cache: Map<bigint, RankCharacterInfo>) {}

  /** RankingDialog.RankList[type] (the listings in rank order from the requested RankIndex). */
  list(rankType: number): readonly RankCharacterInfo[] { return this.lists[rankType] ?? []; }
  /** RankingDialog.Rank[type]: the user's rank (0 = Not Listed). */
  myRank(rankType: number): number { return this.ranks[rankType] ?? 0; }
  /** RankingDialog.RankCount (the last packet's Count, whatever its type). */
  get count(): number { return this._count; }
  /** GameScene.RankingList (PlayerId -> details), shared by every game on this connection. */
  get details(): ReadonlyMap<bigint, RankCharacterInfo> { return this.cache; }

  /** RankingDialog.RequestRanks: C.GetRanking from `rankIndex` (the first row shown). */
  request(rankType: number, rankIndex: number, onlineOnly: boolean): void {
    if (rankType > 6) return;
    this.send(ClientPackets.GetRanking({ RankType: rankType, RankIndex: rankIndex, OnlineOnly: onlineOnly }));
  }

  /** Server packets; true = consumed (the classic chain returned on S.Rankings). */
  handle(p: ServerPacket): boolean {
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
