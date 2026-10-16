// Client-side info cache: GameScene.MonsterInfoList / NPCInfoList / MapInfoList (BigMapRecord) / TeleportToNPCCost and the
// BigMapDialog.WorldMapSetup state, with the request helpers (GameScene.RequestMonsterInfo / RequestNPCInfo / RequestItemInfo,
// deduped by the Requested*Info sets until the answer arrives) and change listeners so the UI can redraw on an answer.
// Item infos stay in ItemInfos (items.ts, filled by game.ts on S.NewItemInfo); this class only dedupes their requests.

import type { ClientMapInfo, ClientMapRespawn, ClientMapSafeZone, ClientMonsterInfo, ClientNPCInfo, ItemInfo, ServerPacket, WorldMapIcon } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { ItemInfos } from "./items.ts";

export type InfoKind = "monster" | "npc" | "item" | "map" | "worldMap" | "mapDetails";

/** S.MapDetails for one map (web-only): its respawns and safe zones, from C.RequestMapDetails. */
export interface MapDetails {
  readonly respawns: ClientMapRespawn[];
  readonly safeZones: ClientMapSafeZone[];
  /** False while parts 1 .. PartCount - 1 are still on their way. */
  complete: boolean;
}

/**
 * Client spacing of C.RequestMapDetails: the server's per-player 1000 ms cooldown (it silently drops requests inside it,
 * timed on arrival) + a 250 ms margin for network jitter.
 */
export const MAP_DETAILS_COOLDOWN_MS = 1250;
/** A request the server never answered (not the current map and no big map, or details off) may be asked again after this. */
const MAP_DETAILS_RETRY_MS = 10_000;
export type InfoListener = (kind: InfoKind, index: number) => void;

/** ItemInfo.FriendlyName: no trailing digits, no [..] parts. */
export function itemFriendlyName(info: ItemInfo): string {
  return info.Name.replace(/\d+$/, "").replace(/\[[^\]]*\]/g, "");
}

export class GameInfo {
  private readonly monsters = new Map<number, ClientMonsterInfo>(); // GameScene.MonsterInfoList
  private readonly npcs = new Map<number, ClientNPCInfo>();         // GameScene.NPCInfoList
  private readonly maps = new Map<number, ClientMapInfo>();         // GameScene.MapInfoList (BigMapRecord.MapInfo)
  private readonly requestedItems = new Set<number>();              // GameScene.RequestedItemInfo
  private readonly requestedMonsters = new Set<number>();           // GameScene.RequestedMonsterInfo
  private readonly requestedNPCs = new Set<number>();               // GameScene.RequestedNPCInfo
  private readonly pendingMaps = new Set<number>();                 // NPCDialog.PendingMapInfoRequests
  private readonly listeners = new Set<InfoListener>();
  private readonly mapDetails = new Map<number, MapDetails>();       // S.MapDetails per map index
  private readonly mapDetailsAsked = new Map<number, number>();      // map index -> request time (until the last part)
  private readonly mapDetailsQueue: number[] = [];                   // waiting for the cooldown
  private mapDetailsSentAt = -Infinity;
  private mapDetailsTimer = 0;

  /** GameScene.TeleportToNPCCost (S.WorldMapSetup). */
  teleportToNPCCost = 0;
  /** BigMapDialog.WorldMapSetup: WorldButton.Visible = Enabled, WorldMap.MakeButtons(Icons). */
  worldMap: { Enabled: boolean; Icons: WorldMapIcon[] } = { Enabled: false, Icons: [] };

  /** `mapDetailsEnabled` = ServerRules.MapDetailsEnabled (requests are dropped while it is false). */
  constructor(
    private readonly send: (f: Uint8Array) => void,
    private readonly items: ItemInfos,
    private readonly mapDetailsEnabled: () => boolean = () => true,
    private readonly now: () => number = () => performance.now(),
  ) {}

  monster(index: number): ClientMonsterInfo | undefined { return this.monsters.get(index); }
  npc(index: number): ClientNPCInfo | undefined { return this.npcs.get(index); }
  map(index: number): ClientMapInfo | undefined { return this.maps.get(index); }
  /** S.MapDetails of a map (undefined until asked with requestMapDetails and answered; check `complete`). */
  details(index: number): MapDetails | undefined { return this.mapDetails.get(index); }
  item(index: number): ItemInfo | undefined { return this.items.get(index); }

  /** Adds a listener; returns its remover. */
  onChange(fn: InfoListener): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private emit(kind: InfoKind, index: number): void {
    for (const fn of this.listeners) {
      try { fn(kind, index); } catch (e) { console.warn("[gameInfo] listener:", e); }
    }
  }

