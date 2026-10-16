// The new UI's window catalog (plan phase 3a; filled by phases 3b-3f). A window module registers itself at import time:
//
//   import { registerWindow } from "../windowRegistry.ts";
//   registerWindow("inventory", (host) => new InventoryWindow(host));
//
// and is imported from windows/index.ts. modernUi.ts copies the catalog into each game's UiRuntime (runtime.ts
// registerWindow: the window is built on its first open, so an unopened window costs nothing) and gives it its hotkeys
// (the key bindings of hud/gameMenu.ts MENU_WINDOWS, or `options.binds`), the HUD menu strip button and the game menu
// launcher tile. A key nobody registered is a no-op everywhere (its strip button and launcher tile stay hidden).
//
// Windows the server opens (storage on S.NPCStorage, inspect on S.PlayerInspect) have no menu entry and are still built
// lazily, so their open-on-event hook lives in `options.watch`: modernUi.ts runs it once per game at UI start (or when
// the window registers later) and calls its removal when the game's UI goes (or the key is registered again).
//
// This file holds only the static catalog (no game state), so window modules can import it without an import cycle
// through modernUi.ts. Per-game state (built windows, the UI scope) lives in the UiRuntime and goes with it at log-out.

import type { KeybindOption } from "../../game/keybinds.ts";
import type { Emitter } from "../../game/emitter.ts";
import type { GameUiContext, UiEvent } from "../gameUi.ts";
import type { ItemLabelContext } from "../itemLabel.ts";
import type { ServerPacket } from "../../protocol/server.ts";
import type { UiRuntime } from "./core/runtime.ts";
import type { UiWindow } from "./core/wm.ts";
import type { DragController } from "./core/drag.ts";
import type { ItemCells } from "./core/itemCells.ts";
import type { ItemTooltip } from "./core/tooltip.ts";
import type { ChatHud } from "./hud/chat.ts";
import type { Hud } from "./hud/hud.ts";

/** What a window factory gets: the game's runtime, context and the shared HUD services (modernUi.ts builds it). */
export interface ModernUiHost {
  readonly rt: UiRuntime;
  readonly ctx: GameUiContext;
  /** The drag / pick controller (core/drag.ts) and its item cells (core/itemCells.ts: cells(grid, index)). */
  readonly drag: DragController;
  readonly cells: ItemCells;
  /** The item tooltip (core/tooltip.ts); show it with labelContext(). */
  readonly tooltip: ItemTooltip;
  readonly chat: ChatHud;
  readonly hud: Hud;
  /** World notifications (GameUi.notify: "itemInfo", "gameDataApplied") for windows that draw item infos / shop data. */
  readonly events: Emitter<UiEvent>;
  /** Every packet GameUi.handle sees (after the models ran: their state is current). Listeners never consume; for
   *  server-opened windows whose packets have no model event (storage). */
  readonly packets: Emitter<ServerPacket>;
  /** GameScene.CreateItemLabel's context for the user (level, class, gender, stats). */
  labelContext(): ItemLabelContext;
  /** Opens / closes / toggles (show undefined) a window by key; false when nothing is registered for it. */
  openWindow(key: string, show?: boolean): boolean;
  /** Opens the help window on a topic: a HelpDialog page title ("Quests", "Mounts", "Purchasing", ...) or a topic id,
   *  null / unknown = the home page (windows/help.ts showTopic). False when no help window is registered. */
  help(topic?: string | null): boolean;
}

/** Builds a window on its first open (once per game). */
export type ModernWindowFactory = (host: ModernUiHost) => UiWindow;

export interface WindowOptions {
  /** Hotkeys that toggle the window; default: the MENU_WINDOWS binding (+ its *2 alternate) for the key. */
  readonly binds?: readonly KeybindOption[];
  /** Server-opened windows: subscribes to the model event / packets that open it (`open` = host.openWindow for this
   *  key). Runs once per game at UI start, before the window is built; returns its removal (also run at UI end). */
  readonly watch?: (host: ModernUiHost, open: (show?: boolean) => boolean) => (() => void) | void;
}

export interface WindowEntry { readonly factory: ModernWindowFactory; readonly options: WindowOptions }

const catalog = new Map<string, WindowEntry>();
const listeners = new Set<(key: string, entry: WindowEntry) => void>();

/** Registers (or replaces) a window by key (hud/gameMenu.ts WINDOW_KEYS names). Running games pick it up at once. */
export function registerWindow(key: string, factory: ModernWindowFactory, options: WindowOptions = {}): void {
  const entry: WindowEntry = { factory, options };
  catalog.set(key, entry);
  for (const fn of [...listeners]) fn(key, entry);
}

/** The registered windows. */
export function windowCatalog(): ReadonlyMap<string, WindowEntry> { return catalog; }

/** Called for every later registerWindow (modernUi.ts, per game); returns its removal. */
export function onWindowRegistered(fn: (key: string, entry: WindowEntry) => void): () => void {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}
