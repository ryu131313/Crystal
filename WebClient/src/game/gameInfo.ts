// Client-side info cache: GameScene.MonsterInfoList / NPCInfoList / MapInfoList (BigMapRecord) / TeleportToNPCCost and the
// BigMapDialog.WorldMapSetup state, with the request helpers (GameScene.RequestMonsterInfo / RequestNPCInfo / RequestItemInfo,
// deduped by the Requested*Info sets until the answer arrives) and change listeners so the UI can redraw on an answer.
// Item infos stay in ItemInfos (items.ts, filled by game.ts on S.NewItemInfo); this class only dedupes their requests.

import type { ClientMapInfo, ClientMonsterInfo, ClientNPCInfo, ItemInfo, ServerPacket, WorldMapIcon } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { ItemInfos } from "./items.ts";

export type InfoKind = "monster" | "npc" | "item" | "map" | "worldMap";
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

  /** GameScene.TeleportToNPCCost (S.WorldMapSetup). */
  teleportToNPCCost = 0;
  /** BigMapDialog.WorldMapSetup: WorldButton.Visible = Enabled, WorldMap.MakeButtons(Icons). */
  worldMap: { Enabled: boolean; Icons: WorldMapIcon[] } = { Enabled: false, Icons: [] };

  constructor(private readonly send: (f: Uint8Array) => void, private readonly items: ItemInfos) {}

  monster(index: number): ClientMonsterInfo | undefined { return this.monsters.get(index); }
  npc(index: number): ClientNPCInfo | undefined { return this.npcs.get(index); }
  map(index: number): ClientMapInfo | undefined { return this.maps.get(index); }
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

  /** GameScene.InvalidateMapInfo (S.GameDataChanged.MapInfoInvalidated, not ported yet): drop the record so it is asked again. */
  invalidateMap(index: number): void {
    if (!this.maps.delete(index)) return;
    this.pendingMaps.delete(index);
    this.emit("map", index);
  }
}
