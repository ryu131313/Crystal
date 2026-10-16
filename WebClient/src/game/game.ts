// In-game scene: world state from server packets (GameScene.ProcessPacket subset), the frame loop, mouse/keyboard
// input (MapControl.CheckInput subset: walk, run, turn, attack) and the name labels. The HUD is a GameUi
// (src/ui/gameUi.ts): today the classic desktop-look windows (src/ui/classicUi.ts: MainDialog, ChatDialog +
// ChatControlBar, MenuDialog, MiniMapDialog, BuffDialog, DuraStatusDialog, hero panels, ...), built from the models here.
// The user moves like the desktop: an action is queued and its packet sent when it starts; the next move waits for
// the server's S.UserLocation (MapControl.NextAction, 2.5 s fallback).

import type { Session } from "./session.ts";
import type { ServerPacket, PacketOf } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirAction as A, ObjectType, ChatType, Spell, DamageType } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { StartInfo } from "../ui/screens.ts";
import { WebGl2Renderer } from "../render/backend.ts";
import { Grade, COZY_GRADE } from "../render/grade.ts";
import { blankMap, type MapCells } from "../map/mapReader.ts";
import { attachRenderer, loadingCount } from "./libraries.ts";
import { ArtWarmer } from "./preload.ts";
import { MapObj, pointMove, directionFromPoint, creatureLabelOffset, CELL_W, CELL_H } from "./objects.ts";
import { MapView, OUTLINE_BLUE, OUTLINE_RED } from "./mapView.ts";
import { setMouseCursor } from "../ui/cursors.ts";
import { ItemInfos, equipmentLooks } from "./items.ts";
import type { LightState } from "./lighting.ts";
import { Inventory, BELT_IDX } from "./inventory.ts";
import { Magic } from "./magic.ts";
import { setEffectWorld, Effect, Lib, InterruptionEffect, ElementsEffect, TrackableEffect, DelayedExplosionEffect } from "./effects.ts";
import { spellStart, spellComplete, drawActionEffects, objectEffect, mapEffect, objectProjectile, createProjectile, SpellObj, type SpellWorld } from "./spellVisuals.ts";
import { spellHooks, moveHooks } from "./objects.ts";
import { SoundManager, SoundList } from "./audio.ts";
import { addBuffEffect, removeBuffEffect, processBuffs, setEffects, actionRefresh, poisonDots, userBuff, type UserBuff } from "./buffs.ts";
import { Doors } from "./doors.ts";
import { setUiRoot, setScreenSize } from "../ui/mir/controls.ts";
import { GameInfo } from "./gameInfo.ts";
import { ItemChanges } from "./itemChanges.ts";
import { Hero } from "./hero.ts";
import { Creatures } from "./creatures.ts";
import { ChatLog } from "./chatLog.ts";
import { HudState } from "./hudState.ts";
import { Account } from "./account.ts";
import { CurrentMap } from "./currentMap.ts";
import { ItemPick } from "./itemPick.ts";
import { HeroStats } from "./heroStats.ts";
import { MountState } from "./mountState.ts";
import { Fishing } from "./fishing.ts";
import { GroupState } from "./group.ts";
import { Trade } from "./trade.ts";
import { Rental } from "./rental.ts";
import { NpcSession } from "./npcSession.ts";
import { Recipes } from "./recipes.ts";
import { Awakening } from "./awakening.ts";
import { Market } from "./market.ts";
import { GuildState } from "./guild.ts";
import { MailBox } from "./mail.ts";
import { Friends } from "./friends.ts";
import { Relationship, Mentor } from "./relationship.ts";
import { Rankings, rankingDetails } from "./rankings.ts";
import { Inspect } from "./inspect.ts";
import { Shop } from "./shop.ts";
import { Territories } from "./territories.ts";
import { HeroItems } from "./heroItems.ts";
import { Weather } from "./weather.ts";
import { gameSettings, uiStyle } from "./settings.ts";
import { monsterActionStart, setMonsterSpecialsWorld } from "./monsterSpecials.ts";
import { applyGameDataChanged, type GameDataChanges } from "./gameData.ts";
import { Quests, questIconImage } from "./quests.ts";
import { refreshStats, type UserStats } from "./stats.ts";
import { canWearItem } from "./canWear.ts";
import { gameSignal, endGameScope } from "./scope.ts";
import type { BaseStats, SelectInfo } from "../protocol/server.ts";
import { findPath } from "./pathFinder.ts";
import { monsterSound, playerSound, fishingFrame, intelligentCreaturePickupSound, type SoundWorld } from "./sounds.ts";
import { canFish, canRideAttack, canCastNextMagic, canUseSpell, canHarvest, canRangeAttack, isFishingAction, isIdleAction, runDistance, transformTypeFromBuffs } from "./riding.ts";
import { PoisonType, MirClass, BuffType, AttackMode, PetMode, EquipmentSlot } from "../protocol/enums.generated.ts";
import { KeybindOptions, matches, matchesAny, matchesAnyBinding, type KeybindOption } from "./keybinds.ts";
import { createInputRouter } from "./input.ts";
import { mapCells, rememberMap } from "./mapFiles.ts";
import { drawDamageNumber, makeDamageNumber, processDamageList, MAX_DAMAGE_NUMBERS, type DamageNumber } from "./damageNumbers.ts";
import type { GameUi, GameUiContext, PlayerState } from "../ui/gameUi.ts";
import { createClassicUi, AMODE_TEXT, PMODE_TEXT } from "../ui/classicUi.ts";

export interface Game { handle(p: ServerPacket): void; dispose(): void }

/** Scene exits (GameScene.LogOutSuccess -> SelectScene, ReturnToLogin -> LoginScene). */
export interface GameExits { logOut(characters: SelectInfo[]): void; returnToLogin(): void }

/** GameScene.ChangeAttackMode / ChangePetMode cycles. */
const AMODE_NEXT: Record<number, number> = {
  [AttackMode.Peace]: AttackMode.Group, [AttackMode.Group]: AttackMode.Guild, [AttackMode.Guild]: AttackMode.EnemyGuild,
  [AttackMode.EnemyGuild]: AttackMode.RedBrown, [AttackMode.RedBrown]: AttackMode.All, [AttackMode.All]: AttackMode.Peace,
};
const PMODE_NEXT: Record<number, number> = {
  [PetMode.Both]: PetMode.MoveOnly, [PetMode.MoveOnly]: PetMode.AttackOnly, [PetMode.AttackOnly]: PetMode.None,
  [PetMode.None]: PetMode.FocusMasterTarget, [PetMode.FocusMasterTarget]: PetMode.Both,
};
/** Packets after which UserObject.RefreshStats runs (items, level, buffs, magic, base stats, item infos). */
const STATS_PACKETS = new Set<string>(["UserInformation", "BaseStatsInfo", "LevelChanged", "AddBuff", "RemoveBuff", "PauseBuff", "NewMagic", "RemoveMagic", "MagicLeveled", "NewItemInfo", "GuildBuffList", "GuildStatus"]);

/** Globals.LogDelay: GameScene.LogTime = CMain.Time + LogDelay (no log out / quit for 10 s after combat). */
const LOG_DELAY = 10000;

/** "<Title/UniqueID>" item links in chat text (RegexFunctions.ChatItemLinks). */
const CHAT_LINK = /<([^<>]*?)\/(\d+)>/g;

/** ItemInfo.FriendlyName (no trailing digits, no [..]) + UserItem.FriendlyName's " (count)". */
function friendlyName(name: string, count: number): string {
  const base = name.replace(/\d+$/, "").replace(/\[[^\]]*\]/g, "");
  return count > 1 ? `${base} (${count})` : base;
}

const argbCss = (c: number) => `rgb(${(c >> 16) & 255},${(c >> 8) & 255},${c & 255})`;

