// The game scene's UI seam: what game.ts (the world: GameScene.ProcessPacket, MapControl, the frame loop) hands a UI,
// and what it calls on that UI. game.ts builds the models and one GameUiContext, then one GameUi: the classic
// desktop-look windows (classicUi.ts, src/ui/*.ts) or, from phase 3a of the UI port, the scene-lab UI (src/ui/modern/).
// The desktop has one UI only; the seam is web-only. Everything here is live: getters over startGame's locals, so a UI
// reads the current value every time and never keeps a copy.
//
// Packets: game.ts runs the models first (hero, inventory, item changes, hero items, magic, quests, then the phase 1b
// models: rental, chat log, current map, account, creatures, HUD state, group, trade; the phase 1c models: relationship,
// mentor, inspect, rankings, mail, friends, shop, guild, NPC session, recipes, awakening), then GameUi.handle (the
// UI-only leftovers: packets whose state still lives in a classic window, prompts and window opening), then the
// territory and market models, then GameInfo and the big switch. Models announce changes and
// prompts through Emitter fields (src/game/emitter.ts); a UI subscribes and unsubscribes on `signal` abort.

import type { Session } from "../game/session.ts";
import type { ServerPacket, BaseStats } from "../protocol/server.ts";
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
import type { PoisonTimers } from "../game/poisons.ts";
import type { Quests } from "../game/quests.ts";
import type { Creatures } from "../game/creatures.ts";
import type { ChatLog } from "../game/chatLog.ts";
import type { HudState } from "../game/hudState.ts";
import type { Account } from "../game/account.ts";
import type { CurrentMap } from "../game/currentMap.ts";
import type { ItemPick } from "../game/itemPick.ts";
import type { HeroStats } from "../game/heroStats.ts";
import type { MountState } from "../game/mountState.ts";
import type { Fishing } from "../game/fishing.ts";
import type { GroupState } from "../game/group.ts";
import type { Trade } from "../game/trade.ts";
import type { Rental } from "../game/rental.ts";
import type { NpcSession } from "../game/npcSession.ts";
import type { Recipes } from "../game/recipes.ts";
import type { Awakening } from "../game/awakening.ts";
import type { Market } from "../game/market.ts";
import type { GuildState } from "../game/guild.ts";
import type { MailBox } from "../game/mail.ts";
import type { Friends } from "../game/friends.ts";
import type { Relationship, Mentor } from "../game/relationship.ts";
import type { Rankings } from "../game/rankings.ts";
import type { Inspect } from "../game/inspect.ts";
import type { Shop } from "../game/shop.ts";
import type { Territories } from "../game/territories.ts";
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
  /** UserObject.IntelligentCreatures (fed by game.ts; `changed` for the dialog). */
  readonly creatures: Creatures;
  /** ChatDialog.History / LastPM / LinkedItems + MirScene.ChatItemList (src/game/chatLog.ts); every chat line goes through it. */
  readonly chatLog: ChatLog;
  /** Output lines, notice, timers, compass, chat notice, roll (src/game/hudState.ts); a UI calls process(now) per frame. */
  readonly hudState: HudState;
  /** GameScene.AllowObserve + the UserObject storage flags (src/game/account.ts). */
  readonly account: Account;
  /** MapControl Index / FileName / Title / MiniMap / BigMap + the map lights (src/game/currentMap.ts). */
  readonly currentMap: CurrentMap;
  /** GameScene.SelectedCell / PickedUpGold (src/game/itemPick.ts). */
  readonly itemPick: ItemPick;
  /** UserHeroObject.Stats (src/game/heroStats.ts). */
  readonly heroStats: HeroStats;
  /** PlayerObject.MountTime + MountDialog.CanRide / Ride (src/game/mountState.ts). */
  readonly mount: MountState;
  /** FishingStatusDialog state (src/game/fishing.ts). */
  readonly fishing: Fishing;
  /** GroupDialog roster + the GameScene group handlers (src/game/group.ts). */
  readonly group: GroupState;
  /** GuestTradeDialog + the GameScene trade handlers (src/game/trade.ts). */
  readonly trade: Trade;
  /** The rental guest side + rented-items list (src/game/rental.ts). */
  readonly rental: Rental;
  /** NPCDialog / goods / drop state + GameScene's NPC handlers, NPCID and NPCRate (src/game/npcSession.ts). */
  readonly npcSession: NpcSession;
  /** GameScene.RecipeInfoList (src/game/recipes.ts). */
  readonly recipes: Recipes;
  /** NPCAwakeDialog state (src/game/awakening.ts). */
  readonly awakening: Awakening;
  /** TrustMerchantDialog state (src/game/market.ts). */
  readonly market: Market;
  /** GuildDialog state + GameScene guild handlers (src/game/guild.ts). */
  readonly guild: GuildState;
  /** UserObject.Mail / NewMail + GameScene mail handlers (src/game/mail.ts). */
  readonly mail: MailBox;
  /** FriendDialog.Friends + GameScene.FriendUpdate (src/game/friends.ts). */
  readonly friends: Friends;
  /** RelationshipDialog + GameScene marriage / divorce handlers (src/game/relationship.ts). */
  readonly relationship: Relationship;
  /** MentorDialog + GameScene mentor handlers (src/game/relationship.ts). */
  readonly mentor: Mentor;
  /** InspectDialog state (src/game/inspect.ts). */
  readonly inspect: Inspect;
  /** RankingDialog lists (src/game/rankings.ts). */
  readonly rankings: Rankings;
  /** GameScene.GameShopInfoList (src/game/shop.ts). */
  readonly shop: Shop;
  /** GuildTerritoryDialog list (src/game/territories.ts). */
  readonly territories: Territories;
  /** The user's poison timers from S.PoisonStatus (web-only; src/game/poisons.ts). */
  readonly poisons: PoisonTimers;
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
  /** GameScene.GameDataChanged after its last chunk: the refresh (an open shop when shop items changed). */
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
  /** Registers the UI's router handlers (called once by game.ts where its own key handling is registered). */
  bindInput(input: InputRouter): void;
  /** Debug refs for window.crystalGame (the classic windows by their old names). */
  readonly debug: Record<string, unknown>;
  /** After endGameScope: what the scope does not drop (classic: the MirButton sound hook). */
  dispose(): void;
}
