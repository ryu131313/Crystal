// GameScene.RecipeInfoList: the crafting recipes the server sends with S.NewRecipeInfo (MirScene.NewRecipeInfo), keyed
// by the recipe item's ItemIndex. The craftable list itself is the PanelType.Craft goods list of the NPC session
// (npcSession.ts goodsList(PanelType.Craft)). game.ts feeds handle(); the UI follows `changed` (the craft list's prices
// and the CraftDialog read the recipes).

import type { ClientRecipeInfo, ServerPacket } from "../protocol/server.ts";
import { Emitter } from "./emitter.ts";

export class Recipes {
  private readonly byItem = new Map<number, ClientRecipeInfo>();
  /** A recipe arrived (after it is stored). */
  readonly changed = new Emitter<ClientRecipeInfo>();

  /** RecipeInfoList.FirstOrDefault(x => x.Item.ItemIndex == itemIndex). */
  get(itemIndex: number): ClientRecipeInfo | undefined {
    return this.byItem.get(itemIndex);
  }

  /** The recipe's gold cost (the craft list's price line; undefined before the recipe arrives). */
  gold(itemIndex: number): number | undefined {
    return this.byItem.get(itemIndex)?.Gold;
  }

  /** Server packets. Never consumes. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "NewRecipeInfo") return false;
    this.byItem.set(p.Info.Item.ItemIndex, p.Info);
    this.changed.emit(p.Info);
    return false;
  }
}
