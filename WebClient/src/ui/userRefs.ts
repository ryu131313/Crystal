// Links between the user's item / character / magic windows, which game.ts builds separately (the desktop reaches
// them through GameScene.Scene.InventoryDialog / CharacterDialog / SkillBarDialogs). Each window registers itself in
// its constructor; the scene's abort clears the links.
//
// `gameUser()` / `gameItemInfos()` read the game's `window.crystalGame` object (set at the end of startGame) for the
// few values game.ts does not pass to these windows yet (the user's gender / hair / guild for the paperdoll and the
// ItemInfo table for GetRealItem in the item label). Stop-gap until game.ts passes them: see the windows' hooks.

import type { InventoryWindow } from "./inventoryWindow.ts";
import type { CharacterWindow } from "./characterWindow.ts";
import type { MagicBar } from "./magicBar.ts";
import type { ItemInfos } from "../game/items.ts";
import { gameSignal } from "../game/scope.ts";

export const userWindows: { inventory: InventoryWindow | null; character: CharacterWindow | null; magic: MagicBar | null } = {
  inventory: null, character: null, magic: null,
};

/** Registers a window for the current game scene (cleared when the scene ends). */
export function registerUserWindow<K extends keyof typeof userWindows>(key: K, win: NonNullable<(typeof userWindows)[K]>): void {
  userWindows[key] = win;
  gameSignal().addEventListener("abort", () => { if (userWindows[key] === win) userWindows[key] = null; });
}

interface GameUserLike { look: { class: number; gender: number; hair: number }; guildName: string; name: string }
interface CrystalGameLike { user?: GameUserLike | null; itemInfos?: ItemInfos }

function crystalGame(): CrystalGameLike | null {
  return ((window as unknown as { crystalGame?: CrystalGameLike }).crystalGame) ?? null;
}

/** The user's MapObj (look, guild name), or null before the game object exists. */
export function gameUser(): GameUserLike | null {
  return crystalGame()?.user ?? null;
}

/** GameScene.ItemInfoList (for Functions.GetRealItem), or null. */
export function gameItemInfos(): ItemInfos | null {
  return crystalGame()?.itemInfos ?? null;
}