export function startGame(session: Session, start: StartInfo, canvas: HTMLCanvasElement, exits: GameExits): Game {
  const signal = gameSignal();
  // SelectScene StartGame: Settings.Resolution lowered to the server's [Permission] MaxResolution, then the desktop's
  // switch (1024 x 768, 1280 x 800, 1366 x 768, 1920 x 1080; anything else 1024). The web client prefers 1920 x 1080
  // (the desktop defaults to 1024); localStorage "crystal.resolution" = 1024 / 1280 / 1366 picks another.
  const preferred = (() => { try { return Number(localStorage.getItem("crystal.resolution")) || 1920; } catch { return 1920; } })();
  const resolution = start.resolution < preferred ? start.resolution : preferred;
  const [width, height] = resolution === 1920 ? [1920, 1080] : resolution === 1366 ? [1366, 768] : resolution === 1280 ? [1280, 800] : [1024, 768];
  canvas.width = width;
  canvas.height = height;
  const stage = document.getElementById("stage")!;
  stage.style.width = width + "px";
  stage.style.height = height + "px";
  setScreenSize(width, height);

  const renderer = WebGl2Renderer.create(canvas);
  if (!renderer) throw new Error("WebGL2 is not available in this browser.");
  attachRenderer(renderer);
  const view = new MapView(renderer, width, height);
  /** Options > More "Cozy mode": the colour grade over the finished map frame (render/grade.ts). */
  const grade = new Grade(renderer);
  /** Loads the user's and the monsters' animation frames ahead of the draw (preload.ts). */
  const artWarmer = new ArtWarmer();
  /** MapControl.Weather particles (weather.ts), drawn by MapView after the objects. */
  const weather = new Weather();
  view.weather = weather;
  view.atmosphere.playSound = (id) => sound.play(id); // web atmosphere: thunder
  view.atmosphere.rainAudio = (level) => sound.setRain(level); // web atmosphere: procedural rain
  view.atmosphere.splashAudio = (level) => sound.splash(level); // web atmosphere: the user's footfalls in puddles
  // Effect.cs needs the clock, the user and the painter
  setEffectWorld({
    now: () => performance.now(),
    user: () => user,
    drawLocation: (ob) => view.drawLocation(ob, user!),
    get offSetX() { return view.offSetX; },
    get offSetY() { return view.offSetY; },
    draw: (lib, i, x, y, offset, opacity) => view.painter.draw(lib, i, x, y, offset, opacity ?? 1),
    drawBlend: (lib, i, x, y, offset, rate) => view.painter.drawBlend(lib, i, x, y, offset, rate),
    get mapEffects() { return mapEffects; },
  });

  // dispose() removes whatever this scene adds to the stage; taken before the 2D overlay is added, so the overlay goes
  // too (it used to be added first, and every re-login left the old one with its last frame of labels on screen)
  const stageBefore = new Set(Array.from(stage.children));
  const overlay = document.createElement("canvas");
  overlay.width = width;
  overlay.height = height;
  overlay.style.cssText = "position:absolute;inset:0;pointer-events:none;width:100%;height:100%";
  stage.insertBefore(overlay, canvas.nextSibling);
  const g2 = overlay.getContext("2d")!;
  // The desktop-look controls (src/ui/mir/controls.ts) and the message boxes attach to the stage
  setUiRoot(stage);
  const screen: [number, number] = [width, height];

  // — Models (src/game) and world state: built before the UI, which reads them through the GameUiContext below —
  let map: MapCells = blankMap();
  /** MapControl Index / FileName / Title / MiniMap / BigMap + the map lights (currentMap.ts). */
  const currentMap = new CurrentMap();
  const lightState: LightState = currentMap.lights;
  const itemInfos = new ItemInfos();
  /** GameScene MonsterInfoList / NPCInfoList / MapInfoList (NPC page links, big map / world map). */
  const gameInfo = new GameInfo((f) => session.send(f), itemInfos);
  const inventory = new Inventory(itemInfos, (f) => session.send(f));
  /** The user's hero (hero.ts): GameScene.Hero + spawn state, storage, behaviour. */
  const hero = new Hero(() => performance.now(), (text) => chat(text, ChatType.Hint));
  /** Sockets / mount and fishing slots, seal, combine, upgrade, rental items (itemChanges.ts). */
  const itemChanges = new ItemChanges(inventory, (f) => session.send(f));
  let magicObjectId = 0; // MapObject.MagicObjectID
  const magic = new Magic({
    send: (f) => session.send(f),
    chat: (t, ty) => chat(t, ty),
    now: () => performance.now(),
    user: () => user,
    mp: () => mp,
    magicObject: () => { const ob = objects.get(magicObjectId); return ob && !ob.dead ? ob : null; },
    setMagicObject: (id) => { magicObjectId = id; },
    hero,
    heroObject: () => (hero.info ? objects.get(hero.info.objectId) ?? null : null),
  });
  const sound = new SoundManager();
  const play = (id: number) => sound.play(id);
  /** ChatDialog's state + MirScene.ChatItemList (chatLog.ts). */
  const chatLog = new ChatLog((f) => session.send(f));
  /** Output lines, notice, timers, compass, chat notice, roll (hudState.ts). */
  const hudState = new HudState({ send: (f) => session.send(f), now: () => performance.now(), play: (id) => sound.play(id), chatLog });
  /** GameScene.AllowObserve + the UserObject storage flags (account.ts). */
  const account = new Account((f) => session.send(f));
  /** BuffsDialog.Buffs (the user's). */
  let userBuffs: UserBuff[] = [];
  const doors = new Doors();
  /** MapControl.AutoPath / CurrentPath: walking a path found by a big-map click. */
  let autoPath: WirePoint[] | null = null;
  /** The hero's bag, belt (7 / 8), equipment and auto-pot (heroItems.ts). */
  const heroItems = new HeroItems(hero, inventory, (f) => session.send(f));
  /** GameScene.SelectedCell / PickedUpGold (itemPick.ts), shared by the UIs. */
  const itemPick = new ItemPick();
  /** Quests: state (quests.ts); the windows are the UI's. */
  const quests = new Quests((f) => session.send(f), itemInfos);
  const objects = new Map<number, MapObj>();
  /** Floating damage numbers per object id (damageNumbers.ts); `critAt` = when S.DamageIndicator Critical arrived, the next Hit is the crit. */
  const damageNumbers = new Map<number, DamageNumber[]>();
  const critAt = new Map<number, number>();
  let user: MapObj | null = null;
  let hp = 0, mp = 0, level = 0;
  let experience = 0n, maxExperience = 0n;
  let gender = 0; // UserObject.Experience / MaxExperience
  let aMode: number = AttackMode.Peace, pMode: number = PetMode.Both, modeTime = 0; // GameScene.AMode / PMode / ChangeAModeTime
  let baseStats: BaseStats | null = null; // UserObject.CoreStats (S.BaseStatsInfo)
  let guildBuffStats: Record<number, number> | null = null;
  let userStats: UserStats | null = null; // UserObject.Stats & co (RefreshStats)
  let statsDirty = true;
  /** GameScene.LogTime: no log out / quit before this (S.Struck, attacking a target, casting; 0 on S.Death). */
  let logTime = 0;
  let disposed = false, rafId = 0;
  /** Intelligent creatures: UserObject.IntelligentCreatures (creatures.ts). */
  const creatures = new Creatures();
  /** UserHeroObject.Stats (the hero character window, hero info panel, hero CanWearItem). */
  const heroStats = new HeroStats(hero, itemInfos, () => performance.now(), () => magic.heroMagics);
  /** PlayerObject.MountTime + MountDialog.CanRide / Ride. */
  const mountState = new MountState((f) => session.send(f),
    () => user && { id: user.id, mountType: user.mountType, ridingMount: user.ridingMount, action: user.action, fishing: user.fishing },
    () => performance.now());
  /** FishingStatusDialog state (S.FishingUpdate for the user). */
  const fishing = new Fishing(inventory, (f) => session.send(f), () => user?.id ?? null);
  /** GroupDialog roster + GameScene group handlers. */
  const group = new GroupState((f) => session.send(f), (t, ty) => chat(t, ty));
  /** GuestTradeDialog + GameScene trade handlers (the user's side is Inventory). */
  const trade = new Trade((f) => session.send(f));
  /** Rental guest side + rented-items list (the user's side is ItemChanges). */
  const rental = new Rental(itemChanges, inventory, (f) => session.send(f), (i) => gameInfo.requestItem(i), () => performance.now());
  // Phase 1c: the NPC, social and shop state the classic windows used to keep (the windows are the UI's views)
  /** NPCDialog / NPCGoodsDialog / NPCDropDialog state + GameScene's NPC handlers (GameScene.NPCID / NPCRate). */
  const npcSession = new NpcSession({ send: (f) => session.send(f), now: () => performance.now(), inventory, chat: (t) => chat(t, ChatType.System) });
  /** GameScene.RecipeInfoList. */
  const recipes = new Recipes();
  /** NPCAwakeDialog state. */
  const awakening = new Awakening({ send: (f) => session.send(f), inventory });
  /** TrustMerchantDialog state (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess). */
  const market = new Market({ send: (f) => session.send(f), now: () => performance.now(), inventory, chat: (t, ty) => chat(t, ty) });
  /** GuildDialog state + GameScene's guild handlers. */
  const guild = new GuildState({
    send: (f) => session.send(f), chat: (t, ty) => chat(t, ty), me: () => user?.name ?? "", now: () => performance.now(),
    setUserGuild: (name) => ctx.actions.setUserGuild(name), // ctx is built below; only called from handle()
    setGuildBuff: (stats) => ctx.actions.setGuildBuff(stats),
    signal,
  });
  /** UserObject.Mail / NewMail + GameScene's mail handlers. */
  const mail = new MailBox((f) => session.send(f), (t, ty) => chat(t, ty), inventory, (i) => gameInfo.requestItem(i));
  /** FriendDialog.Friends. */
  const friends = new Friends((f) => session.send(f));
  /** RelationshipDialog / MentorDialog state + GameScene's marriage, divorce and mentor handlers. */
  const relationship = new Relationship((f) => session.send(f), (t, ty) => chat(t, ty));
  const mentor = new Mentor((f) => session.send(f), (t, ty) => chat(t, ty));
  /** InspectDialog state (GameScene.PlayerInspect). */
  const inspect = new Inspect({
    send: (f) => session.send(f), now: () => performance.now(), chat: (t, ty) => chat(t, ty),
    requestItem: (i) => gameInfo.requestItem(i), group, me: () => user?.name ?? "",
  });
  /** RankingDialog lists (the details cache is per connection, like the desktop's static dictionary). */
  const rankings = new Rankings((f) => session.send(f), rankingDetails(session));
  /** GameScene.GameShopInfoList. */
  const shop = new Shop((f) => session.send(f));
  /** GuildTerritoryDialog list. */
  const territories = new Territories((f) => session.send(f));

  /** "Cannot leave game for {0} seconds" (ClientTextKeys.CannotLeaveGame, (LogTime - CMain.Time) / 1000). */
  const cannotLeave = (t: number) => `Cannot leave game for ${Math.trunc((logTime - t) / 1000)} seconds`;
  /** GameScene.LogOut / QuitGame: inside the combat lock (LogTime) the CannotLeaveGame line, else the MirMessageBox,
   *  then C.LogOut (back to character select; QuitGame's Yes closes the desktop client instead). */
  const logOut = (exit: boolean) => {
    const t = now();
    if (t < logTime) { chat(cannotLeave(t), ChatType.System); return; }
    ui.confirm(exit ? "Do you want to quit Legend of Mir?" : "Do you want to log out of Legend of Mir?", () => session.send(ClientPackets.LogOut()));
  };

  // — The UI (src/ui/gameUi.ts): live views over the state above —
  const player: PlayerState = {
    get user() { return user; }, get level() { return level; }, get hp() { return hp; }, get mp() { return mp; },
    get experience() { return experience; }, get maxExperience() { return maxExperience; }, get gender() { return gender; },
    get stats() { return userStats; }, get baseStats() { return baseStats; }, get aMode() { return aMode; }, get pMode() { return pMode; },
    get buffs() { return userBuffs; }, get guildBuffStats() { return guildBuffStats; },
  };
  const ctx: GameUiContext = {
    stage, screen, signal, send: (f) => session.send(f), now: () => performance.now(), sound, session,
    models: {
      itemInfos, gameInfo, inventory, hero, heroItems, itemChanges, magic, quests, creatures, chatLog, hudState, account, currentMap,
      itemPick, heroStats, mount: mountState, fishing, group, trade, rental,
      npcSession, recipes, awakening, market, guild, mail, friends, relationship, mentor, inspect, rankings, shop, territories,
    },
    player,
    world: {
      get map() { return map; }, get mapTitle() { return currentMap.title; }, get miniMapIndex() { return currentMap.miniMapIndex; }, lights: lightState, objects,
      mouseCell: () => (user ? view.mapLocation(mouse.x, mouse.y, user) : null),
      walkTo: (cell) => {
        if (!user) return;
        const path = findPath(map.width, map.height, (x, y) => emptyCell({ X: x, Y: y }), user.location, cell);
        if (!path || path.length === 0) { chat("Could not find a path.", ChatType.System); return; }
        autoPath = path;
      },
    },
    actions: {
      logOut,
      pickUp: () => pickUp(),
      setGuildBuff: (stats) => {
        // GameScene.GuildBuffList: a local, infinite BuffType.Guild buff while any guild buff is active
        userBuffs = userBuffs.filter((b) => b.Type !== BuffType.Guild);
        guildBuffStats = stats;
        statsDirty = true;
        if (stats && user) userBuffs.push({ Type: BuffType.Guild, Visible: false, ObjectID: user.id, expire: 0, Infinite: true, Paused: false, Stats: stats, Values: [] });
      },
      setUserGuild: (name) => { if (user) user.guildName = name; },
    },
  };
  // settings.ts uiStyle(): "Modern" falls back to the classic UI until phase 3a of the UI port (no src/ui/modern/ yet).
  // The classic windows add their own key listeners here, before game.ts's key router below (desktop key order kept).
  const ui: GameUi = uiStyle() === "Modern" ? createClassicUi(ctx) : createClassicUi(ctx);

  // Model hooks, set once here (the item hooks used to be set by the Socket / Rental / Mount window constructors)
  inventory.refuse = (text) => chat(text, ChatType.System);
  inventory.canWear = (item, slot) => {
    const info = itemInfos.get(item.ItemIndex);
    if (!info || !userStats || !user) return null; // no data yet: let the server decide
    const cur = inventory.equipment[slot] ?? null, curInfo = cur ? itemInfos.get(cur.ItemIndex) : undefined;
    const weapon = inventory.equipment[EquipmentSlot.Weapon];
    return canWearItem({ cls: user.look.class, gender, level, stats: userStats, weaponInfo: weapon ? itemInfos.get(weapon.ItemIndex) : undefined },
      item, info, cur && curInfo ? { item: cur, info: curInfo } : null);
  };
  inventory.useSlotItem = (index) => itemChanges.useSlotItem(index); // MirItemCell.UseSlotItem (sockets, mount and fishing parts)
  itemChanges.refuse = (text) => chat(text, ChatType.System);
  // MirItemCell.UseItem's riding / fishing gate (GameScene.User)
  itemChanges.userState = () => ({ ridingMount: !!user?.ridingMount, fishing: !!user?.fishing });
  heroItems.refuse = (text) => chat(text, ChatType.System);
  heroItems.ask = (text, yes) => { ui.confirm(text, yes); };
  // MirItemCell.CanWearItem with the hero as the wearer (UserHeroObject stats: heroStats.ts)
  heroItems.canWear = (item, slot) => {
    const h = hero.info, stats = heroStats.stats(), info = itemInfos.get(item.ItemIndex);
    if (!h || !stats || !info) return null; // no data yet: let the server decide
    const cur = h.equipment[slot] ?? null, curInfo = cur ? itemInfos.get(cur.ItemIndex) : undefined;
    const weapon = h.equipment[EquipmentSlot.Weapon];
    return canWearItem({ cls: h.cls, gender: h.gender, level: h.level, stats, weaponInfo: weapon ? itemInfos.get(weapon.ItemIndex) : undefined },
      item, info, cur && curInfo ? { item: cur, info: curInfo } : null);
  };
  // MapControl.UseMagic: GameScene.LogTime = CMain.Time + Globals.LogDelay on the user's casts (magic.ts sendCast)
  magic.onCast(() => { logTime = now() + LOG_DELAY; });

  /** UserObject.RefreshStats (src/game/stats.ts); the Guild buff icon is not a stat source (RefreshGuildBuffs adds those). */
  function recomputeStats(): void {
    if (!user) return;
    statsDirty = false;
    user.transformType = transformTypeFromBuffs(userBuffs); // UserObject.RefreshBuffs (TransformUpdate is not sent to the user)
    userStats = refreshStats({
      cls: user.look.class, level, inventory: inventory.inventory, equipment: inventory.equipment, infos: itemInfos,
      magics: magic.magics, buffs: userBuffs.filter((b) => b.Type !== BuffType.Guild), guildBuffStats, baseStats, hp,
    });
  }

  view.npcQuestImage = (ob) => { const icon = quests.npcQuestIcon(ob.id); return icon === null ? null : questIconImage(icon, performance.now()); };
  /** MapControl.CheckDoorOpen for a cell. */
  const doorOk = (pt: WirePoint) => pt.X < 0 || pt.Y < 0 || pt.X >= map.width || pt.Y >= map.height
    || doors.checkOpen(map.doorIndex[pt.X * map.height + pt.Y]!, now(), (i) => session.send(ClientPackets.Opendoor({ DoorIndex: i })));
  /** MapControl.Effects (map-point effects). */
  const mapEffects: Effect[] = [];
  /** SpellObjects (FireWall, PoisonCloud, ...), keyed by ObjectID. */
  const spellObjects = new Map<number, SpellObj>();
  const spellWorld: SpellWorld = {
    getObject: (id) => objects.get(id),
    validPoint: (p) => p.X >= 0 && p.Y >= 0 && p.X < map.width && p.Y < map.height
      && (map.backImage[p.X * map.height + p.Y]! & 0x20000000) === 0 && (map.frontImage[p.X * map.height + p.Y]! & 0x8000) === 0,
    user: () => user,
    now: () => performance.now(),
    playSound: play,
    addMapEffect: (e) => { mapEffects.push(e); },
  };
  spellHooks.start = (ob) => spellStart(ob, spellWorld);
  spellHooks.complete = (ob) => spellComplete(ob, spellWorld);
  const soundWorld: SoundWorld = { play: (id, loop, delay) => sound.play(id, loop, delay), stop: (id) => sound.stop(id), user: () => user, map: () => map };
  setMonsterSpecialsWorld(spellWorld, soundWorld); // MonsterObject.SetAction / ProcessFrames per-monster specials (monsterSpecials.ts)
  // Action-start sounds (MonsterObject / PlayerObject.SetAction) and shield refresh
  spellHooks.action = (ob) => {
    if (ob.race === ObjectType.Player) {
      actionRefresh(ob);
      if (ob.action === A.Struck || ob.action === A.MountStruck) { playerSound(ob, "struck", soundWorld); playerSound(ob, "flinch", soundWorld); }
    } else if (ob.race === ObjectType.Monster) {
      switch (ob.action) {
        case A.Running: monsterSound(ob, "run", soundWorld); break;
        case A.Attack1: monsterSound(ob, "attack", soundWorld); break;
        case A.Struck: monsterSound(ob, "flinch", soundWorld); monsterSound(ob, "struck", soundWorld); break;
        case A.Die: monsterSound(ob, "die", soundWorld); break;
        case A.Revive: monsterSound(ob, "revive", soundWorld); break;
      }
      monsterActionStart(ob, spellWorld, soundWorld);
    }
  };
  // Per-frame sounds (ProcessFrames): monster walk L/R on frames 1/4, the user's steps on 1/4, player attack and die on frame 1
  // GetBackStepDistance / GetFlashDashDistance: ValidPoint and no blocking object (MapControl.EmptyCell)
  moveHooks.canStand = (p) => emptyCell(p);
  spellHooks.frame = (ob) => {
    const f = ob.frameIndex;
    // PlayerObject.ProcessFrames AttackRange1 (Archer bow shot): attack sound at 1, the arrow at 5, Focus at 6
    if (ob.race === ObjectType.Player && ob.action === A.AttackRange1) {
      if (f === 1) playerSound(ob, "attack", soundWorld);
      if (f === 5) {
        const missile = createProjectile(ob, spellWorld, 1030, Lib.Magic3, true, 5, 30, 5);
        play(20000 + 121 * 10);
        if (missile.target) missile.onComplete(() => play(20000 + 121 * 10 + 2));
      }
      if (f === 6 && ob.spell === Spell.Focus) {
        ob.effects.push(new Effect(Lib.Magic3, 2730, 10, (ob.frame?.count ?? 0) * (ob.frame?.interval ?? 0), ob));
        play(20000 + 121 * 10 + 5);
      }
      return;
    }
    if (ob.race === ObjectType.Monster) {
      if (ob.action === A.Walking) { if (f === 1) monsterSound(ob, "walkL", soundWorld); else if (f === 4) monsterSound(ob, "walkR", soundWorld); }
    } else if (ob.race === ObjectType.Player) {
      const stepping = ob.action === A.Walking || ob.action === A.Running || ob.action === A.MountWalking || ob.action === A.MountRunning;
      if (stepping && ob === user && (f === 1 || f === 4)) playerSound(ob, "step", soundWorld);
      else if ((ob.action === A.Attack1 || ob.action === A.Attack2 || ob.action === A.Attack3 || ob.action === A.Attack4 || ob.action === A.MountAttack) && f === 1) playerSound(ob, "attack", soundWorld);
      else if (isFishingAction(ob.action) && f === 1) fishingFrame(ob, soundWorld); // PlayerObject.ProcessFrames: throw / pull sounds, ripples, the bite mark
      else if (ob.action === A.Die && f === 1) playerSound(ob, "die", soundWorld);
    }
  };
  /** StruckWeapon for a struck object: -2, or the attacking player's weapon (an Assassin's class weapon counts as 1). */
  const struckWeaponOf = (attackerId: number): number => {
    const a = objects.get(attackerId);
    if (!a || a.race !== ObjectType.Player) return -2;
    return a.look.class === MirClass.Assassin && a.look.weapon > -1 ? 1 : a.look.weapon;
  };
  view.actionEffects = (ob) => drawActionEffects(ob, spellWorld);
  view.spellObjects = spellObjects;
  /** GameScene.DefaultNPCID (S.DefaultNPC). */
  let defaultNpcId = 0;
  const orbSound = { play: (id: number, loop: boolean) => sound.play(id, loop), stop: (id: number) => sound.stop(id) };
  /** PlayerObject.SetEffects' element orbs from ObjectPlayer.ElementOrbEffect (4 = the mixed orb, else 1..3 single orbs). */
  function elementOrbs(ob: MapObj, effect: number): void {
    if (effect <= 0 || ob.hasElements) return;
    ob.hasElements = true;
    const orb = (base: number, type: number) => ob.effects.push(new ElementsEffect(Lib.Magic3, base, 10, 10 * 100, ob, true, type, ob.elementOrbMax, false, orbSound));
    if (effect === 4) orb(1660, 4);
    else {
      if (effect >= 1) orb(1630, 1);
      if (effect >= 2) orb(1640, 2);
      if (effect === 3) orb(1650, 3);
    }
  }
  let nextAction = 0;     // MapControl.NextAction
  let attackTime = 0;     // GameScene.AttackTime
  let canRun = false;     // GameScene.CanRun
  let lastRunTime = 0;    // GameScene.LastRunTime: the user's last step (CanRun lapses 900 ms later, as the server's step counter does)
  let nextRunTime = 0;    // GameScene.NextRunTime: no running for 2.5 s after being struck
  let inputDelay = 0;     // MapControl.InputDelay
  let pickUpTime = 0;     // GameScene.PickUpTime
  const pickUp = () => {
    if (now() <= pickUpTime) return;
    pickUpTime = now() + 200;
    session.send(ClientPackets.PickUp());
  };
  const now = () => performance.now();

  /** ChatDialog.ReceiveChat (chatLog.ts; the UI renders the lines from its `added` event). */
  function chat(text: string, type: number): void {
    chatLog.receive(text, type);
  }

  async function loadMap(fileName: string): Promise<void> {
    map = blankMap();
    try {
      rememberMap(fileName);
      const parsed = await mapCells(fileName);
      if (currentMap.fileName === fileName) map = parsed; // currentMap.handle took the name first
    } catch (e) {
      chat(`Could not load map ${fileName}: ${(e as Error).message}`, ChatType.System);
    }
  }

  function addObject(ob: MapObj): void {
    objects.get(ob.id); // replaced below (MapObject ctor removes an existing object with the same id)
    objects.set(ob.id, ob);
    ob.setAction(now(), true);
  }

  function setupUser(p: PacketOf<"UserInformation">): void {
    if (user) objects.delete(user.id);
    const u = new MapObj(p.ObjectID, ObjectType.Player, p.Location, p.Direction);
    u.isUser = true;
    u.name = p.Name;
    u.nameColour = p.NameColour;
    u.guildName = p.GuildName;
    u.look = { class: p.Class, gender: p.Gender, hair: p.Hair, armour: 0, weapon: -1, weaponEffect: 0 };
    hp = p.HP; mp = p.MP; level = p.Level;
    u.level = p.Level;
    experience = p.Experience; maxExperience = p.MaxExperience;
    gender = p.Gender;

    u.onActionStart = (q) => {
      // PlayerObject.SetAction for the user: send the packet when the action starts.
      switch (q.action) {
        case A.Walking: lastRunTime = now(); session.send(ClientPackets.Walk({ Direction: q.direction })); canRun = true; nextAction = now() + 2500; break;
        case A.Running: lastRunTime = now(); session.send(ClientPackets.Run({ Direction: q.direction })); nextAction = now() + 2500; break;
        // a Turn resets the server's step counter, so the next move must be a walk
        case A.Standing: session.send(ClientPackets.Turn({ Direction: q.direction })); nextAction = now() + 2500; canRun = false; break;
        case A.Pushed: lastRunTime = now(); inputDelay = now() + 500; canRun = false; break;
        case A.Attack1: {
          // PlayerObject.SetAction: the warrior toggles pick the attack spell
          const hasTarget = attackTargetId !== 0;
          const spell = magic.attackSpell(hasTarget, hasTargetAt(pointMove(q.location, q.direction, 2)), canHalfMoon(q.location, q.direction), canCrossHalfMoon(q.location));
          u.spell = spell;
          u.spellLevel = magic.get(spell)?.Level ?? 0;
          session.send(ClientPackets.Attack({ Direction: q.direction, Spell: spell }));
          attackTime = now() + (userStats?.attackSpeed ?? 1400 - Math.min(370, level * 14)); // GameScene.AttackTime = User.AttackSpeed
          nextAction = now() + 2500;
          break;
        }
        case A.Spell: {
          if (!q.spell) break;
          // PlayerObject.SetAction's spell switch already turned some spells into other actions (objects.ts spellAction)
          const sp = q.spell.spell, t = now();
          const magicPacket = (objectId: number, targetId = 0, location = { X: 0, Y: 0 }) => {
            session.send(ClientPackets.Magic({ ObjectID: objectId, Spell: sp, Direction: u.direction, TargetID: targetId, Location: location, SpellTargetLock: false }));
            magic.casted(); // the user's C.Magic: the combat log-out lock (MapControl.UseMagic's LogTime)
          };
          switch (u.action) {
            case A.DashL: magicPacket(u.id); nextAction = t + 2500; magic.spellTime = t + 2500; canRun = false; break; // ShoulderDash
            case A.Jump: magicPacket(u.id); nextAction = t + 800; magic.spellTime = t + 2500; canRun = false; break; // BackStep
            case A.DashAttack: magicPacket(0); nextAction = t; magic.spellTime = t + 250; canRun = false; break; // FlashDash with a jump
            case A.Harvest: // ExplosiveTrap (ArcherLayTrap: the trap sound instead of C.Harvest)
              magicPacket(u.id, q.spell.targetId, q.spell.targetPoint); nextAction = t + 1000; magic.spellTime = t + 1500;
              play(20000 + 124 * 10);
              break;
            default:
              nextAction = t + magic.sendCast(u, sp, q.spell.targetId, q.spell.targetPoint, u.direction);
              if (sp === Spell.BladeAvalanche) inputDelay = t + 1000;
              if (sp === Spell.CounterAttack) attackTime = t + (userStats?.attackSpeed ?? 1400);
          }
          break;
        }
        case A.Harvest: session.send(ClientPackets.Harvest({ Direction: q.direction })); nextAction = now() + 2500; break;
        case A.AttackRange1:
          // PlayerObject.SetAction AttackRange1: AttackTime = AttackSpeed + 200, C.RangeAttack with the queued target
          attackTime = now() + (userStats?.attackSpeed ?? 1400) + 200;
          session.send(ClientPackets.RangeAttack({ Direction: q.direction, Location: u.location, TargetID: q.spell?.targetId ?? 0, TargetLocation: q.spell?.targetPoint ?? u.location }));
          break;
        case A.DashL: case A.DashR: case A.Jump: case A.DashAttack: lastRunTime = now(); canRun = false; break;
      }
    };
    user = u;
    objects.set(u.id, u);
    u.setAction(now(), true);
    refreshUserLooks();
  }

  /** UserObject.RefreshEquipmentStats: the user's armour/weapon shapes and light come from equipped items' ItemInfo. */
  function refreshUserLooks(): void {
    if (!user) return;
    const looks = equipmentLooks(inventory.equipment, itemInfos, level, user.look.class);
    user.look = { ...user.look, armour: looks.armour, weapon: looks.weapon, weaponEffect: looks.weaponEffect };
    user.light = looks.light;
  }

  // — Packets —
  const gameDataChanges: GameDataChanges = { magics: false, shop: false };
  function handle(p: ServerPacket): void {
    if (hero.handle(p) && p.type === "HeroLevelChanged") play(SoundList.LevelUp); // GameScene.HeroLevelChanged
    if (inventory.handle(p)) { refreshUserLooks(); statsDirty = true; }
    if (itemChanges.handle(p)) { refreshUserLooks(); statsDirty = true; } // after inventory.handle (DeleteItem, bait MergeItem)
    if (heroItems.handle(p)) { refreshUserLooks(); statsDirty = true; heroStats.invalidate(); } // TakeBack / Transfer change the user's bag too
    magic.handle(p); // (the classic RentalWindow used to run just before it: no packet reaches both)
    if (STATS_PACKETS.has(p.type)) statsDirty = true;
    quests.handle(p); // reads UserInformation / LevelChanged too, so never returns early
    // The models the classic windows used to feed (phase 1b), with the old classic ui.handle early returns. Each only
    // reads packets no earlier handler of the old chain reacted to, so moving them ahead of the windows keeps the order.
    rental.handle(p); // was first in the classic ui.handle; never consumes
    chatLog.handle(p); // MirScene.NewChatItem; never consumes
    currentMap.handle(p); // MapInformation / MapChanged / TimeOfDay / MapAtmosphere; never consumes (BigMap / NpcWindow read it)
    if (account.handle(p)) return; // S.AllowObserve consumed (UserInformation / storage packets are only read)
    creatures.handle(p); // the creature packets (the classic ui.handle ignored this return: nothing else reads them)
    if (hudState.handle(p)) return; // output / notice / timers / compass / roll / browser / NPC input: consumed
    group.handle(p); // never consumes
    trade.handle(p); // never consumes
    // Phase 1c models, in the old classic chain's order (quest share and storage windows first, then relationship,
    // mentor, inspect, ranking, mail, friends, game shop, territories, guild, NPC, NPC services, big map, market). The
    // quest share and storage windows (still in ui.handle) read none of the packets the models below consume.
    if (relationship.handle(p) || mentor.handle(p) || inspect.handle(p) || rankings.handle(p) || mail.handle(p) || friends.handle(p)) return;
    if (shop.handle(p)) return; // GameShopInfo / GameShopStock (GameDataChanged re-dispatches GameShopInfo through here)
    guild.handle(p); // never consumes (the classic chain ignored the guild window's return)
    npcSession.handle(p); // NPC packets + RefineItem close; never consumes
    recipes.handle(p); // NewRecipeInfo; never consumes
    awakening.handle(p); // NPCAwakening / AwakeningNeedMaterials / AwakeningLockedItem / Awakening; never consumes
    // The UI's packets (classic: quest share, storage, the game shop's money refresh, NPC services, big map and the
    // market window's NPCResponse close, in that order); true = consumed
    if (ui.handle(p)) return;
    // After ui.handle: the classic GameShopDialog.Process money refresh ran on these before their windows consumed them
    if (territories.handle(p)) return; // GuildTerritoryPage
    if (market.handle(p)) return; // NPCMarket / NPCMarketPage / MarketFail / MarketSuccess
    if (gameInfo.handle(p)) return;
    switch (p.type) {
      case "GainExperience":
        // GameScene.GainExperience (OutputMessage)
        hudState.output(`Experience Gained ${p.Amount}`);
        experience += BigInt(p.Amount);
        break;
      case "BaseStatsInfo": baseStats = p.Stats; break;
      case "ChangeAMode": aMode = p.Mode; chat(AMODE_TEXT[p.Mode] ?? `[Mode ${p.Mode}]`, ChatType.Hint); break;
      case "ChangePMode": pMode = p.Mode; chat(PMODE_TEXT[p.Mode] ?? `[Pet mode ${p.Mode}]`, ChatType.Hint); break;
      case "LogOutSuccess": dispose(); exits.logOut(p.Characters); break;
      case "LogOutFailed": break; // GameScene.LogOutFailed: just re-enables the scene
      case "ReturnToLogin": dispose(); exits.returnToLogin(); break;
      case "LevelChanged":
        experience = p.Experience; maxExperience = p.MaxExperience;
        level = p.Level;
        if (user) user.level = p.Level;
        refreshUserLooks(); // level-based items change shape (GetRealItem)
        hudState.output("Congratulations! You have leveled up. Your HP and MP have been restored."); // GameScene.LevelChanged
        play(SoundList.LevelUp);
        break;
      case "GainedItem": {
        const info = itemInfos.get(p.Item.ItemIndex);
        hudState.output(`You gained ${info ? friendlyName(info.Name, p.Item.Count) : "an item"}.`); // GameScene.GainedItem
        break;
      }
      // GameScene.GainedGold / GainedCredit / LoseCredit (inventory.ts keeps the amounts); "{0:###,###,###} {1}"
      case "GainedGold": hudState.output(`You gained ${p.Gold > 0 ? p.Gold.toLocaleString("en-US") : ""} Gold`); play(SoundList.Gold); break;
      case "GainedCredit": if (p.Credit !== 0) { hudState.output(`You gained ${p.Credit.toLocaleString("en-US")} Credit`); play(SoundList.Gold); } break;
      case "LoseCredit": play(SoundList.Gold); break;
      case "NewItemInfo":
        itemInfos.add(p.Info);
        refreshUserLooks();
        ui.notify({ kind: "itemInfo" });
        break;
      case "PlayerUpdate": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob !== user) { ob.look = { ...ob.look, armour: p.Armour, weapon: p.Weapon, weaponEffect: p.WeaponEffect }; ob.light = p.Light; }
        break;
      }
      case "MapInformation": // MiniMap / Title / Lights / MapDarkLight: currentMap.ts
        sound.playMusic(p.Music);
        weather.set(p.WeatherParticles); // GameScene.MapInformation -> MapControl.Weather + UpdateWeather
        void loadMap(p.FileName);
        break;
      case "MapChanged": // MiniMap / Title / Lights / MapDarkLight: currentMap.ts
        autoPath = null;
        attackTargetId = 0; // MapControl.ResetMap: TargetObjectID = 0
        sound.playMusic(p.Music);
        weather.set(p.Weather); // GameScene.MapChanged -> MapControl.UpdateWeather
        for (const id of [...objects.keys()]) if (id !== user?.id) objects.delete(id);
        mapEffects.length = 0;
        doors.clear();
        spellObjects.clear();
        user?.effects.splice(0);
        user?.snap(p.Location, p.Direction);
        nextAction = 0;
        canRun = false;
        void loadMap(p.FileName);
        break;
      case "UserInformation": setupUser(p); break;
      case "UserLocation":
        nextAction = 0;
        if (!user) break;
        if (user.location.X === p.Location.X && user.location.Y === p.Location.Y && user.direction === p.Direction) break;
        user.snap(p.Location, p.Direction);
        inputDelay = now() + 400;
        break;
      case "MapAtmosphere": // GameScene.MapAtmosphere: LiveDb changed the current map's weather (lights: currentMap.ts); no reload
        if (weather.setting !== p.Weather) weather.set(p.Weather);
        break;
      case "HealthChanged": hp = p.HP; mp = p.MP; break;
      case "ObjectPlayer":
      case "ObjectHero": { // GameScene.ObjectHero: HeroObject.Load = PlayerObject.Load + OwnerName
        const ob = new MapObj(p.ObjectID, ObjectType.Player, p.Location, p.Direction);
        if (p.type === "ObjectHero") ob.ownerName = p.OwnerName;
        ob.name = p.Name; ob.nameColour = p.NameColour; ob.dead = p.Dead; ob.hidden = p.Hidden; ob.light = p.Light;
        ob.guildName = p.GuildName; ob.level = p.Level;
        ob.buffs = [...p.Buffs]; ob.poison = p.Poison; ob.currentEffect = p.Effect;
        ob.look = { class: p.Class, gender: p.Gender, hair: p.Hair, armour: p.Armour, weapon: p.Weapon, weaponEffect: p.WeaponEffect };
        ob.levelEffects = p.LevelEffects;
        ob.elementsLevel = p.ElementOrbLvl; ob.elementOrbMax = p.ElementOrbMax;
        ob.mountType = p.MountType; ob.ridingMount = p.RidingMount; ob.fishing = p.Fishing; ob.transformType = p.TransformType;
        addObject(ob);
        if (!ob.ridingMount) setEffects(ob); // PlayerObject.Load: no SetEffects while riding
        elementOrbs(ob, p.ElementOrbEffect);
        processBuffs(ob, play);
        break;
      }
      case "ObjectMonster": {
        const ob = new MapObj(p.ObjectID, ObjectType.Monster, p.Location, p.Direction);
        ob.name = p.Name; ob.nameColour = p.NameColour; ob.image = p.Image; ob.dead = p.Dead; ob.hidden = p.Hidden; ob.light = p.Light;
        ob.buffs = [...p.Buffs]; ob.poison = p.Poison; ob.ai = p.AI;
        ob.stage = p.ExtraByte; // MonsterObject.Load: Stage = ExtraByte (GreatFoxSpirit frames)
        addObject(ob);
        processBuffs(ob, play);
        // MonsterObject.Load: a standing monster announces itself and starts on a random frame
        if (ob.action === A.Standing) {
          monsterSound(ob, "appear", soundWorld);
          if (ob.frame) ob.frameIndex = Math.floor(Math.random() * ob.frame.count);
        }
        break;
      }
      case "ObjectNPC": {
        const ob = new MapObj(p.ObjectID, ObjectType.Merchant, p.Location, p.Direction);
        ob.name = p.Name; ob.nameColour = p.NameColour; ob.image = p.Image;
        ob.light = 10; // NPCObject ctor
        addObject(ob);
        break;
      }
      case "ObjectItem": {
        const ob = new MapObj(p.ObjectID, ObjectType.Item, p.Location, 0);
        ob.name = p.Name; ob.nameColour = p.NameColour; ob.image = p.Image;
        addObject(ob);
        break;
      }
      case "ObjectGold": {
        const ob = new MapObj(p.ObjectID, ObjectType.Item, p.Location, 0);
        ob.name = `${p.Gold} Gold`; // ClientTextKeys.GoldAmount
        ob.nameColour = -1;
        ob.image = p.Gold < 100 ? 112 : p.Gold < 200 ? 113 : p.Gold < 500 ? 114 : p.Gold < 1000 ? 115 : 116;
        addObject(ob);
        break;
      }
      case "ObjectEffect": objectEffect(p, spellWorld); break;
      case "Opendoor": doors.open(p.DoorIndex, p.Close, now()); break;
      // — Buffs / poison / hiding / sound —
      case "AddBuff": {
        const b = p.Buff;
        if (b.ObjectID === user?.id) {
          const ub = userBuff(b, now());
          const i = userBuffs.findIndex((x) => x.Type === b.Type);
          if (i >= 0) userBuffs[i] = ub; else userBuffs.push(ub);
        }
        if (!b.Visible || b.ObjectID <= 0) break;
        const ob = objects.get(b.ObjectID);
        if (ob && (ob.race === ObjectType.Player || ob.race === ObjectType.Monster)) {
          if (!ob.buffs.includes(b.Type)) ob.buffs.push(b.Type);
          addBuffEffect(ob, b.Type, play);
        }
        break;
      }
      case "RemoveBuff": {
        if (p.ObjectID === user?.id) userBuffs = userBuffs.filter((b) => b.Type !== p.Type);
        if (p.ObjectID <= 0) break;
        const ob = objects.get(p.ObjectID);
        if (ob) { ob.buffs = ob.buffs.filter((t) => t !== p.Type); removeBuffEffect(ob, p.Type); }
        break;
      }
      case "PauseBuff": {
        if (p.ObjectID !== user?.id) break;
        const b = userBuffs.find((x) => x.Type === p.Type);
        if (!b || b.Paused === p.Paused) break;
        b.Paused = p.Paused;
        b.expire += p.Paused ? -now() : now();
        break;
      }
      case "ObjectHidden": { const ob = objects.get(p.ObjectID); if (ob) ob.hidden = p.Hidden; break; }
      case "Poisoned":
        if (user) {
          user.poison = p.Poison;
          if (p.Poison & (PoisonType.Stun | PoisonType.Dazed | PoisonType.Frozen | PoisonType.Paralysis | PoisonType.LRParalysis)) magic.next = null; // User.ClearMagic
        }
        break;
      case "ObjectPoisoned": { const ob = objects.get(p.ObjectID); if (ob) ob.poison = p.Poison; break; }
      case "PlaySound": play(p.Sound); break;
      case "MapEffect": mapEffect(p, spellWorld); break;
      case "ObjectProjectile": objectProjectile(p, spellWorld); break;
      case "ObjectSpell": spellObjects.set(p.ObjectID, new SpellObj(p, spellWorld)); break;
      case "ObjectRemove":
        spellObjects.delete(p.ObjectID);
        if (p.ObjectID !== user?.id) objects.delete(p.ObjectID);
        break;
      case "ObjectTurn": objects.get(p.ObjectID)?.queue(A.Standing, p.Direction, p.Location); break;
      case "ObjectWalk": objects.get(p.ObjectID)?.queue(A.Walking, p.Direction, p.Location); break;
      case "ObjectRun": objects.get(p.ObjectID)?.queue(A.Running, p.Direction, p.Location); break;
      case "ObjectAttack":
      { // GameScene.ObjectAttack: a monster's Type 1-4 is Attack2-5
        const atk = objects.get(p.ObjectID);
        const action = atk?.race === ObjectType.Player ? A.Attack1 : ([A.Attack1, A.Attack2, A.Attack3, A.Attack4, A.Attack5][p.Type] ?? A.Attack1);
        atk?.queue(action, p.Direction, p.Location, { spell: p.Spell, targetId: 0, targetPoint: p.Location, cast: false, level: p.Level, secondaryTargetIds: [] });
      }
        break;
      case "ObjectMagic": {
        // GameScene.ObjectMagic: the user's own casts only when SelfBroadcast
        if (!p.SelfBroadcast && p.ObjectID === user?.id) break;
        objects.get(p.ObjectID)?.queue(A.Spell, p.Direction, p.Location,
          { spell: p.Spell, targetId: p.TargetID, targetPoint: p.Target, cast: p.Cast, level: p.Level, secondaryTargetIds: p.SecondaryTargetIDs });
        break;
      }
      case "DamageIndicator": {
        // GameScene.DamageIndicator (Settings.DisplayDamage, at most 10 per object), drawn in the WoOOLcraft style
        const ob = objects.get(p.ObjectID);
        if (!ob || !gameSettings.DisplayDamage) break;
        const t = now();
        if (p.Type === DamageType.Critical) { critAt.set(p.ObjectID, t); break; }
        let list = damageNumbers.get(p.ObjectID);
        if (!list) damageNumbers.set(p.ObjectID, list = []);
        if (list.length >= MAX_DAMAGE_NUMBERS) break;
        const isUserTarget = ob === user;
        if (p.Type === DamageType.Miss) list.push(makeDamageNumber({ value: 0, text: "Miss", isUserTarget, startTime: t }));
        else if (p.Damage !== 0) {
          const crit = p.Damage < 0 && t - (critAt.get(p.ObjectID) ?? -Infinity) < 500;
          critAt.delete(p.ObjectID);
          if (crit) view.atmosphere.crit(ob); // web atmosphere: a short flash on the target
          list.push(makeDamageNumber({ value: p.Damage, critical: crit, isUserTarget, startTime: t }));
        }
        break;
      }
      case "ObjectStruck": {
        // GameScene.ObjectStruck: no Struck while the object is behind (SkipFrames) or already has one queued last
        const ob = objects.get(p.ObjectID);
        if (!ob || ob === user || ob.skipFrames(true) || ob.feed[ob.feed.length - 1]?.action === A.Struck) break;
        ob.queue(A.Struck, p.Direction, p.Location, undefined, struckWeaponOf(p.AttackerID));
        break;
      }
      case "Struck":
        logTime = now() + LOG_DELAY; // GameScene.Struck: LogTime = CMain.Time + Globals.LogDelay
        if (!user) break;
        // GameScene.Struck: being hit interrupts the user's own Reincarnation cast
        if (user.reincarnationStopTime > now()) session.send(ClientPackets.CancelReincarnation());
        nextRunTime = now() + 2500;
        // one Struck at a time (the server sends one per PvP hit), at the end of whatever is already queued
        if (user.feed.some((q) => q.action === A.Struck)) break;
        {
          const last = user.feed[user.feed.length - 1];
          user.queue(A.Struck, last?.direction ?? user.direction, last?.location ?? user.location, undefined, struckWeaponOf(p.AttackerID));
        }
        break;
      // — Section 5: reincarnation (GameScene.ProcessPacket CancelReincarnation / RequestReincarnation) —
      case "CancelReincarnation": if (user) user.reincarnationStopTime = 0; break;
      case "RequestReincarnation": if (user?.dead && user.action === A.Dead) ui.reincarnation(); break;
      // — Section 5: mounts, transform, fishing (GameScene.MountUpdate / TransformUpdate / FishingUpdate) —
      case "MountUpdate": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob.race === ObjectType.Player) {
          ob.mountUpdate(p, now());
          if (!ob.ridingMount) setEffects(ob);
          playerSound(ob, "mount", soundWorld);
        }
        if (p.ObjectID === user?.id) { canRun = false; statsDirty = true; }
        mountState.handle(p); // PlayerObject.MountTime for the user (MountDialog)
        break;
      }
      // — Section 6: the web admin's LiveDb push (GameScene.GameDataChanged) —
      case "GameDataChanged":
        applyGameDataChanged(p, {
          dispatch: (q) => handle(q),
          userMagics: () => magic.magics,
          heroMagics: () => magic.heroMagics,
          shopItems: () => shop.editableItems(),
          invalidateMap: (m) => gameInfo.invalidateMap(m),
        }, gameDataChanges);
        if (p.Last) { // the refresh after the last chunk: stats, skill bars, an open shop
          statsDirty = true;
          heroStats.invalidate();
          ui.notify({ kind: "gameDataApplied", shopChanged: gameDataChanges.shop }); // an open shop
          gameDataChanges.magics = false; gameDataChanges.shop = false;
        }
        break;
      case "IntelligentCreaturePickup": intelligentCreaturePickupSound(objects.get(p.ObjectID), soundWorld); break; // GameScene.IntelligentCreaturePickup
      case "TransformUpdate": { const ob = objects.get(p.ObjectID); if (ob?.race === ObjectType.Player) ob.transformType = p.TransformType; break; }
      case "FishingUpdate":
        objects.get(p.ObjectID)?.fishingUpdate(p);
        if (user) fishing.handle(p); // FishingDialog / FishingStatusDialog state
        break;
      case "ObjectDied": {
        const ob = objects.get(p.ObjectID);
        // GameScene.ObjectDied: Dead at once (untargetable while the queued Die waits its turn)
        if (ob) { if (p.Type === 0) { ob.queue(A.Die, p.Direction, p.Location); ob.dead = true; } else objects.delete(p.ObjectID); }
        break;
      }
      case "Death":
        if (user) { user.dead = true; user.snap(p.Location, p.Direction); }
        logTime = 0; // GameScene.Death: LogTime = 0
        ui.revive(true);
        break;
      case "Revived":
        if (user) { user.dead = false; user.snap(user.location, user.direction); }
        ui.revive(false);
        break;
      case "ObjectRevived": { const ob = objects.get(p.ObjectID); if (ob) { ob.dead = false; ob.snap(ob.location, ob.direction); } break; }
      case "ObjectHealth": { const ob = objects.get(p.ObjectID); if (ob) { ob.healthPercent = p.Percent; ob.healthExpire = now() + p.Expire * 1000; } break; }
      case "ObjectPushed": objects.get(p.ObjectID)?.queue(A.Pushed, p.Direction, p.Location); break;
      case "Pushed": if (user) user.queue(A.Pushed, p.Direction, p.Location); break;
      case "ObjectTeleportOut": if (p.ObjectID !== user?.id) objects.delete(p.ObjectID); break;
      // — Section 2: objects and class moves (GameScene handlers of the same names) —
      case "ObjectLeveled": { const ob = objects.get(p.ObjectID); if (ob) { ob.effects.push(new Effect(Lib.Magic2, 1180, 16, 2500, ob)); play(SoundList.LevelUp); } break; }
      case "ObjectName": { const ob = objects.get(p.ObjectID); if (ob && ob !== user) ob.name = p.Name; break; }
      case "ObjectHide": { const ob = objects.get(p.ObjectID); ob?.queue(A.Hide, ob.direction, ob.location); break; }
      case "ObjectShow": { const ob = objects.get(p.ObjectID); ob?.queue(A.Show, ob.direction, ob.location); break; }
      case "TeleportIn": if (user) { user.effects.push(new Effect(Lib.Magic, 260, 10, 500, user)); play(SoundList.Teleport); } break;
      case "ObjectSitDown": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob !== user && ob.race === ObjectType.Monster) { ob.sitDown = p.Sitting; ob.queue(A.SitDown, p.Direction, p.Location); }
        break;
      }
      case "ObjectHarvest": { const ob = objects.get(p.ObjectID); ob?.queue(A.Harvest, ob.direction, ob.location); break; }
      case "ObjectHarvested": { const ob = objects.get(p.ObjectID); ob?.queue(A.Skeleton, ob.direction, ob.location); break; }
      case "ObjectMana": { const ob = objects.get(p.ObjectID); if (ob) ob.percentMana = p.Percent; break; }
      case "ObjectLevelEffects": { const ob = objects.get(p.ObjectID); if (ob && ob.race === ObjectType.Player) ob.levelEffects = p.LevelEffects; break; }
      case "ObjectDeco": { const ob = new MapObj(p.ObjectID, ObjectType.Deco, p.Location, 0); ob.image = p.Image; addObject(ob); break; } // DecoObject.Load
      case "ObjectSneaking": break; // GameScene.ObjectSneaking: the desktop ignores it too
      case "DefaultNPC": defaultNpcId = p.ObjectID; break;
      case "NPCImageUpdate": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob.race === ObjectType.Merchant) { ob.image = p.Image; ob.colour = p.Colour; ob.setAction(now(), true); } // NPCObject.LoadLibrary
        break;
      }
      case "RangeAttack": if (user) { user.targetId = p.TargetID; user.targetPoint = { ...p.Target }; user.spell = p.Spell; } break;
      case "ObjectRangeAttack": {
        if (p.ObjectID === user?.id) break;
        const ob = objects.get(p.ObjectID);
        if (!ob) break;
        const action = ob.race !== ObjectType.Player && p.Type === 1 ? A.AttackRange2 : ob.race !== ObjectType.Player && p.Type === 2 ? A.AttackRange3 : A.AttackRange1;
        ob.queue(action, p.Direction, p.Location, { spell: p.Spell, targetId: p.TargetID, targetPoint: { ...p.Target }, cast: false, level: p.Level, secondaryTargetIds: [] });
        break;
      }
      case "UserDash": {
        if (!user) break;
        if (user.direction === p.Direction && user.location.X === p.Location.X && user.location.Y === p.Location.Y) { nextAction = 0; break; }
        // alternate DashL / DashR: the last queued dash wins over the current one
        let action: number = user.action === A.DashL ? A.DashR : A.DashL;
        for (let i = user.feed.length - 1; i >= 0; i--) {
          const a = user.feed[i]!.action;
          if (a === A.DashR) { action = A.DashL; break; }
          if (a === A.DashL) { action = A.DashR; break; }
        }
        user.queue(action, p.Direction, p.Location);
        break;
      }
      case "ObjectDash": {
        if (p.ObjectID === user?.id) break;
        const ob = objects.get(p.ObjectID);
        if (ob) ob.queue(ob.feed.length > 0 && ob.feed[ob.feed.length - 1]!.action === A.DashL ? A.DashR : A.DashL, p.Direction, p.Location);
        break;
      }
      case "UserDashFail": nextAction = 0; user?.queue(A.DashFail, p.Direction, p.Location); break;
      case "ObjectDashFail": { if (p.ObjectID === user?.id) break; objects.get(p.ObjectID)?.queue(A.DashFail, p.Direction, p.Location); break; }
      case "UserBackStep": case "UserDashAttack": {
        if (!user) break;
        if (user.direction === p.Direction && user.location.X === p.Location.X && user.location.Y === p.Location.Y) { nextAction = 0; break; }
        user.queue(p.type === "UserBackStep" ? A.Jump : A.DashAttack, p.Direction, p.Location);
        break;
      }
      case "ObjectBackStep": case "ObjectDashAttack": {
        if (p.ObjectID === user?.id) break;
        const ob = objects.get(p.ObjectID);
        if (ob) { ob.jumpDistance = p.Distance; ob.queue(p.type === "ObjectBackStep" ? A.Jump : A.DashAttack, p.Direction, p.Location); }
        break;
      }
      case "UserAttackMove": {
        // GameScene.UserAttackMove (SlashingBurst): put the user on the cell, drop queued actions except pushes, stand there
        nextAction = 0;
        const u = user;
        if (!u || (u.location.X === p.Location.X && u.location.Y === p.Location.Y && u.direction === p.Direction)) break;
        u.location = { ...p.Location };
        inputDelay = now() + 400;
        if (u.dead) break;
        magic.next = null;
        u.feed.splice(0, u.feed.length, ...u.feed.filter((q) => q.action === A.Pushed));
        u.setAction(now());
        u.queue(A.Standing, p.Direction, p.Location);
        break;
      }
      case "InTrapRock": if (user) user.inTrapRock = p.Trapped; break;
      case "RemoveDelayedExplosion": {
        const id = DelayedExplosionEffect.getOwnerEffectID(p.ObjectID);
        if (id >= 0) DelayedExplosionEffect.effectlist[id]!.remove();
        break;
      }
      case "SetConcentration": {
        const ob = objects.get(p.ObjectID);
        if (!ob || ob.race !== ObjectType.Player) break;
        ob.concentrating = p.Enabled;
        ob.concentrateInterrupted = p.Interrupted;
        if (p.Enabled && !p.Interrupted && InterruptionEffect.getOwnerEffectID(ob.id) < 0) {
          ob.effects.push(new InterruptionEffect(Lib.Magic3, 1860, 8, 8 * 100, ob, true));
          play(20000 + 129 * 10);
        }
        break;
      }
      case "SetElemental": {
        const ob = objects.get(p.ObjectID);
        if (!ob || ob.race !== ObjectType.Player) break;
        ob.hasElements = p.Enabled;
        ob.elementCasted = p.Casted && user?.id !== p.ObjectID;
        ob.elementsLevel = p.Value;
        if (p.Enabled && p.ElementType > 0) {
          const t = p.ElementType;
          ob.effects.push(new ElementsEffect(Lib.Magic3, 1630 + (t - 1) * 10, 10, 10 * 100, ob, true, 1 + (t - 1), p.ExpLast,
            user?.id === p.ObjectID && (t === 4 || t === 3), orbSound));
        }
        break;
      }
      case "SetBindingShot": {
        // GameScene.SetBindingShot: cast -> dropped (repeats until the bind ends) -> fall, with the net sounds
        if (p.ObjectID === user?.id) break;
        const ob = objects.get(p.ObjectID);
        if (!ob || ob.race !== ObjectType.Monster) break;
        const netCast = new TrackableEffect(new Effect(Lib.MagicC, 0, 8, 700, ob), "BindingShotDrop");
        const dropped = new Effect(Lib.MagicC, 7, 1, 1000, ob);
        dropped.repeat = true;
        dropped.repeatUntil = now() + (Number(p.Value) - 1500);
        const netDropped = new TrackableEffect(dropped, "BindingShotDrop");
        const netFall = new TrackableEffect(new Effect(Lib.MagicC, 8, 8, 700, ob), "BindingShotFall");
        netDropped.onComplete(() => { play(20000 + 130 * 10 + 6); ob.effects.push(netFall); });
        netCast.onComplete(() => { play(20000 + 130 * 10 + 5); ob.effects.push(netDropped); });
        ob.effects.push(netCast);
        break;
      }
      // GameScene.ColourChanged / ObjectColourChanged / ObjectGuildNameChanged (guild, PK and war name colours come from the server)
      case "ColourChanged": if (user) user.nameColour = p.NameColour; break;
      case "ObjectColourChanged": { const ob = objects.get(p.ObjectID); if (ob && ob !== user) ob.nameColour = p.NameColour; break; }
      case "ObjectGuildNameChanged": { const ob = objects.get(p.ObjectID); if (ob && ob !== user) ob.guildName = p.GuildName; break; }
      case "Chat": chat(p.Message, p.Type); break;
      case "ObjectChat": {
        chat(p.Text, p.Type);
        const ob = objects.get(p.ObjectID);
        if (ob) { ob.chatText = p.Text.replace(CHAT_LINK, "$1"); ob.chatUntil = now() + 6000; }
        break;
      }
    }
  }

  // — Input —
  let mouse = { x: width / 2, y: height / 2 };
  let buttons = 0;
  let shift = false;
  const toCanvas = (e: MouseEvent) => {
    const r = canvas.getBoundingClientRect();
    return { x: ((e.clientX - r.left) * width) / r.width, y: ((e.clientY - r.top) * height) / r.height };
  };
  canvas.addEventListener("contextmenu", (e) => e.preventDefault(), { signal });
  canvas.addEventListener("mousedown", (e) => {
    mouse = toCanvas(e);
    ui.blurChat();
    // MapControl.OnMouseClick: Ctrl + right-click on another player inspects them
    if (e.button === 2 && e.ctrlKey && user) {
      const ob = mouseObject();
      if (ob && ob.race === ObjectType.Player && ob !== user) { ui.command({ kind: "inspect", objectId: ob.id }); return; }
    }
    // A picked item or gold: a click on the map drops it (GameScene: SelectedCell / PickedUpGold + map click; the UI's)
    if (ui.mapMouseDown(e)) return;
    // MapControl.OnMouseDown (left): MapObject.TargetObject = the live mouse object, unless an item, an NPC or a
    // monster with AI 64 / 70; anything else clears it. CheckInput melees it whenever it is adjacent.
    if (e.button === 0 && user) {
      const mo = mouseObject();
      attackTargetId = mo && !mo.dead && mo.race !== ObjectType.Item && mo.race !== ObjectType.Merchant
        && !(mo.race === ObjectType.Monster && (mo.ai === 64 || mo.ai === 70)) ? mo.id : 0;
    }
    // Left-click on an NPC talks to it (MapControl.OnMouseClick -> C.CallNPC "[@Main]")
    if (e.button === 0 && user) {
      const target = mouseObject();
      if (target && target.race === ObjectType.Merchant) { ui.command({ kind: "talk", objectId: target.id, name: target.name }); return; }
    }
    buttons = e.buttons; shift = e.shiftKey;
  }, { signal });
  window.addEventListener("mouseup", (e) => { buttons = e.buttons; if (!(buttons & 2)) canRun = false; }, { signal });
  canvas.addEventListener("mousemove", (e) => { mouse = toCanvas(e); buttons = e.buttons; shift = e.shiftKey; }, { signal });
  // One key router for the scene (input.ts), created here, after the UI's windows added their own listeners. The UI's
  // gameplay keys (classic: mount ride, creature pickup) run before game.ts's, as their window listeners did.
  const input = createInputRouter(signal);
  ui.bindInput(input);
  input.register("gameplay", (e) => {
    // ChatDialog opens itself on Enter / '@' / '!' / Space / '/'. While typing, only F1-F12 and Tab reach the bindings
    // below (ChatTextBox_KeyDown -> CMain_KeyDown; chatDialog.ts re-dispatches them).
    if (ui.typing && !/^F([1-9]|1[0-2])$|^Tab$/.test(e.key)) return;
    // KeybindOptions.Logout (Alt+X) / Exit (Alt+Q): MenuDialog's Log Out / Exit
    if (matches(e, KeybindOptions.Logout) || matches(e, KeybindOptions.Exit)) { e.preventDefault(); logOut(matches(e, KeybindOptions.Exit)); return; }
    // A Ctrl / Alt game binding (desktop "don't care" modifiers: Ctrl+F friends, Ctrl+P group, ...) must not also reach the
    // browser (find, print, address bar). Text inputs keep their keys; Ctrl+C with a text selection still copies.
    const active = document.activeElement;
    const typing = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active instanceof HTMLSelectElement;
    if (!typing && (e.ctrlKey || e.altKey) && !(e.ctrlKey && e.key.toLowerCase() === "c" && (window.getSelection()?.toString() ?? "") !== "")
      && matchesAnyBinding(e)) e.preventDefault();
    // GameScene.GameScene_KeyDown (Client/MirScenes/GameScene.cs) through the central key map (keybinds.ts)
    // Ctrl+H: attack mode, Ctrl+A: pet mode (KeybindOptions.ChangeAttackmode / ChangePetmode; 300 ms apart)
    const toggleAMode = matches(e, KeybindOptions.ChangeAttackmode), togglePMode = !toggleAMode && matches(e, KeybindOptions.ChangePetmode);
    if (toggleAMode || togglePMode) {
      e.preventDefault();
      if (now() > modeTime) {
        modeTime = now() + 300;
        if (toggleAMode) session.send(ClientPackets.ChangeAMode({ Mode: AMODE_NEXT[aMode] ?? AttackMode.Peace }));
        else session.send(ClientPackets.ChangePMode({ Mode: PMODE_NEXT[pMode] ?? PetMode.Both }));
      }
      return;
    }
    // KeybindOptions.Attackmode* / Petmode* (unbound by default): set the mode directly
    for (const [opt, mode] of SET_AMODE) if (matches(e, opt)) { e.preventDefault(); session.send(ClientPackets.ChangeAMode({ Mode: mode })); return; }
    for (const [opt, mode] of SET_PMODE) if (matches(e, opt)) { e.preventDefault(); session.send(ClientPackets.ChangePMode({ Mode: mode })); return; }
    // T: trade request (KeybindOptions.Trade)
    if (matches(e, KeybindOptions.Trade) && !(document.activeElement instanceof HTMLTextAreaElement)) ui.command({ kind: "trade" });
    // Tab: pick up (KeybindOptions.Pickup)
    if (matches(e, KeybindOptions.Pickup)) { e.preventDefault(); pickUp(); }
    // KeybindOptions.Ranking (K) / Minimap (V)
    if (matches(e, KeybindOptions.Ranking)) ui.command({ kind: "toggle", window: "ranking" });
    if (matches(e, KeybindOptions.Minimap)) ui.command({ kind: "toggle", window: "miniMap" });
    // Magic keys: Bar1Skill1-8 = keys 1-8 (F1-F8), Bar2Skill1-8 = 9-16 (Ctrl+F1-F8), HeroSkill1-8 = 17-24 (Shift+F1-F8)
    for (let i = 0; i < 24; i++) {
      const opt = (i < 16 ? KeybindOptions.Bar1Skill1 + i : KeybindOptions.HeroSkill1 + i - 16) as KeybindOption;
      if (!matches(e, opt)) continue;
      e.preventDefault();
      const loc = user ? view.mapLocation(mouse.x, mouse.y, user) : null;
      // HeroSkill: GameScene.UseSpell(17..24), without the user's riding / fishing gate
      if (user && loc && i >= 16) magic.useHeroSpell(i + 1, loc, mouseObject(), mouseDirection());
      else if (user && loc && canUseSpell(user)) magic.useSpell(i + 1, loc, mouseObject(), mouseDirection());
      return;
    }
    // Belt keys 1-6 / NumPad1-6 (KeybindOptions.Belt1..6 / Belt1Alt..6Alt -> BeltDialog.Grid[i].UseItem)
    for (let i = 0; i < BELT_IDX; i++) {
      if (matchesAny(e, (KeybindOptions.Belt1 + i * 2) as KeybindOption, (KeybindOptions.Belt1Alt + i * 2) as KeybindOption)) inventory.use(i, now());
    }
  });
  const SET_AMODE: [KeybindOption, AttackMode][] = [[KeybindOptions.AttackmodePeace, AttackMode.Peace], [KeybindOptions.AttackmodeGroup, AttackMode.Group],
    [KeybindOptions.AttackmodeGuild, AttackMode.Guild], [KeybindOptions.AttackmodeEnemyguild, AttackMode.EnemyGuild],
    [KeybindOptions.AttackmodeRedbrown, AttackMode.RedBrown], [KeybindOptions.AttackmodeAll, AttackMode.All]];
  const SET_PMODE: [KeybindOption, PetMode][] = [[KeybindOptions.PetmodeBoth, PetMode.Both], [KeybindOptions.PetmodeMoveonly, PetMode.MoveOnly],
    [KeybindOptions.PetmodeAttackonly, PetMode.AttackOnly], [KeybindOptions.PetmodeNone, PetMode.None], [KeybindOptions.PetmodeFocusMasterTarget, PetMode.FocusMasterTarget]];

  /** MapControl.MouseDirection: the 8-way direction of the cursor from the user's cell. */
  function mouseDirection(): number {
    const cx = view.offSetX * CELL_W + CELL_W / 2, cy = view.offSetY * CELL_H + CELL_H / 2;
    const px = Math.trunc(mouse.x / CELL_W), py = Math.trunc(mouse.y / CELL_H);
    if (Math.abs(px - view.offSetX) <= 2 && Math.abs(py - view.offSetY) <= 2) return directionFromPoint({ X: view.offSetX, Y: view.offSetY }, { X: px, Y: py });
    let angle = (Math.atan2(mouse.x - cx, cy - mouse.y) * 180) / Math.PI;
    if (angle < 0) angle += 360;
    angle += 22.5;
    if (angle >= 360) angle -= 360;
    return Math.trunc(angle / 45) & 7;
  }

  /** MapControl.HasTarget: a blocking (live, non-item) object on the cell. */
  function hasTargetAt(p: WirePoint): boolean {
    for (const ob of objects.values()) if (!ob.dead && ob.race !== ObjectType.Item && ob.location.X === p.X && ob.location.Y === p.Y) return true;
    return false;
  }
  /** MapControl.CanHalfMoon: a target on any of the 4 cells from the previous to the next-next direction. */
  function canHalfMoon(p: WirePoint, d: number): boolean {
    d = (d + 7) & 7;
    for (let i = 0; i < 4; i++) { if (hasTargetAt(pointMove(p, d, 1))) return true; d = (d + 1) & 7; }
    return false;
  }
  function canCrossHalfMoon(p: WirePoint): boolean {
    for (let d = 0; d < 8; d++) if (hasTargetAt(pointMove(p, d, 1))) return true;
    return false;
  }
  /** MapObject.TargetObjectID: set on a left mouse-down (the mouse object), melee'd by CheckInput when adjacent; also
   *  the attack spell choice's "has target". */
  let attackTargetId = 0;

  /** MapControl.CanWalk(dir, out dir): `dir` when its cell is empty, else NextDir, else PreviousDir; -1 when none or
   *  while InTrapRock. This side-step is what walks the user around an object while a button is held. */
  function canWalkDir(u: MapObj, dir: number): number {
    if (u.inTrapRock) return -1;
    for (const d of [dir, (dir + 1) & 7, (dir + 7) & 7]) if (emptyCell(pointMove(u.location, d, 1))) return d;
    return -1;
  }

  function emptyCell(pt: WirePoint): boolean {
    if (pt.X < 0 || pt.Y < 0 || pt.X >= map.width || pt.Y >= map.height) return false;
    const i = pt.X * map.height + pt.Y;
    if ((map.backImage[i] & 0x20000000) !== 0 || (map.frontImage[i] & 0x8000) !== 0) return false;
    for (const ob of objects.values()) if (!ob.dead && ob !== user && ob.race !== ObjectType.Item && ob.race !== ObjectType.Deco && ob.location.X === pt.X && ob.location.Y === pt.Y) return false;
    return true;
  }

  /** MapControl.CheckInput's AutoPath step: re-plan to the path's end, then walk (or run) one step along it. */
  function autoPathStep(u: MapObj): void {
    if (!autoPath || autoPath.length === 0) { autoPath = null; return; }
    const end = autoPath[autoPath.length - 1]!;
    const path = findPath(map.width, map.height, (x, y) => emptyCell({ X: x, Y: y }), u.location, end);
    if (!path || path.length === 0) { autoPath = null; return; }
    autoPath = path;
    // drop the nodes up to (and including) the user's own cell
    const here = autoPath.findIndex((n) => n.X === u.location.X && n.Y === u.location.Y);
    if (here >= 0) autoPath.splice(0, here + 1);
    if (autoPath.length === 0) { autoPath = null; return; }
    const next = autoPath[0]!;
    const dir = directionFromPoint(u.location, next);
    if (autoPath.length > runDistance(u.ridingMount) - 1 && tryRun(u, dir)) return;
    if (emptyCell(next) && doorOk(next)) { u.queue(A.Walking, dir, next); canRun = true; }
  }

  /** MapControl.CanRun + the run step: 2 cells (3 while riding), every one empty and passable. */
  function tryRun(u: MapObj, dir: number): boolean {
    if (!canRun || now() <= nextRunTime || hp < 10) return false;
    const n = runDistance(u.ridingMount);
    for (let i = 1; i <= n; i++) { const c = pointMove(u.location, dir, i); if (!emptyCell(c) || !doorOk(c)) return false; }
    u.queue(A.Running, dir, pointMove(u.location, dir, n));
    return true;
  }

  function checkInput(t: number): void {
    const u = user;
    if (!u || u.dead || ui.typing) return;
    if (u.fishing) return; // MapControl.CheckInput: no input while fishing
    if (t < nextAction || t < inputDelay) return;
    // GameScene.CheckInput: nothing new while Blizzard / Reincarnation is still being cast
    if (t < u.blizzardStopTime || t < u.reincarnationStopTime) return;
    const idle = isIdleAction(u.action) && u.feed.length === 0;
    if (!idle) return;
    const archerBow = canRangeAttack(u.look.class === MirClass.Archer && u.hasClassWeapon, u); // a riding archer falls back to melee
    const rideAttack = canRideAttack(u.ridingMount, inventory.equipment[EquipmentSlot.Mount]);
    const dazed = (u.poison & PoisonType.Dazed) !== 0;
    const inRange = (ob: MapObj) => Math.max(Math.abs(ob.location.X - u.location.X), Math.abs(ob.location.Y - u.location.Y)) <= MAX_ATTACK_RANGE;
    const tooFar = () => { if (t >= outputDelay) { outputDelay = t + 1000; hudState.output("Target is too far."); } };
    const shoot = (direction: number, targetId: number, point: WirePoint) =>
      u.queue(A.AttackRange1, direction, u.location, { spell: Spell.None, targetId, targetPoint: { ...point }, cast: false, level: 0, secondaryTargetIds: [] });

    // MapControl.CheckInput, TargetObject first (button held or not): a monster (not a pet, "Name(Owner)"), or a
    // pet / player with Shift, is shot by an Archer with a bow in range, else melee'd once adjacent.
    let tgt = objects.get(attackTargetId);
    if (tgt && tgt.race === ObjectType.Monster && tgt.ai === 64) { attackTargetId = 0; tgt = undefined; } // MapControl.Process
    if (tgt && !tgt.dead) {
      const pet = tgt.name.endsWith(")");
      if (((pet || tgt.race === ObjectType.Player) && shiftHeld) || (!pet && tgt.race === ObjectType.Monster)) {
        logTime = t + LOG_DELAY; // MapControl.CheckInput: GameScene.LogTime = CMain.Time + Globals.LogDelay
        if (archerBow) {
          if (!inRange(tgt)) tooFar();
          else if (t > attackTime) { shoot(directionFromPoint(u.location, tgt.location), tgt.id, tgt.location); return; }
        } else if (Math.max(Math.abs(tgt.location.X - u.location.X), Math.abs(tgt.location.Y - u.location.Y)) <= 1) {
          if (t > attackTime && rideAttack && !dazed) { u.queue(A.Attack1, directionFromPoint(u.location, tgt.location), u.location); return; }
        } else if (!(buttons & 2)) {
          // Walk to the target (port addition from the WoOOLcraft client's "walk-to-target"; Crystal's desktop only walks
          // while the left button is held): straight at it, else CanWalk's side-step, else turn towards it. The held
          // right button (run) wins, any left mouse-down re-targets, a lost or dead target stops it.
          const toward = directionFromPoint(u.location, tgt.location);
          const wd = canWalkDir(u, toward);
          if (wd >= 0 && doorOk(pointMove(u.location, wd, 1))) { u.queue(A.Walking, wd, pointMove(u.location, wd, 1)); return; }
          if (toward !== u.direction) { u.queue(A.Standing, toward, u.location); return; }
          if (!buttons) return;
        }
      }
    }

    if (!buttons) { if (autoPath) autoPathStep(u); return; }
    autoPath = null; // MapControl.OnMouseClick: any map click ends AutoPath

    let dir = mouseDirection();
    const cell = view.mapLocation(mouse.x, mouse.y, u);
    const target = mouseObject();
    /** CanWalk(dir, out dir) + CheckDoorOpen -> Walking, else a turn to the (side-stepped) direction. */
    const walkOrTurn = (): boolean => {
      const wd = canWalkDir(u, dir);
      if (wd >= 0) {
        dir = wd;
        const to = pointMove(u.location, dir, 1);
        if (doorOk(to)) { u.queue(A.Walking, dir, to); return true; }
      }
      if (dir !== u.direction) { u.queue(A.Standing, dir, u.location); return true; }
      return false;
    };

    if (buttons & 1) {
      if (target && (target.race === ObjectType.Merchant || (target.race === ObjectType.Player && target !== u))) return;
      if (target && target.race === ObjectType.Monster && target.ai === 70) return;
      // MapControl.CheckInput: Alt + left = harvest a corpse
      if (altHeld && canHarvest(u)) { u.queue(A.Harvest, dir, u.location); return; }
      if (shift) {
        if (t <= attackTime || !rideAttack) return;
        // Shift + left with a bow: a shot towards the mouse (at the object under it when in range)
        if (archerBow && !dazed) {
          const ob = target && (target.race === ObjectType.Monster || target.race === ObjectType.Player) ? target : null;
          if (ob && !inRange(ob)) { tooFar(); return; }
          shoot(dir, ob?.id ?? 0, pointMove(u.location, dir, 9));
          return;
        }
        if (dazed) return;
        u.queue(A.Attack1, dir, u.location);
        return;
      }
      // A monster with a bow: the ranged attack (ArcherTest - range attack)
      if (target && target.race === ObjectType.Monster && archerBow && tgt && !tgt.dead) {
        if (!inRange(target)) tooFar();
        else if (t > attackTime) shoot(dir, tgt.id, tgt.location);
        return;
      }
      // Clicking the user's own cell picks up (MapControl.CheckInput)
      if (cell.X === u.location.X && cell.Y === u.location.Y) { pickUp(); return; }
      if (walkOrTurn()) return;
      // MapControl.CheckInput: facing water with a rod casts out (GameScene.CanFish)
      if (canFish(u, dir, map, t)) { u.fishingTime = t; session.send(ClientPackets.FishingCast({ CastOut: true })); return; }
    } else if (buttons & 2) {
      // MapControl.CheckInput: the mouse within 2 cells only turns the user
      if (Math.max(Math.abs(cell.X - u.location.X), Math.abs(cell.Y - u.location.Y)) <= 2) {
        if (dir !== u.direction) u.queue(A.Standing, dir, u.location);
        return;
      }
      if (!u.inTrapRock && tryRun(u, dir)) return;
      walkOrTurn();
    }
  }

  // — Frame loop —
  let animationCount = 0, nextAnimation = 0, frames = 0, fps = 0, fpsAt = now();

  function drawOverlay(t: number): void {
    g2.clearRect(0, 0, width, height);
    if (!user) return;
    g2.font = "12px 'Segoe UI', sans-serif";
    g2.textAlign = "center";
    g2.lineWidth = 3;
    g2.strokeStyle = "#000";
    // Ground item names (Settings.DropView, on by default): outlined; the hovered one boxed (ItemObject.DrawName)
    g2.textBaseline = "middle";
    for (const ob of objects.values()) {
      if (ob.race !== ObjectType.Item) continue;
      if (!gameSettings.DropView && ob.id !== hoverId) continue; // CreateTexture: Settings.DropView, else only the hovered item
      const r = view.itemRect(ob, user);
      if (!r || r.x < -100 || r.y < -100 || r.x > width + 100 || r.y > height + 100) continue;
      const cx = r.x + r.w / 2, cy = r.y + r.h / 2 - 20;
      g2.fillStyle = ob.nameColour === -1 ? "#fff" : argbCss(ob.nameColour);
      if (ob.id === hoverId) {
        const w = g2.measureText(ob.name).width + 6;
        g2.save();
        g2.fillStyle = "rgba(0,24,48,0.4)"; g2.fillRect(cx - w / 2, cy - 8, w, 16);
        g2.strokeStyle = "#000"; g2.lineWidth = 1; g2.strokeRect(cx - w / 2 + 0.5, cy - 7.5, w - 1, 15);
        g2.restore();
      } else g2.strokeText(ob.name, cx, cy);
      g2.fillText(ob.name, cx, cy);
    }
    g2.textBaseline = "alphabetic";
    // MapControl.DrawObjects / CreateTexture: with NameView every live non-item object is named (the user too), and the
    // object under the mouse always is (dead or not; DisplayBodyName is the same mouse-over case for monsters).
    // Labels sit at the feet (DisplayRectangle.Y = the draw y): Arial 8 pt, 1 px black outline, LABEL_H = MirLabel height.
    const LABEL_H = 14;
    g2.font = "11px Arial, sans-serif";
    g2.lineWidth = 2;
    g2.textBaseline = "top";
    const label = (text: string, cx: number, top: number, colour: string) => {
      g2.fillStyle = colour; g2.strokeText(text, cx, top); g2.fillText(text, cx, top);
    };
    for (const ob of objects.values()) {
      if (ob.race === ObjectType.Item) continue;
      const { x, y } = view.drawLocation(ob, user);
      if (x < -100 || y < -100 || x > width + 100 || y > height + 100) continue;
      const colour = ob.nameColour === -1 ? "#fff" : argbCss(ob.nameColour);
      const deadDrop = ob.dead ? 35 : 8;
      const named = ob.name && ((gameSettings.NameView && !ob.dead) || hoverId === ob.id);
      if (named && ob.ownerName) {
        // HeroObject.DrawName: name at Y - (42 - h/2), "{0}'s Hero" directly under it
        const top = y - (42 - LABEL_H / 2) + deadDrop;
        label(ob.name, x + 25, top, colour);
        label(`${ob.ownerName}'s Hero`, x + 25, top + LABEL_H - 1, colour);
      } else if (named && ob.race === ObjectType.Player) {
        // PlayerObject.DrawName: the name at Y - (31 - h/2), the guild label at Y - (19 - h/2)
        label(ob.name, x + 25, y - (31 - LABEL_H / 2) + deadDrop, colour);
        if (ob.guildName) label(ob.guildName, x + 25, y - (19 - LABEL_H / 2) + deadDrop, colour);
      } else if (named && ob.name.includes("_") && (ob.race === ObjectType.Merchant || ob.race === ObjectType.Monster)) {
        // NPCObject / MonsterObject.DrawName: one line per "_" part, 12 px apart; an NPC's later words are white;
        // creatures sit higher (MonsterObject yOffset)
        const parts = ob.name.split("_");
        const yOffset = ob.race === ObjectType.Monster ? creatureLabelOffset(ob.image).name : 0;
        const base = y - (32 - LABEL_H / 2) + deadDrop - Math.trunc(((parts.length - 1) * 10) / 2) + yOffset;
        parts.forEach((w, i) => label(w, x + 24, base + i * 12, ob.race === ObjectType.Merchant && i > 0 ? "#fff" : colour));
      } else if (named) {
        label(ob.name, x + 25, y - (32 - LABEL_H / 2) + deadDrop, colour); // MapObject.DrawName
      }
      // MapObject.DrawHealth: the bar at DisplayRectangle + (8, -64); past HealthTime only ShouldDrawHealth (own hero)
      if ((ob.healthExpire > t || (ob.ownerName !== "" && ob.ownerName === user.name)) && ob.healthPercent >= 0 && !ob.dead) {
        g2.fillStyle = "#000"; g2.fillRect(x + 8, y - 64, 32, 4);
        g2.fillStyle = ob.race === ObjectType.Monster ? "#e03030" : "#30c030"; g2.fillRect(x + 8, y - 64, (32 * ob.healthPercent) / 100, 4);
      }
      // MapObject.DrawPoison: 4x4 dots on a 6x6 black square, 5 px apart, at DisplayRectangle + (7, -21)
      if (ob.poison && !ob.dead) {
        poisonDots(ob.poison).forEach((c, i) => {
          g2.fillStyle = "#000"; g2.fillRect(x + 7 + i * 5, y - 21, 6, 6);
          g2.fillStyle = c; g2.fillRect(x + 8 + i * 5, y - 20, 4, 4);
        });
      }
      // MapObject.DrawChat: above the head at Y - (60 + h) - (dead ? 35 : 0), grey when dead (one line here)
      if (ob.chatUntil > t && ob.chatText) {
        const text = ob.chatText.length > 60 ? ob.chatText.slice(0, 57) + "…" : ob.chatText;
        const chatOffset = ob.race === ObjectType.Monster ? creatureLabelOffset(ob.image).chat : 0; // MonsterObject.DrawChat
        label(text, x + 24, y - (60 + LABEL_H) - (ob.dead ? 35 : 0) + chatOffset, ob.dead ? "#808080" : "#fff");
      }
    }
    // Damage numbers last, over names and chat; lists of objects that left are dropped
    for (const [id, list] of damageNumbers) {
      const ob = objects.get(id);
      if (ob) processDamageList(list, t);
      if (!ob || list.length === 0) { damageNumbers.delete(id); critAt.delete(id); continue; }
      const { x, y } = view.drawLocation(ob, user);
      if (x < -200 || y < -200 || x > width + 200 || y > height + 200) continue;
      for (const d of list) drawDamageNumber(g2, d, x, y);
    }
    g2.textBaseline = "alphabetic";
  }

  let hoverId = -1;
  /** MapObject.MouseObject (MapControl.CheckMouseOver): the object under the mouse pixel, by its drawn body. */
  const mouseObject = (): MapObj | null => (user ? view.mouseObject(mouse.x, mouse.y, user, objects.values(), gameSettings.TargetDead) : null);
  // The hover outline (WoOOLcraft hoverOutline.ts, with Settings.HighlightTarget): red on the hovered live monster,
  // blue on the hovered NPC, blue always on a party or guild member.
  view.outline = (ob) => {
    if (!gameSettings.HighlightTarget || ob.dead) return null;
    if (ob.id === hoverId || ob.id === attackTargetId) { // the target keeps it (MapControl: TargetObject.DrawBlend with HighlightTarget)
      if (ob.race === ObjectType.Monster) return OUTLINE_RED;
      if (ob.race === ObjectType.Merchant) return OUTLINE_BLUE;
    }
    if (ob.race === ObjectType.Player && ob !== user && (group.has(ob.name) || (!!user?.guildName && ob.guildName === user.guildName))) return OUTLINE_BLUE;
    return null;
  };
  let shiftHeld = false, altHeld = false;
  let outputDelay = 0; // MapControl.OutputDelay ("Target is too far.")
  const MAX_ATTACK_RANGE = 9; // Globals.MaxAttackRange
  // Held Shift / Alt (CMain.Shift / Alt): watchers see every key, whatever a handler consumes or the chat input types
  input.watch((e) => { if (e.key === "Shift") shiftHeld = true; if (e.key === "Alt") { altHeld = true; e.preventDefault(); } });
  input.onKeyUp((e) => { if (e.key === "Shift") shiftHeld = false; if (e.key === "Alt") altHeld = false; });
  input.onBlur(() => { shiftHeld = altHeld = false; });

  function step(): void {
    const t = now();
    if (t >= nextAnimation) { animationCount++; nextAnimation = t + 100; } // MapControl.AnimationCount: 100 ms ticks
    for (const ob of objects.values()) ob.process(t);
    for (let i = mapEffects.length - 1; i >= 0; i--) mapEffects[i]?.process();
    for (const so of spellObjects.values()) so.process();
    doors.process(t);
    // MapControl: a pending magic (F-key) is cast once the user is idle
    if (user && magic.next && !user.dead && t >= nextAction && t >= inputDelay && isIdleAction(user.action) && user.feed.length === 0 && canCastNextMagic(user)) magic.useNext(user);
    if (statsDirty && user) recomputeStats();
    ui.frame(t, "beforeInput"); // classic: HUD output lines / timers / compass, character window (+ bag weight)
    if (t - lastRunTime > 899) canRun = false; // PlayerObject.Process (User)
    checkInput(t);
    ui.frame(t, "afterInput"); // classic: skill-bar cooldowns, hero windows / panels, buff icons
    if (user) {
      const hovered = mouseObject();
      hoverId = hovered?.id ?? -1;
      // GameScene.UpdateMouseCursor: monster Attack, NPC NPCTalk, another player AttackRed with Shift, else Default
      setMouseCursor(canvas, !hovered ? "Default" : hovered.race === ObjectType.Monster ? "Attack" : hovered.race === ObjectType.Merchant ? "NPCTalk"
        : hovered.race === ObjectType.Player && shiftHeld ? "AttackRed" : "Default");
      weather.update(t, view.painter.width, view.painter.height);
      artWarmer.warm(user, objects.values());
      artWarmer.warmSpells(user, magic.magics);
      view.render({ map, user, objects: objects.values(), animationCount, lights: lightState, mapEffects, doors, now: t });
      if (gameSettings.CozyMode) grade.apply(view.painter.width, view.painter.height, COZY_GRADE);
      ui.frame(t, "afterRender"); // classic: minimap and big map canvases
      drawOverlay(t);
    }
    frames++;
    if (t - fpsAt >= 1000) { fps = frames; frames = 0; fpsAt = t; }
    ui.frame(t, "end"); // classic: MainDialog.Process (+ the mode labels and DuraStatusDialog under the minimap)
  }
  const frame = () => { if (disposed) return; step(); rafId = requestAnimationFrame(frame); };
  rafId = requestAnimationFrame(frame);

  /** GameScene.Dispose on LogOutSuccess / ReturnToLogin: stop the loop, drop listeners, music and the scene's DOM. */
  function dispose(): void {
    if (disposed) return;
    disposed = true;
    cancelAnimationFrame(rafId);
    endGameScope();
    sound.stopAll();
    for (const c of Array.from(stage.children)) if (!stageBefore.has(c)) c.remove();
    ui.dispose();
    renderer?.clear();
    // GameScene.LogOutSuccess / ReturnToLogin: back to 1024 x 768 for the account screens
    stage.style.width = "1024px";
    stage.style.height = "768px";
    setScreenSize(1024, 768);
  }

  // Debug object; classic src/ui/userRefs.ts reads `user` / `itemInfos` from it. The UI's windows keep their old keys.
  (window as unknown as { crystalGame: unknown }).crystalGame = {
    ...ui.debug, ui,
    objects, get user() { return user; }, get map() { return map; }, view, step, doors, itemChanges, weather, creatures, hero, heroItems, quests, chatLog, hudState, account, currentMap, itemPick, heroStats, mountState, fishing, groupState: group, tradeState: trade, rentalState: rental, get autoPath() { return autoPath; }, lightState, itemInfos, inventory, gameInfo, magic, mapEffects, sound, get userBuffs() { return userBuffs; },
    get fps() { return fps; }, get loading() { return loadingCount(); },
    /** Debug/test hook: hold a mouse button at a canvas pixel (buttons: 1 left, 2 right, 0 release). */
    mouse(x: number, y: number, b: number) { mouse = { x, y }; buttons = b; if (!(b & 2)) canRun = false; },
  };
  return { handle, dispose };
}
