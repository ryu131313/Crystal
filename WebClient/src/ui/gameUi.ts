// The game scene's UI seam: what game.ts (the world: GameScene.ProcessPacket, MapControl, the frame loop) hands a UI,
// and what it calls on that UI. game.ts builds the models and one GameUiContext, then one GameUi: the classic
// desktop-look windows (classicUi.ts, src/ui/*.ts) or, from phase 3a of the UI port, the scene-lab UI (src/ui/modern/).
// The desktop has one UI only; the seam is web-only. Everything here is live: getters over startGame's locals, so a UI
// reads the current value every time and never keeps a copy.
//
// Packets: game.ts runs the models first (hero, inventory, item changes, hero items, magic, quests), then
// GameUi.handle (the UI-only leftovers: packets whose state still lives in a classic window; phases 1b / 1c move them
// into src/game models, so handle() shrinks to prompts and window opening), then GameInfo and the big switch.

import type { Session } from "../game/session.ts";
import type { ServerPacket, PacketOf, BaseStats, UserItem, GameShopItem } from "../protocol/server.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { MapCells } from "../map/mapReader.ts";
import type { MapObj } from "../game/objects.ts";
import type { LightState } from "../game/lighting.ts";
import type { SoundManager } from "../game/audio.ts";
import type { ItemInfos } from "../game/items.ts";
import type { GameInfo } from "../game/gameInfo.ts";
import type { Inventory } from "../game/inventory.ts";
import type { Hero } from "../game/hero.ts";
import type { HeroItems } from "../game/heroItems.ts";
import type { ItemChanges } from "../game/itemChanges.ts";
import type { Magic } from "../game/magic.ts";
import type { Quests } from "../game/quests.ts";
import type { Creatures } from "../game/creatures.ts";
import type { UserStats } from "../game/stats.ts";
import type { UserBuff } from "../game/buffs.ts";
import type { InputRouter } from "../game/input.ts";

/** The UI style setting (settings.ts); game.ts resolves it with uiStyle(). */
export type { UiStyle } from "../game/settings.ts";

/** The user's live state (GameScene.User / UserObject fields kept by game.ts). Read-only views over startGame's locals. */
export interface PlayerState {
  /** GameScene.User (null until S.UserInformation). */
  readonly user: MapObj | null;
  /** UserObject.Level / HP / MP. */
  readonly level: number;
  readonly hp: number;
  readonly mp: number;
  /** UserObject.Experience / MaxExperience. */
  readonly experience: bigint;
  readonly maxExperience: bigint;
  /** UserObject.Gender (S.UserInformation). */
  readonly gender: number;
  /** UserObject.Stats & co (RefreshStats; null until the first recompute). */
  readonly stats: UserStats | null;
  /** UserObject.CoreStats (S.BaseStatsInfo). */
  readonly baseStats: BaseStats | null;
  /** GameScene.AMode / PMode. */
  readonly aMode: number;
  readonly pMode: number;
  /** BuffsDialog.Buffs (the user's buffs, the local Guild buff included). */
  readonly buffs: readonly UserBuff[];
  /** The summed active guild buff stats (GameScene.GuildBuffList; null when none). */
  readonly guildBuffStats: Record<number, number> | null;
}

/** The DOM-free models game.ts builds before the UI (src/game/*). */
export interface GameModels {
  readonly itemInfos: ItemInfos;
  readonly gameInfo: GameInfo;
  readonly inventory: Inventory;
  readonly hero: Hero;
  readonly heroItems: HeroItems;
  readonly itemChanges: ItemChanges;
  readonly magic: Magic;
  readonly quests: Quests;
  /** Fed by the classic CreatureWindow until phase 1b (creatures fed by game.ts). */
  readonly creatures: Creatures;
  /** GameScene.ChatItemList (S.NewChatItem: incoming chat links), keyed by UniqueID. */
  readonly chatItems: ReadonlyMap<string, UserItem>;
}

/** The world as the UI may read it (MapControl). */
export interface GameWorld {
  /** MapControl.M2CellInfo of the current map (a blank map while loading). */
  readonly map: MapCells;
  /** MapControl.Title / MiniMap (S.MapInformation / MapChanged). */
  readonly mapTitle: string;
  readonly miniMapIndex: number;
  /** Map light, time of day, dark light. */
  readonly lights: LightState;
  /** MapControl.Objects by ObjectID. */
  readonly objects: ReadonlyMap<number, MapObj>;
  /** MapControl.MapLocation of the mouse (null without a user). */
  mouseCell(): WirePoint | null;
  /** MapControl.AutoPath: walk a found path to the cell (BigMap click); posts "Could not find a path." when none. */
  walkTo(cell: WirePoint): void;
}

/** World actions a UI may start. */
export interface GameActions {
  /** GameScene.LogOut / QuitGame: refused with "Cannot leave game for N seconds" inside the combat lock, else the prompt. */
  logOut(exit: boolean): void;
  /** GameScene KeybindOptions.Pickup (C.PickUp, 200 ms throttle). */
  pickUp(): void;
  /** GameScene.GuildBuffList: the summed guild buff stats (null = none) for RefreshStats and the local Guild buff. */
  setGuildBuff(stats: Record<number, number> | null): void;
  /** GameScene GuildStatus: the user's guild name (ObjectGuildNameChanged skips the user). */
  setUserGuild(name: string): void;
}

