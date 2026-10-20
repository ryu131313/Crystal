// The game data the server sends once per connection (MirConnection.SentItemInfo / SentMonsterInfo / SentNPCInfo /
// SentQuestInfo / SentRecipeInfo / SentChatItem / SentMapInfo, and StorageSent for S.UserStorage). The desktop keeps
// these in static GameScene lists that outlive the scene; the web client rebuilds its models with every game scene, so
// a log-out and back in over the same connection lost them: the server never resends, and the bag, equipment and
// tooltips stayed on "Loading item details". This per-Session store records the packets as game.ts handles them and
// replays them into the next game scene, like serverRules.ts keeps S.ServerRules.
//
// Not kept: S.WorldMapSetupInfo (the server resends it after each log-out) and the hero roster (S.ManageHeroes /
// NewHeroInfo are per character; the server's HeroStorageSent / SentHeroInfo flags are per connection).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import type { Session } from "./session.ts";

/** Packet type + the record's key; null = not a once-per-connection packet. */
function keyOf(p: ServerPacket): string | null {
  switch (p.type) {
    case "NewItemInfo": return `i${p.Info.Index}`;
    case "NewMonsterInfo": return `m${p.Info.Index}`;
    case "NewNPCInfo": return `n${p.Info.Index}`;
    case "NewQuestInfo": return `q${p.Info.Index}`;
    case "NewRecipeInfo": return `r${p.Info.Item.ItemIndex}`;
    case "NewChatItem": return `c${p.Item.UniqueID}`;
    case "NewMapInfo": return `p${p.MapIndex}`;
    case "UserStorage": return "s";
  }
  return null;
}

export class InfoCache {
  /** Insertion order is kept (ItemInfos.realItem scans infos in arrival order); a repeat replaces in place. */
  private readonly packets = new Map<string, ServerPacket>();

  /** Called with every packet game.ts handles (live and GameDataChanged upserts). */
  record(p: ServerPacket): void {
    const key = keyOf(p);
    if (key) this.packets.set(key, p);
  }

  /** S.GameDataChanged MapInfoInvalidated: the server forgot it sent the map (SentMapInfo), so the client asks again. */
  forgetMap(mapIndex: number): void {
    this.packets.delete(`p${mapIndex}`);
  }

  /** Game dispose: storage changed in place since S.UserStorage (StoreItem / TakeBackItem / sorting); keep the last view. */
  saveStorage(storage: (UserItem | null)[]): void {
    if (this.packets.has("s")) this.packets.set("s", { type: "UserStorage", Storage: storage } as ServerPacket);
  }

  /** The recorded packets, oldest first, for a new game scene. */
  replay(): ServerPacket[] {
    return Array.from(this.packets.values());
  }
}

const caches = new WeakMap<Session, InfoCache>();

/** The info cache for this connection (created on first use, dropped with the Session). */
export function infoCache(session: Session): InfoCache {
  let cache = caches.get(session);
  if (!cache) { cache = new InfoCache(); caches.set(session, cache); }
  return cache;
}