  /** True when the packet was consumed. S.NewItemInfo is only observed (game.ts adds it to ItemInfos): returns false. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NewMonsterInfo": // GameScene.NewMonsterInfo
        this.monsters.set(p.Info.Index, p.Info);
        this.requestedMonsters.delete(p.Info.Index); // OnMonsterInfoReceived
        this.emit("monster", p.Info.Index);
        return true;
      case "NewNPCInfo": // GameScene.NewNPCInfo
        this.npcs.set(p.Info.Index, p.Info);
        this.requestedNPCs.delete(p.Info.Index); // OnNPCInfoReceived
        this.emit("npc", p.Info.Index);
        return true;
      case "NewMapInfo": // GameScene.NewMapInfo (MapInfoList.Add; a repeat replaces the record instead of throwing)
        this.maps.set(p.MapIndex, p.Info);
        this.pendingMaps.delete(p.MapIndex);
        this.emit("map", p.MapIndex);
        return true;
      case "WorldMapSetupInfo": // GameScene.WorldMapSetup
        this.worldMap = { Enabled: p.Setup.Enabled, Icons: p.Setup.Icons };
        this.teleportToNPCCost = p.TeleportToNPCCost;
        this.emit("worldMap", 0);
        return true;
      case "MapDetails": { // web-only: part 0 replaces the map's details, later parts append
        let d = this.mapDetails.get(p.MapIndex);
        if (p.Part === 0 || !d) {
          d = { respawns: [], safeZones: [], complete: false };
          this.mapDetails.set(p.MapIndex, d);
        }
        d.respawns.push(...p.Respawns);
        d.safeZones.push(...p.SafeZones);
        d.complete = p.Part + 1 >= p.PartCount;
        if (d.complete) this.mapDetailsAsked.delete(p.MapIndex);
        for (const r of p.Respawns) this.requestMonster(r.MonsterIndex); // the server sends the infos first; this only fills gaps
        this.emit("mapDetails", p.MapIndex);
        return true;
      }
      case "NewItemInfo": // GameScene.OnItemInfoReceived (the item itself is added by game.ts)
        this.requestedItems.delete(p.Info.Index);
        queueMicrotask(() => this.emit("item", p.Info.Index)); // after game.ts has added it
        return false;
    }
    return false;
  }

  /** GameScene.RequestItemInfo. */
  requestItem(index: number): void {
    if (index <= 0 || this.items.get(index) || this.requestedItems.has(index)) return;
    this.requestedItems.add(index);
    this.send(ClientPackets.RequestItemInfo({ ItemIndex: index }));
  }

  /** GameScene.RequestMonsterInfo. */
  requestMonster(index: number): void {
    if (index <= 0 || this.monsters.has(index) || this.requestedMonsters.has(index)) return;
    this.requestedMonsters.add(index);
    this.send(ClientPackets.RequestMonsterInfo({ MonsterIndex: index }));
  }

  /** GameScene.RequestNPCInfo. */
  requestNPC(index: number): void {
    if (index <= 0 || this.npcs.has(index) || this.requestedNPCs.has(index)) return;
    this.requestedNPCs.add(index);
    this.send(ClientPackets.RequestNPCInfo({ NPCIndex: index }));
  }

  /** C.RequestMapInfo when the record is missing. `dedupe` = NPCDialog.GetNPCInfo (PendingMapInfoRequests);
   *  BigMapDialog.SetTargetMap asks every time (the server only answers once per map: Connection.SentMapInfo). */
  requestMap(index: number, dedupe = true): void {
    if (this.maps.has(index)) return;
    if (dedupe) {
      if (this.pendingMaps.has(index)) return;
      this.pendingMaps.add(index);
    }
    this.send(ClientPackets.RequestMapInfo({ MapIndex: index }));
  }

  /** C.RequestMapDetails (web-only), deduped: nothing when the details are cached or already asked (unless `refresh`),
   *  and spaced by the server's 1 s cooldown (a request inside it waits on a timer). The answer fires onChange("mapDetails").
   *  The server answers only for the current map or a map with a big map, and only while ServerRules.MapDetailsEnabled. */
  requestMapDetails(index: number, refresh = false): void {
    if (index < 0 || !this.mapDetailsEnabled()) return;
    if (!refresh && this.mapDetails.get(index)?.complete) return;
    const asked = this.mapDetailsAsked.get(index);
    if (asked !== undefined && this.now() - asked < MAP_DETAILS_RETRY_MS) return;
    if (this.mapDetailsQueue.includes(index)) return;
    this.mapDetailsQueue.push(index);
    this.flushMapDetails();
  }

  private flushMapDetails(): void {
    if (this.mapDetailsTimer || this.mapDetailsQueue.length === 0) return;
    const wait = this.mapDetailsSentAt + MAP_DETAILS_COOLDOWN_MS - this.now();
    if (wait > 0) {
      this.mapDetailsTimer = window.setTimeout(() => { this.mapDetailsTimer = 0; this.flushMapDetails(); }, wait);
      return;
    }
    const index = this.mapDetailsQueue.shift()!;
    if (!this.mapDetailsEnabled()) { this.mapDetailsQueue.length = 0; return; }
    const now = this.now();
    this.mapDetailsSentAt = now;
    this.mapDetailsAsked.set(index, now);
    this.send(ClientPackets.RequestMapDetails({ MapIndex: index }));
    this.flushMapDetails();
  }

  /** Drop a map's cached details (e.g. after a LiveDb map edit) so the next requestMapDetails asks again. */
  invalidateMapDetails(index: number): void {
    this.mapDetailsAsked.delete(index);
    if (this.mapDetails.delete(index)) this.emit("mapDetails", index);
  }

  /** Game dispose: stop the queued map-details requests. */
  dispose(): void {
    if (this.mapDetailsTimer) clearTimeout(this.mapDetailsTimer);
    this.mapDetailsTimer = 0;
    this.mapDetailsQueue.length = 0;
  }

  /** GameScene.InvalidateMapInfo (S.GameDataChanged.MapInfoInvalidated, not ported yet): drop the record so it is asked again. */
  invalidateMap(index: number): void {
    if (!this.maps.delete(index)) return;
    this.pendingMaps.delete(index);
    this.emit("map", index);
  }
}
