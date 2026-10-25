// S.GameDataChanged (the web admin's LiveDb push, GameScene.GameDataChanged): upserts game data the client already caches.
// Items / monsters / NPCs go through the same handlers as S.NewItemInfo / NewMonsterInfo / NewNPCInfo
// (the desktop's Upsert* helpers are what those handlers call), spells copy their server-owned fields onto the user's and
// the hero's own magics (CopyMagicInfo), shop rows replace by GIndex (RemoveGameShopItem + GameShopInfo), and big-map
// records are dropped so they are requested again (InvalidateMapInfo). The packet arrives in chunks; `Last` ends a batch.

import type { ClientMagic, GameShopItem, PacketOf, ServerPacket } from "../protocol/server.ts";
import type { UserMagic } from "./magic.ts";

export interface GameDataHost {
  /** Runs a synthetic packet through game.ts's normal handler (NewItemInfo, NewMonsterInfo, NewNPCInfo, GameShopInfo). */
  dispatch(p: ServerPacket): void;
  userMagics(): UserMagic[];
  heroMagics(): UserMagic[];
  /** GameScene.GameShopInfoList (shop.ts `Shop.items`, edited in place). */
  shopItems(): GameShopItem[];
  invalidateMap(mapIndex: number): void;
}

/** What changed in the batch so far (GameScene._gameDataMagicsChanged / _gameDataShopChanged). */
export interface GameDataChanges { magics: boolean; shop: boolean }

/** GameScene.CopyMagicInfo: the pushed spell's server-owned fields onto the actor's own magic of the same Spell. */
function copyMagicInfo(magics: UserMagic[], pushed: ClientMagic): boolean {
  const m = magics.find((x) => x.Spell === pushed.Spell);
  if (!m) return false;
  m.Name = pushed.Name; m.BaseCost = pushed.BaseCost; m.LevelCost = pushed.LevelCost; m.Icon = pushed.Icon;
  m.Level1 = pushed.Level1; m.Level2 = pushed.Level2; m.Level3 = pushed.Level3;
  m.Need1 = pushed.Need1; m.Need2 = pushed.Need2; m.Need3 = pushed.Need3; m.Range = pushed.Range;
  m.delay = Number(pushed.Delay);
  return true;
}

/** GameScene.RemoveGameShopItem. */
function removeShopItem(list: GameShopItem[], gIndex: number): boolean {
  let removed = false;
  for (let i = list.length - 1; i >= 0; i--) if (list[i]!.GIndex === gIndex) { list.splice(i, 1); removed = true; }
  return removed;
}

/** GameScene.GameDataChanged (the try block). Accumulates into `changes`; the caller refreshes on `p.Last`. */
export function applyGameDataChanged(p: PacketOf<"GameDataChanged">, host: GameDataHost, changes: GameDataChanges): void {
  for (const Info of p.Items) host.dispatch({ type: "NewItemInfo", Info });
  for (const Info of p.Monsters) host.dispatch({ type: "NewMonsterInfo", Info });
  for (const Info of p.Npcs) host.dispatch({ type: "NewNPCInfo", Info });
  for (const m of p.Magics) if (copyMagicInfo(host.userMagics(), m)) changes.magics = true;
  for (const m of p.HeroMagics) if (copyMagicInfo(host.heroMagics(), m)) changes.magics = true;
  const shop = host.shopItems();
  for (const g of p.GameShopRemoved) if (removeShopItem(shop, g)) changes.shop = true;
  for (const e of p.GameShop) {
    removeShopItem(shop, e.Item.GIndex);
    host.dispatch({ type: "GameShopInfo", Item: e.Item, StockLevel: e.StockLevel }); // GameShopUpdate: Stock = StockLevel, Add
    changes.shop = true;
  }
  for (const m of p.MapInfoInvalidated) host.invalidateMap(m);
}