/** Everything a GameUi is built from. Built once per startGame. */
export interface GameUiContext {
  /** #stage (game pixels; play/main.ts scales it). setUiRoot(stage) has run. */
  readonly stage: HTMLElement;
  /** The game resolution [width, height]. */
  readonly screen: readonly [number, number];
  /** The scene scope (gameSignal()): aborts on dispose. */
  readonly signal: AbortSignal;
  send(frame: Uint8Array): void;
  /** performance.now(). */
  now(): number;
  readonly sound: SoundManager;
  readonly session: Session;
  readonly models: GameModels;
  readonly player: PlayerState;
  readonly world: GameWorld;
  readonly actions: GameActions;
}

/** Notifications from the world (game.ts's big switch) that a UI may show. Leftovers until the models own the state. */
export type UiEvent =
  /** S.NewItemInfo: an ItemInfo arrived (item cells redraw). */
  | { kind: "itemInfo" }
  /** GameScene.UserInformation / S.AllowObserve: the user's AllowObserve flag. */
  | { kind: "allowObserve"; allow: boolean }
  /** GameScene.MountUpdate after the object update (the user's MountDialog). */
  | { kind: "mountUpdate"; packet: PacketOf<"MountUpdate"> }
  /** GameScene.FishingUpdate after the object update (FishingDialog / FishingStatusDialog), with a user. */
  | { kind: "fishingUpdate"; packet: PacketOf<"FishingUpdate"> }
  /** The hero's stats may have changed (hero items, GameDataChanged): until heroStats.ts (phase 1b). */
  | { kind: "heroStats" }
  /** GameScene.GameDataChanged after its last chunk: the refresh (hero stats, an open shop when shop items changed). */
  | { kind: "gameDataApplied"; shopChanged: boolean };

/** UI commands game.ts starts from keys or map clicks. */
export type UiCommand =
  /** KeybindOptions.Trade: TradeDialog request (C.TradeRequest). */
  | { kind: "trade" }
  /** KeybindOptions.Ranking / Minimap: toggle that window. */
  | { kind: "toggle"; window: "ranking" | "miniMap" }
  /** MapControl.OnMouseClick, Ctrl + right-click on another player: InspectDialog (C.Inspect). */
  | { kind: "inspect"; objectId: number }
  /** MapControl.OnMouseClick on an NPC: C.CallNPC "[@Main]" (GameScene.NPCID). */
  | { kind: "talk"; objectId: number; name: string };

/** Where in game.ts step() a UI frame hook runs (the classic windows keep the desktop's interleaving with the world). */
export type FramePoint =
  /** After the stats recompute, before CheckInput. */
  | "beforeInput"
  /** After CheckInput, before the cursor / world render. */
  | "afterInput"
  /** With a user only: after the map render and colour grade, before the 2D name overlay. */
  | "afterRender"
  /** Last in the frame. */
  | "end";

export interface GameUi {
  readonly style: "Modern" | "Classic";
  /** The UI-only leftovers of GameScene.ProcessPacket, after the models. True = consumed: GameInfo and the big switch skip it. */
  handle(p: ServerPacket): boolean;
  /** A world notification from the big switch. */
  notify(e: UiEvent): void;
  /** Per-frame UI work at one point of step(). */
  frame(t: number, at: FramePoint): void;
  /** ChatDialog.ReceiveChat. */
  chat(text: string, type: number): void;
  /** GameScene.OutputMessage (the HUD's output lines). */
  output(text: string): void;
  /** A Yes / No prompt (MirMessageBox YesNo); returns its close. */
  confirm(text: string, yes: () => void, opts?: { primary?: boolean; no?: () => void }): () => void;
  /** A count prompt (MirAmountBox); returns its close. */
  amount(title: string, image: number, max: number, ok: (n: number) => void): () => void;
  /** GameScene: the DiedTip box after S.Death (true) / closed on S.Revived (false). */
  revive(show: boolean): void;
  /** GameScene.RequestReincarnation: the "revived?" prompt. */
  reincarnation(): void;
  /** ChatDialog: the chat input has the focus (game keys and CheckInput stop). */
  readonly typing: boolean;
  blurChat(): void;
  /** ChatDialog.ChatTextBox prefill (whisper "/name ", guild "!~"). */
  prefillChat(text: string): void;
  /** A left / right mouse-down on the map, after Ctrl + right inspect: a picked item / gold dropped on the map. True = consumed. */
  mapMouseDown(e: MouseEvent): boolean;
  command(c: UiCommand): void;
  /** Leftover until group.ts (phase 1b): the name is in the user's group (hover outline). */
  inGroup(name: string): boolean;
  /** Leftover until shop.ts (phase 1c): GameScene.GameShopInfoList for GameDataChanged. */
  shopItems(): GameShopItem[];
  /** Registers the UI's router handlers (called once by game.ts where its own key handling is registered). */
  bindInput(input: InputRouter): void;
  /** Debug refs for window.crystalGame (the classic windows by their old names). */
  readonly debug: Record<string, unknown>;
  /** After endGameScope: what the scope does not drop (classic: the MirButton sound hook). */
  dispose(): void;
}
