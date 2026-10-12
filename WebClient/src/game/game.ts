// In-game scene: world state from server packets (GameScene.ProcessPacket subset), the frame loop, mouse/keyboard
// input (MapControl.CheckInput subset: walk, run, turn, attack) and the HUD wiring (MainDialog, ChatDialog +
// ChatControlBar, MenuDialog, MiniMapDialog, BuffDialog, DuraStatusDialog, hero panels: src/ui/*) + the name labels.
// The user moves like the desktop: an action is queued and its packet sent when it starts; the next move waits for
// the server's S.UserLocation (MapControl.NextAction, 2.5 s fallback).

import type { Session } from "./session.ts";
import type { ServerPacket, PacketOf } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirAction as A, ObjectType, ChatType, Spell, LightSetting, Stat } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { StartInfo } from "../ui/screens.ts";
import { WebGl2Renderer } from "../render/backend.ts";
import { readMap, blankMap, type MapCells } from "../map/mapReader.ts";
import { attachRenderer, loadingCount } from "./libraries.ts";
import { MapObj, pointMove, directionFromPoint, creatureLabelOffset, CELL_W, CELL_H } from "./objects.ts";
import { MapView, objectAt } from "./mapView.ts";
import { ItemInfos, equipmentLooks } from "./items.ts";
import type { LightState } from "./lighting.ts";
import { Inventory, BELT_IDX } from "./inventory.ts";
import { InventoryWindow } from "../ui/inventoryWindow.ts";
import { NpcWindow } from "../ui/npcWindow.ts";
import { Magic } from "./magic.ts";
import { setEffectWorld, Effect, Lib, InterruptionEffect, ElementsEffect, TrackableEffect, DelayedExplosionEffect } from "./effects.ts";
import { MagicBar } from "../ui/magicBar.ts";
import { spellStart, spellComplete, drawActionEffects, objectEffect, mapEffect, objectProjectile, createProjectile, SpellObj, type SpellWorld } from "./spellVisuals.ts";
import { spellHooks, moveHooks } from "./objects.ts";
import { SoundManager, SoundList } from "./audio.ts";
import { addBuffEffect, removeBuffEffect, processBuffs, setEffects, actionRefresh, poisonDots, userBuff, type UserBuff } from "./buffs.ts";
import { BuffBar } from "../ui/buffBar.ts";
import { Doors } from "./doors.ts";
import { MainDialog, AMODE_TEXT, PMODE_TEXT } from "../ui/mainDialog.ts";
import { MenuDialog } from "../ui/menuDialog.ts";
import { ChatDialog } from "../ui/chatDialog.ts";
import { DuraPanel } from "../ui/duraPanel.ts";
import { setUiRoot, setUiSound, setScreenSize } from "../ui/mir/controls.ts";
import { amountBox } from "../ui/messageBox.ts";
import { MiniMap } from "../ui/miniMap.ts";
import { BigMap } from "../ui/bigMap.ts";
import { Group } from "../ui/groupWindow.ts";
import { TradeWindow } from "../ui/tradeWindow.ts";
import { HudExtras } from "../ui/hudExtras.ts";
import { GameInfo } from "./gameInfo.ts";
import { MarketWindow } from "../ui/marketWindow.ts";
import { StorageWindow } from "../ui/storageWindow.ts";
import { NpcServices } from "../ui/npcServices.ts";
import { ItemChanges } from "./itemChanges.ts";
import { SocketWindow } from "../ui/socketWindow.ts";
import { RentalWindow } from "../ui/rentalWindow.ts";
import { RelationshipWindow, MentorWindow } from "../ui/relationshipWindow.ts";
import { InspectWindow } from "../ui/inspectWindow.ts";
import { RankingWindow } from "../ui/rankingWindow.ts";
import { MailWindow } from "../ui/mailWindow.ts";
import { FriendWindow } from "../ui/friendWindow.ts";
import { GameShopWindow } from "../ui/gameShopWindow.ts";
import { Hero } from "./hero.ts";
import { Creatures } from "./creatures.ts";
import { CreatureWindow } from "../ui/creatureWindow.ts";
import { HeroMagicWindow } from "../ui/heroMagicWindow.ts";
import { HeroPanels } from "../ui/heroPanels.ts";
import { HeroManageWindow, NewHeroWindow } from "../ui/heroManageWindow.ts";
import { HeroCharacterWindow } from "../ui/heroCharacterWindow.ts";
import { HeroItems } from "./heroItems.ts";
import { Weather } from "./weather.ts";
import { gameSettings } from "./settings.ts";
import { OptionWindow } from "../ui/optionWindow.ts";
import { HelpWindow, HELP_DEFAULT_KEYS } from "../ui/helpWindow.ts";
import { ChatOptionWindow } from "../ui/chatOptionWindow.ts";
import { installClickSounds } from "../ui/clickSounds.ts";
import { KeyboardLayoutWindow } from "../ui/keyboardLayoutWindow.ts";
import { monsterActionStart, setMonsterSpecialsWorld } from "./monsterSpecials.ts";
import { applyGameDataChanged, type GameDataChanges } from "./gameData.ts";
import { HeroInventoryWindow } from "../ui/heroInventoryWindow.ts";
import { MountWindow } from "../ui/mountWindow.ts";
import { FishingWindow, FishingStatusWindow } from "../ui/fishingWindow.ts";
import { GuildTerritoryWindow } from "../ui/guildTerritoryWindow.ts";
import { Quests, questIconImage } from "./quests.ts";
import { QuestWindow } from "../ui/questWindow.ts";
import { messageBox } from "../ui/messageBox.ts";
import { CharacterWindow } from "../ui/characterWindow.ts";
import { refreshStats, type UserStats } from "./stats.ts";
import { canWearItem } from "./canWear.ts";
import { gameSignal, endGameScope } from "./scope.ts";
import type { BaseStats, SelectInfo, UserItem } from "../protocol/server.ts";
import { Guild } from "../ui/guildWindow.ts";
import { findPath } from "./pathFinder.ts";
import { monsterSound, playerSound, fishingFrame, intelligentCreaturePickupSound, type SoundWorld } from "./sounds.ts";
import { canFish, canRideAttack, canCastNextMagic, canUseSpell, canHarvest, canRangeAttack, isFishingAction, isIdleAction, runDistance, transformTypeFromBuffs } from "./riding.ts";
import { PoisonType, MirClass, BuffType, AttackMode, PetMode, MirGridType, EquipmentSlot, BindMode, StatName } from "../protocol/enums.generated.ts";
import { KeybindOptions, keyLabel, matches, matchesAny, matchesAnyBinding, type KeybindOption } from "./keybinds.ts";

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
  // SelectScene StartGame: Settings.Resolution (default 1024; "crystal.resolution" = 1280 opts in) capped by the server's
  const preferred = (() => { try { return Number(localStorage.getItem("crystal.resolution")) || 1024; } catch { return 1024; } })();
  const width = Math.min(start.resolution || 1024, preferred) >= 1280 ? 1280 : 1024;
  const height = width === 1280 ? 800 : 768;
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
  /** MapControl.Weather particles (weather.ts), drawn by MapView after the objects. */
  const weather = new Weather();
  view.weather = weather;
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

  const overlay = document.createElement("canvas");
  overlay.width = width;
  overlay.height = height;
  overlay.style.cssText = "position:absolute;inset:0;pointer-events:none;width:100%;height:100%";
  stage.insertBefore(overlay, canvas.nextSibling);
  const g2 = overlay.getContext("2d")!;
  // dispose() removes whatever this scene adds to the stage
  const stageBefore = new Set(Array.from(stage.children));
  // The desktop-look controls (src/ui/mir/controls.ts) attach to the stage and play MirButton sounds through the scene
  setUiRoot(stage);
  const screen: [number, number] = [width, height];

  // — HUD (GameScene ctor): MainDialog, ChatDialog + ChatControlBar, MenuDialog; the rest below —
  /** GameScene.PickedUpGold: the GoldLabel was clicked; the next map click asks how much gold to drop. */
  let pickedUpGold = false;
  const mainDialog = new MainDialog(screen, {
    inventory: () => inventoryWindow.toggle(),
    character: () => characterWindow.openPage("character"),
    skills: () => magicBar.toggle(),
    quests: () => questWindow.toggleDiary(),
    options: () => optionWindow.toggle(),
    menu: () => menuDialog.toggle(),
    gameShop: () => gameShop.toggle(),
    gold: () => { if (inventoryWindow.pickedInventorySlot < 0) pickedUpGold = !pickedUpGold && inventory.gold > 0; },
  });
  const chatDialog = new ChatDialog({
    screen, mainX: mainDialog.location[0], send: (f) => session.send(f),
    chatItem: (id) => chatItems.get(id),
    describe: (item) => inventoryWindow.describe(item),
    chatNotice: (text) => extras.showChatNotice(text),
    chatOptions: () => chatOptions.toggle(),
    trade: () => trade.request(),
  });
  /** GameScene.LogOut / QuitGame: the MirMessageBox, then C.LogOut (back to character select). */
  const logOut = (exit: boolean) => {
    messageBox(stage, exit ? "Do you want to quit Legend of Mir?" : "Do you want to log out of Legend of Mir?", [
      { label: "Yes", onClick: () => session.send(ClientPackets.LogOut()) }, { label: "No" }]);
  };
  const menuDialog = new MenuDialog(width, mainDialog.location[1], {
    exit: () => logOut(true), logOut: () => logOut(false),
    help: () => helpWindow.toggle(), keyboard: () => keyboardLayout.toggle(), ranking: () => ranking.toggle(),
    creatures: () => creatureWindow.toggle(), mount: () => mountWindow.toggle(), fishing: () => fishingWindow.toggle(),
    friends: () => friends.toggle(), mentor: () => mentor.toggle(), relationship: () => relationship.toggle(),
    group: () => group.toggle(), guild: () => guild.toggle(),
  });

  // — World state —
  let map: MapCells = blankMap();
  let mapTitle = "", mapFile = "";
  const lightState: LightState = { mapLights: LightSetting.Normal, timeOfDay: LightSetting.Day, mapDarkLight: 0 };
  const itemInfos = new ItemInfos();
  /** GameScene MonsterInfoList / NPCInfoList / MapInfoList (NPC page links, big map / world map). */
  const gameInfo = new GameInfo((f) => session.send(f), itemInfos);
  const inventory = new Inventory(itemInfos, (f) => session.send(f));
  /** The user's hero (hero.ts): GameScene.Hero + spawn state, storage, behaviour. */
  const hero = new Hero(() => performance.now(), (text) => chat(text, ChatType.Hint));
  const inventoryWindow = new InventoryWindow(stage, inventory, () => performance.now());
  const npcWindow = new NpcWindow(stage, session, inventory, itemInfos, inventoryWindow, () => performance.now(), gameInfo);
  /** TrustMerchantDialog (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess; closes with the NPC dialog). */
  const market = new MarketWindow(stage, inventory, inventoryWindow, (f) => session.send(f), (t, ty) => chat(t, ty), () => performance.now(), npcWindow);
  /** StorageDialog (opened by S.NPCStorage; the items live in inventory.storage). */
  const storageWindow = new StorageWindow(stage, inventoryWindow, (f) => session.send(f), (t, ty) => chat(t, ty), npcWindow);
  /** NPC item services: refine, wedding ring, crafting, awakening, disassemble / downgrade / reset, pearl shop. */
  const npcServices = new NpcServices({
    stage, send: (f) => session.send(f), inv: inventory, infos: itemInfos, invWindow: inventoryWindow, npc: npcWindow,
    chat: (t, ty) => chat(t, ty), stat: (st) => userStats?.stats[st] ?? 0,
  });
  /** Sockets / mount and fishing slots, seal, combine, upgrade, rental items (itemChanges.ts) + their windows. */
  const itemChanges = new ItemChanges(inventory, (f) => session.send(f));
  new SocketWindow(stage, inventory, inventoryWindow, itemChanges, (i) => gameInfo.requestItem(i)); // sets inventoryWindow.changes, inventory.useSlotItem
  const rental = new RentalWindow(stage, inventory, inventoryWindow, itemChanges, (f) => session.send(f),
    (t, ty) => chat(t, ty), () => user?.name ?? "", (i) => gameInfo.requestItem(i), () => performance.now());
  /** MountDialog, FishingDialog, FishingStatusDialog (J / M / N / Esc are registered by the windows). */
  const mountWindow = new MountWindow(stage, inventory, inventoryWindow, itemChanges, (f) => session.send(f),
    () => user && { id: user.id, mountType: user.mountType, ridingMount: user.ridingMount, action: user.action, fishing: user.fishing },
    (i) => gameInfo.requestItem(i), () => performance.now());
  const fishingWindow = new FishingWindow(stage, inventory, inventoryWindow, (f) => session.send(f), (i) => gameInfo.requestItem(i), () => performance.now());
  const fishingStatus = new FishingStatusWindow(stage, inventory, inventoryWindow, (f) => session.send(f));
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
  const magicBar = new MagicBar(stage, magic, (f) => session.send(f));
  /** Hero skills (Ctrl+S, registered by the window; Shift+F1-F8 cast for the hero). */
  const heroMagicWindow = new HeroMagicWindow(stage, magic);
  const sound = new SoundManager();
  const play = (id: number) => sound.play(id);
  setUiSound((id) => sound.play(id));
  /** GameScene.BuffsDialog. */
  const buffBar = new BuffBar(stage, { screenWidth: width, guildStats: () => guildBuffText() });
  /** BuffsDialog.Buffs (the user's). */
  let userBuffs: UserBuff[] = [];
  const doors = new Doors();
  // quests / lightState are read only from the frame loop, after they exist
  const miniMap = new MiniMap(stage, {
    questIcon: (id) => quests.npcQuestIcon(id), timeOfDay: () => lightState.timeOfDay, screenWidth: width,
    openMail: () => mail.toggle(), openBigMap: () => bigMap.toggle(), newMail: () => mail.newMail,
  });
  /** DuraStatusDialog + CharacterDuraPanel (under the minimap). */
  const duraPanel = new DuraPanel(width, miniMap.location[0], miniMap.height, {
    equipment: () => inventory.equipment, info: (item) => inventory.info(item),
  });
  /** OptionDialog (F12 / O), HelpDialog (H), ChatOptionDialog; MirButton click sounds on every stage button. */
  const optionWindow = new OptionWindow(stage, (f) => session.send(f), (t, ty) => chat(t, ty), sound);
  const helpWindow = new HelpWindow(stage, (o) => { // the shortcut pages show the live bindings (keybinds.ts)
    const opt = (KeybindOptions as Record<string, KeybindOption | undefined>)[o];
    return opt === undefined ? HELP_DEFAULT_KEYS[o] ?? "" : keyLabel(opt);
  });
  const chatOptions = new ChatOptionWindow(stage); // ChatDialog follows the [Filters] / TransparentChat settings itself
  const keyboardLayout = new KeyboardLayoutWindow(stage); // KeyboardLayoutDialog (U), rebinding through keybinds.ts
  installClickSounds(stage, (id) => sound.play(id));
  const bigMap = new BigMap(stage, gameInfo, (f) => session.send(f), () => inventory.gold);
  const group = new Group(stage, (f) => session.send(f), (t, ty) => chat(t, ty), () => user?.name ?? "");
  /** MapControl.AutoPath / CurrentPath: walking a path found by a big-map click. */
  let autoPath: WirePoint[] | null = null;
  bigMap.onWalk = (cell) => {
    if (!user) return;
    const path = findPath(map.width, map.height, (x, y) => emptyCell({ X: x, Y: y }), user.location, cell);
    if (!path || path.length === 0) { chat("Could not find a path.", ChatType.System); return; }
    autoPath = path;
  };
  const trade = new TradeWindow(stage, inventory, inventoryWindow, (f) => session.send(f), (t, ty) => chat(t, ty), () => user?.name ?? "");
  const guild = new Guild(stage, (f) => session.send(f), (t, ty) => chat(t, ty), () => user?.name ?? "", () => performance.now(),
    () => chatDialog.setText("!~"), inventoryWindow, () => inventory.gold,
    (stats) => {
      // GameScene.GuildBuffList: a local, infinite BuffType.Guild buff while any guild buff is active
      userBuffs = userBuffs.filter((b) => b.Type !== BuffType.Guild);
      guildBuffStats = stats;
      statsDirty = true;
      if (stats && user) userBuffs.push({ Type: BuffType.Guild, Visible: false, ObjectID: user.id, expire: 0, Infinite: true, Paused: false, Stats: stats, Values: [] });
    });
  const extras = new HudExtras({
    stage, send: (f) => session.send(f), now: () => performance.now(), play: (id) => sound.play(id), screen,
    callNpcPage: (page) => npcWindow.callPage(page),
  });
  const characterWindow = new CharacterWindow(stage, () => ({
    name: user?.name ?? "", cls: user?.look.class ?? 0, level, hp, mp, experience, maxExperience, stats: userStats,
    gender: user?.look.gender, hair: user?.look.hair, guildName: user?.guildName,
  }));
  // ItemLabel context for the inventory's item labels (GameScene.CreateItemLabel)
  inventoryWindow.labelContext = () => ({
    info: (i) => itemInfos.get(i.ItemIndex), realItem: (info) => itemInfos.realItem(info, level, user?.look.class ?? 0),
    level, job: user?.look.class ?? 0, gender: user?.look.gender ?? 0, stat: (k) => userStats?.stats[k] ?? 0,
  });
  // ChatDialog.LinkedItems: Shift+right-click puts "<FriendlyName> " in the chat box (MirItemCell)
  inventoryWindow.onLink = (item) => {
    const info = itemInfos.get(item.ItemIndex);
    if (!info) return;
    if (!chatDialog.addLink(item.UniqueID, friendlyName(info.Name, item.Count), MirGridType.Inventory)) chat("Unable to link item, message exceeds allowed length", ChatType.System);
  };
  inventoryWindow.notify = (text) => chat(text, ChatType.System);
  inventory.refuse = (text) => chat(text, ChatType.System);
  inventory.canWear = (item, slot) => {
    const info = itemInfos.get(item.ItemIndex);
    if (!info || !userStats || !user) return null; // no data yet: let the server decide
    const cur = inventory.equipment[slot] ?? null, curInfo = cur ? itemInfos.get(cur.ItemIndex) : undefined;
    const weapon = inventory.equipment[EquipmentSlot.Weapon];
    return canWearItem({ cls: user.look.class, gender, level, stats: userStats, weaponInfo: weapon ? itemInfos.get(weapon.ItemIndex) : undefined },
      item, info, cur && curInfo ? { item: cur, info: curInfo } : null);
  };

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

  /** GuildDialog.UpdateActiveStats: the Guild buff's hint lines (ClientTextKeys.ValueByOwnerPercent). */
  function guildBuffText(): string {
    let text = "";
    for (const [k, v] of Object.entries(guildBuffStats ?? {})) {
      if (!v) continue;
      const name = StatName[Number(k)] ?? k;
      text += `${v < 0 ? "Decreases" : "Increases"} ${name} by: ${v}${name.includes("Percent") ? "%" : ""}.\n`;
    }
    return text;
  }

  /** GameScene: the DiedTip box once the user is dead (Yes = C.TownRevive while still dead). */
  function showRevive(): void {
    closeRevive?.();
    setTimeout(() => {
      if (disposed || !user?.dead) return;
      closeRevive = messageBox(stage, "You have died, Do you want to revive in town?", [
        { label: "Yes", primary: true, onClick: () => { closeRevive = null; if (user?.dead) session.send(ClientPackets.TownRevive()); } },
        { label: "No", onClick: () => { closeRevive = null; } },
      ]);
    }, 1500);
  }
  /** GameScene.RequestReincarnation: a Taoist offers to revive the dead user (Yes = C.AcceptReincarnation); like the
   *  desktop, an offer that arrives during the death animation is dropped. */
  function showReincarnation(): void {
    closeRevive?.();
    closeRevive = messageBox(stage, "Would you like to be revived?", [
      { label: "Yes", primary: true, onClick: () => { closeRevive = null; if (user?.dead) session.send(ClientPackets.AcceptReincarnation()); } },
      { label: "No", onClick: () => { closeRevive = null; } },
    ]);
  }
  /** Hero panels (info, menu, behaviour, buffs, summon), hero character (Ctrl+C), manage + create dialogs. */
  const heroChar = new HeroCharacterWindow(stage, hero, itemInfos, () => performance.now(), () => magic.heroMagics);
  /** The hero's bag, belt (7 / 8), equipment and auto-pot (Ctrl+I); cells share the inventory pick. */
  const heroItems = new HeroItems(hero, inventory, (f) => session.send(f));
  const heroInventoryWindow = new HeroInventoryWindow(stage, hero, heroItems, inventoryWindow, () => performance.now());
  heroItems.refuse = (text) => chat(text, ChatType.System);
  heroItems.ask = (text, yes) => { messageBox(stage, text, [{ label: "Yes", onClick: yes }, { label: "No" }]); };
  // MirItemCell.CanWearItem with the hero as the wearer (UserHeroObject stats from heroCharacterWindow)
  heroItems.canWear = (item, slot) => {
    const h = hero.info, stats = heroChar.stats(), info = itemInfos.get(item.ItemIndex);
    if (!h || !stats || !info) return null; // no data yet: let the server decide
    const cur = h.equipment[slot] ?? null, curInfo = cur ? itemInfos.get(cur.ItemIndex) : undefined;
    const weapon = h.equipment[EquipmentSlot.Weapon];
    return canWearItem({ cls: h.cls, gender: h.gender, level: h.level, stats, weaponInfo: weapon ? itemInfos.get(weapon.ItemIndex) : undefined },
      item, info, cur && curInfo ? { item: cur, info: curInfo } : null);
  };
  const openHeroInventory = (): void => heroInventoryWindow.toggle();
  const heroPanels = new HeroPanels(stage, hero, (f) => session.send(f), {
    openSkills: () => heroMagicWindow.toggle(),
    openInventory: () => openHeroInventory(),
    openCharacter: () => heroChar.toggle(),
  }, {
    main: mainDialog.win, mainLocation: mainDialog.location, screen,
    maxHp: () => heroChar.maxHp(), maxMp: () => heroChar.maxMp(), itemImage: (i) => itemInfos.get(i)?.Image,
  });
  new HeroManageWindow(stage, hero, (f) => session.send(f));
  new NewHeroWindow(stage, hero, (f) => session.send(f));
  /** Quests: state (quests.ts) + the NPC quest list, details, log (Q), tracking panel, quest items. */
  const quests = new Quests((f) => session.send(f), itemInfos);
  const questWindow = new QuestWindow(stage, quests, npcWindow, inventoryWindow, itemInfos, gameInfo);
  questWindow.onHelp = () => helpWindow.displayPageNamed("Quests");
  mountWindow.onHelp = () => helpWindow.displayPageNamed("Mounts");
  npcWindow.onHelp = () => helpWindow.displayPageNamed("Purchasing");
  inventoryWindow.onQuestTab = () => questWindow.toggleItems(true);
  view.npcQuestImage = (ob) => { const icon = quests.npcQuestIcon(ob.id); return icon === null ? null : questIconImage(icon, performance.now()); };
  let miniMapIndex = 0; // MapControl.MiniMap
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
  const objects = new Map<number, MapObj>();
  let user: MapObj | null = null;
  let hp = 0, mp = 0, level = 0;
  let experience = 0n, maxExperience = 0n;
  let gender = 0; // UserObject.Experience / MaxExperience
  let aMode: number = AttackMode.Peace, pMode: number = PetMode.Both, modeTime = 0; // GameScene.AMode / PMode / ChangeAModeTime
  let baseStats: BaseStats | null = null; // UserObject.CoreStats (S.BaseStatsInfo)
  let guildBuffStats: Record<number, number> | null = null;
  let userStats: UserStats | null = null; // UserObject.Stats & co (RefreshStats)
  let statsDirty = true;
  let closeRevive: (() => void) | null = null;
  /** GameScene.ChatItemList (S.NewChatItem, for incoming chat links). */
  const chatItems = new Map<string, UserItem>();
  let disposed = false, rafId = 0;
  // — Section 4: social windows (after the state above: RankingWindow renders in its constructor and reads user) —
  const prefillChat = (text: string) => chatDialog.setText(text);
  const relationship = new RelationshipWindow(stage, (f) => session.send(f), (t, ty) => chat(t, ty), prefillChat);
  const mentor = new MentorWindow(stage, (f) => session.send(f), (t, ty) => chat(t, ty), () => ({ name: user?.name ?? "", level, cls: user?.look.class ?? 0 }));
  const inspect = new InspectWindow(stage, (f) => session.send(f), (t, ty) => chat(t, ty), prefillChat,
    () => group.list, () => user?.name ?? "", () => performance.now(), inventoryWindow, gameInfo);
  const ranking = new RankingWindow(stage, (f) => session.send(f), () => user?.name ?? "", inspect);
  const mail = new MailWindow(stage, inventory, inventoryWindow, (f) => session.send(f), (t, ty) => chat(t, ty), (i) => gameInfo.requestItem(i));
  inspect.composeMail = (n) => mail.composeLetter(n);
  market.mailTo = (name, msg) => mail.composeLetter(name, msg);
  mail.onHelp = () => helpWindow.toggle(true);
  const friends = new FriendWindow(stage, (f) => session.send(f), (t, ty) => chat(t, ty), (name) => mail.composeLetter(name));
  relationship.composeMail = (n) => mail.composeLetter(n);
  /** Section 5: game shop (Y / Shop button) and the guild territory list (opened by the server). */
  const gameShop = new GameShopWindow(stage, inventoryWindow, (f) => session.send(f), (t, ty) => chat(t, ty),
    () => inventory.gold, () => inventory.credit, () => user?.look.class ?? 0);
  const territories = new GuildTerritoryWindow(stage, (f) => session.send(f), (name) => mail.composeLetter(name));
  /** Intelligent creatures (E; X / Alt+A pick up at the mouse cell): UserObject.IntelligentCreatures + IntelligentCreatureDialog. */
  const creatures = new Creatures();
  const creatureWindow = new CreatureWindow(stage, creatures, (f) => session.send(f), (t, ty) => chat(t, ty),
    () => (user ? view.mapLocation(mouse.x, mouse.y, user) : null));
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
  let inputDelay = 0;     // MapControl.InputDelay
  let pickUpTime = 0;     // GameScene.PickUpTime
  const pickUp = () => {
    if (now() <= pickUpTime) return;
    pickUpTime = now() + 200;
    session.send(ClientPackets.PickUp());
  };
  const now = () => performance.now();

  /** ChatDialog.ReceiveChat (chatDialog.ts). */
  function chat(text: string, type: number): void {
    chatDialog.receiveChat(text, type);
  }

  async function loadMap(fileName: string): Promise<void> {
    mapFile = fileName;
    map = blankMap();
    try {
      const res = await fetch(`/map/${encodeURIComponent(fileName)}.map`);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const parsed = readMap(new Uint8Array(await res.arrayBuffer()));
      if (mapFile === fileName) map = parsed;
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
    optionWindow.setAllowObserve(p.AllowObserve); // GameScene.UserInformation: AllowObserve
    if (user) objects.delete(user.id);
    const u = new MapObj(p.ObjectID, ObjectType.Player, p.Location, p.Direction);
    u.name = p.Name;
    u.nameColour = p.NameColour;
    u.guildName = p.GuildName;
    u.look = { class: p.Class, gender: p.Gender, hair: p.Hair, armour: 0, weapon: -1, weaponEffect: 0 };
    hp = p.HP; mp = p.MP; level = p.Level;
    experience = p.Experience; maxExperience = p.MaxExperience;
    gender = p.Gender;

    u.onActionStart = (q) => {
      // PlayerObject.SetAction for the user: send the packet when the action starts.
      switch (q.action) {
        case A.Walking: session.send(ClientPackets.Walk({ Direction: q.direction })); canRun = true; nextAction = now() + 2500; break;
        case A.Running: session.send(ClientPackets.Run({ Direction: q.direction })); nextAction = now() + 2500; break;
        case A.Standing: session.send(ClientPackets.Turn({ Direction: q.direction })); nextAction = now() + 2500; break;
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
          const magicPacket = (objectId: number, targetId = 0, location = { X: 0, Y: 0 }) =>
            session.send(ClientPackets.Magic({ ObjectID: objectId, Spell: sp, Direction: u.direction, TargetID: targetId, Location: location, SpellTargetLock: false }));
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
        case A.DashL: case A.DashR: case A.Jump: case A.DashAttack: canRun = false; break;
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
    if (heroItems.handle(p)) { refreshUserLooks(); statsDirty = true; heroChar.invalidate(); } // TakeBack / Transfer change the user's bag too
    rental.handle(p);
    magic.handle(p);
    if (STATS_PACKETS.has(p.type)) statsDirty = true;
    quests.handle(p); // reads UserInformation / LevelChanged too, so never returns early
    questWindow.handle(p);
    if (optionWindow.handle(p)) return; // S.AllowObserve
    creatureWindow.handle(p); // the creature list + window (false for UserInformation, so nothing is skipped)
    if (extras.handle(p)) return;
    storageWindow.handle(p);
    if (relationship.handle(p) || mentor.handle(p) || inspect.handle(p) || ranking.handle(p) || mail.handle(p) || friends.handle(p)) return;
    if (gameShop.handle(p) || territories.handle(p)) return;
    group.handle(p);
    trade.handle(p);
    guild.handle(p, (name) => { if (user) user.guildName = name; });
    npcWindow.handle(p);
    npcServices.handle(p);
    bigMap.handle(p);
    if (market.handle(p)) return;
    if (gameInfo.handle(p)) return;
    switch (p.type) {
      case "GainExperience":
        // GameScene.GainExperience (OutputMessage)
        extras.output(`Experience Gained ${p.Amount}`);
        experience += BigInt(p.Amount);
        break;
      case "BaseStatsInfo": baseStats = p.Stats; break;
      case "ChangeAMode": aMode = p.Mode; chat(AMODE_TEXT[p.Mode] ?? `[Mode ${p.Mode}]`, ChatType.Hint); break;
      case "ChangePMode": pMode = p.Mode; chat(PMODE_TEXT[p.Mode] ?? `[Pet mode ${p.Mode}]`, ChatType.Hint); break;
      case "LogOutSuccess": dispose(); exits.logOut(p.Characters); break;
      case "LogOutFailed": break; // GameScene.LogOutFailed: just re-enables the scene
      case "ReturnToLogin": dispose(); exits.returnToLogin(); break;
      case "NewChatItem": chatItems.set(String(p.Item.UniqueID), p.Item); break; // MirScene.NewChatItem
      case "LevelChanged":
        experience = p.Experience; maxExperience = p.MaxExperience;
        level = p.Level;
        refreshUserLooks(); // level-based items change shape (GetRealItem)
        extras.output("Congratulations! You have leveled up. Your HP and MP have been restored."); // GameScene.LevelChanged
        play(SoundList.LevelUp);
        break;
      case "GainedItem": {
        const info = itemInfos.get(p.Item.ItemIndex);
        extras.output(`You gained ${info ? friendlyName(info.Name, p.Item.Count) : "an item"}.`); // GameScene.GainedItem
        break;
      }
      // GameScene.GainedGold / GainedCredit / LoseCredit (inventory.ts keeps the amounts); "{0:###,###,###} {1}"
      case "GainedGold": extras.output(`You gained ${p.Gold > 0 ? p.Gold.toLocaleString("en-US") : ""} Gold`); play(SoundList.Gold); break;
      case "GainedCredit": if (p.Credit !== 0) { extras.output(`You gained ${p.Credit.toLocaleString("en-US")} Credit`); play(SoundList.Gold); } break;
      case "LoseCredit": play(SoundList.Gold); break;
      case "NewItemInfo":
        itemInfos.add(p.Info);
        refreshUserLooks();
        inventoryWindow.render();
        break;
      case "PlayerUpdate": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob !== user) { ob.look = { ...ob.look, armour: p.Armour, weapon: p.Weapon, weaponEffect: p.WeaponEffect }; ob.light = p.Light; }
        break;
      }
      case "MapInformation":
        miniMapIndex = p.MiniMap;
        bigMap.bigMapIndex = p.BigMap;
        sound.playMusic(p.Music);
        mapTitle = p.Title;
        lightState.mapLights = p.Lights;
        lightState.mapDarkLight = p.MapDarkLight;
        weather.set(p.WeatherParticles); // GameScene.MapInformation -> MapControl.Weather + UpdateWeather
        void loadMap(p.FileName);
        break;
      case "MapChanged":
        miniMapIndex = p.MiniMap;
        bigMap.bigMapIndex = p.BigMap;
        autoPath = null;
        sound.playMusic(p.Music);
        mapTitle = p.Title;
        lightState.mapLights = p.Lights;
        lightState.mapDarkLight = p.MapDarkLight;
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
      case "TimeOfDay": lightState.timeOfDay = p.Lights; break;
      case "HealthChanged": hp = p.HP; mp = p.MP; break;
      case "ObjectPlayer":
      case "ObjectHero": { // GameScene.ObjectHero: HeroObject.Load = PlayerObject.Load + OwnerName
        const ob = new MapObj(p.ObjectID, ObjectType.Player, p.Location, p.Direction);
        if (p.type === "ObjectHero") ob.ownerName = p.OwnerName;
        ob.name = p.Name; ob.nameColour = p.NameColour; ob.dead = p.Dead; ob.hidden = p.Hidden; ob.light = p.Light;
        ob.guildName = p.GuildName;
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
      case "ObjectStruck": objects.get(p.ObjectID)?.queue(A.Struck, p.Direction, p.Location, undefined, struckWeaponOf(p.AttackerID)); break;
      case "Struck":
        if (!user) break;
        // GameScene.Struck: being hit interrupts the user's own Reincarnation cast
        if (user.reincarnationStopTime > now()) session.send(ClientPackets.CancelReincarnation());
        user.queue(A.Struck, user.direction, user.location, undefined, struckWeaponOf(p.AttackerID));
        break;
      // — Section 5: reincarnation (GameScene.ProcessPacket CancelReincarnation / RequestReincarnation) —
      case "CancelReincarnation": if (user) user.reincarnationStopTime = 0; break;
      case "RequestReincarnation": if (user?.dead && user.action === A.Dead) showReincarnation(); break;
      // — Section 5: mounts, transform, fishing (GameScene.MountUpdate / TransformUpdate / FishingUpdate) —
      case "MountUpdate": {
        const ob = objects.get(p.ObjectID);
        if (ob && ob.race === ObjectType.Player) {
          ob.mountUpdate(p, now());
          if (!ob.ridingMount) setEffects(ob);
          playerSound(ob, "mount", soundWorld);
        }
        if (p.ObjectID === user?.id) { canRun = false; statsDirty = true; }
        mountWindow.handle(p);
        break;
      }
      // — Section 6: the web admin's LiveDb push (GameScene.GameDataChanged) —
      case "GameDataChanged":
        applyGameDataChanged(p, {
          dispatch: (q) => handle(q),
          userMagics: () => magic.magics,
          heroMagics: () => magic.heroMagics,
          shopItems: () => gameShop.items,
          invalidateMap: (m) => gameInfo.invalidateMap(m),
        }, gameDataChanges);
        if (p.Last) { // the refresh after the last chunk: stats, skill bars, an open shop
          statsDirty = true; heroChar.invalidate();
          if (gameDataChanges.shop && gameShop.visible) gameShop.updateShop();
          gameDataChanges.magics = false; gameDataChanges.shop = false;
        }
        break;
      case "IntelligentCreaturePickup": intelligentCreaturePickupSound(objects.get(p.ObjectID), soundWorld); break; // GameScene.IntelligentCreaturePickup
      case "TransformUpdate": { const ob = objects.get(p.ObjectID); if (ob?.race === ObjectType.Player) ob.transformType = p.TransformType; break; }
      case "FishingUpdate":
        objects.get(p.ObjectID)?.fishingUpdate(p);
        if (user) { fishingWindow.handle(p, user.id); fishingStatus.handle(p, user.id); }
        break;
      case "ObjectDied": {
        const ob = objects.get(p.ObjectID);
        if (ob) { if (p.Type === 0) ob.queue(A.Die, p.Direction, p.Location); else objects.delete(p.ObjectID); }
        break;
      }
      case "Death":
        if (user) { user.dead = true; user.snap(p.Location, p.Direction); }
        showRevive();
        break;
      case "Revived":
        if (user) { user.dead = false; user.snap(user.location, user.direction); }
        closeRevive?.(); closeRevive = null;
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
    chatDialog.input.blur();
    // MapControl.OnMouseClick: Ctrl + right-click on another player inspects them
    if (e.button === 2 && e.ctrlKey && user) {
      const ob = objectAt(objects.values(), view.mapLocation(mouse.x, mouse.y, user), user);
      if (ob && ob.race === ObjectType.Player && ob !== user) { inspect.inspect(ob.id); return; }
    }
    // An inventory item is picked: a click on the map drops it (GameScene: SelectedCell + map click -> DropItem)
    if (e.button === 0 && heroInventoryWindow.mapDrop()) return; // a picked hero item dropped on the map
    const slot = inventoryWindow.pickedInventorySlot;
    if (slot >= 0 && e.button === 0) {
      // MapControl.OnMouseClick with GameScene.SelectedCell: DontDrop refusal, YesNo for one item, MirAmountBox for a stack
      const item = inventory.inventory[slot];
      const info = item ? itemInfos.get(item.ItemIndex) : undefined;
      if (item) {
        const name = info ? friendlyName(info.Name, item.Count) : "this item";
        if (info && (info.Bind & BindMode.DontDrop)) messageBox(stage, `You cannot drop ${name}`, [{ label: "OK" }]);
        else if (item.Count === 1) messageBox(stage, `Are you sure you want to drop ${name}?`, [{ label: "Yes", onClick: () => inventory.drop(slot) }, { label: "No" }]);
        else amountBox("Drop Amount:", info?.Image ?? 0, item.Count, (n) => { if (n > 0) inventory.drop(slot, n); });
      }
      inventoryWindow.clearPick();
      return;
    }
    // GameScene.PickedUpGold: MirAmountBox (Items 116) -> C.DropGold
    if (pickedUpGold && e.button === 0) {
      pickedUpGold = false;
      amountBox("Drop Amount:", 116, inventory.gold, (n) => { if (n > 0) inventory.dropGold(n); });
    }
    // Left-click on an NPC talks to it (MapControl.OnMouseClick -> C.CallNPC "[@Main]")
    if (e.button === 0 && user) {
      const target = objectAt(objects.values(), view.mapLocation(mouse.x, mouse.y, user), user);
      if (target && target.race === ObjectType.Merchant) { npcWindow.talk(target.id, target.name); return; }
    }
    buttons = e.buttons; shift = e.shiftKey;
  }, { signal });
  window.addEventListener("mouseup", (e) => { buttons = e.buttons; if (!(buttons & 2)) canRun = false; }, { signal });
  canvas.addEventListener("mousemove", (e) => { mouse = toCanvas(e); buttons = e.buttons; shift = e.shiftKey; }, { signal });
  window.addEventListener("keydown", (e) => {
    // ChatDialog opens itself on Enter / '@' / '!' / Space / '/'. While typing, only F1-F12 and Tab reach the bindings
    // below (ChatTextBox_KeyDown -> CMain_KeyDown; chatDialog.ts re-dispatches them).
    if (chatDialog.typing && !/^F([1-9]|1[0-2])$|^Tab$/.test(e.key)) return;
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
    if (matches(e, KeybindOptions.Trade) && !(document.activeElement instanceof HTMLTextAreaElement)) trade.request();
    // Tab: pick up (KeybindOptions.Pickup)
    if (matches(e, KeybindOptions.Pickup)) { e.preventDefault(); pickUp(); }
    // KeybindOptions.Ranking (K) / Minimap (V)
    if (matches(e, KeybindOptions.Ranking)) ranking.toggle();
    if (matches(e, KeybindOptions.Minimap)) miniMap.toggle();
    // Magic keys: Bar1Skill1-8 = keys 1-8 (F1-F8), Bar2Skill1-8 = 9-16 (Ctrl+F1-F8), HeroSkill1-8 = 17-24 (Shift+F1-F8)
    for (let i = 0; i < 24; i++) {
      const opt = (i < 16 ? KeybindOptions.Bar1Skill1 + i : KeybindOptions.HeroSkill1 + i - 16) as KeybindOption;
      if (!matches(e, opt)) continue;
      e.preventDefault();
      const loc = user ? view.mapLocation(mouse.x, mouse.y, user) : null;
      // HeroSkill: GameScene.UseSpell(17..24), without the user's riding / fishing gate
      if (user && loc && i >= 16) magic.useHeroSpell(i + 1, loc, objectAt(objects.values(), loc, user), mouseDirection());
      else if (user && loc && canUseSpell(user)) magic.useSpell(i + 1, loc, objectAt(objects.values(), loc, user), mouseDirection());
      return;
    }
    // Belt keys 1-6 / NumPad1-6 (KeybindOptions.Belt1..6 / Belt1Alt..6Alt -> BeltDialog.Grid[i].UseItem)
    for (let i = 0; i < BELT_IDX; i++) {
      if (matchesAny(e, (KeybindOptions.Belt1 + i * 2) as KeybindOption, (KeybindOptions.Belt1Alt + i * 2) as KeybindOption)) inventory.use(i, now());
    }
  }, { signal });
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
  /** MapObject.TargetObject for the attack spell choice: the monster being attacked. */
  let attackTargetId = 0;

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
    if (!canRun || hp < 10) return false;
    const n = runDistance(u.ridingMount);
    for (let i = 1; i <= n; i++) { const c = pointMove(u.location, dir, i); if (!emptyCell(c) || !doorOk(c)) return false; }
    u.queue(A.Running, dir, pointMove(u.location, dir, n));
    return true;
  }

  function checkInput(t: number): void {
    const u = user;
    if (!u || u.dead || chatDialog.typing) return;
    if (u.fishing) return; // MapControl.CheckInput: no input while fishing
    if (t < nextAction || t < inputDelay) return;
    // GameScene.CheckInput: nothing new while Blizzard / Reincarnation is still being cast
    if (t < u.blizzardStopTime || t < u.reincarnationStopTime) return;
    const idle = isIdleAction(u.action) && u.feed.length === 0;
    if (!idle) return;
    if (!buttons) { if (autoPath) autoPathStep(u); return; }
    autoPath = null; // MapControl.OnMouseClick: any map click ends AutoPath

    let dir = mouseDirection();
    const cell = view.mapLocation(mouse.x, mouse.y, u);
    const target = objectAt(objects.values(), cell, u);

    const archerBow = canRangeAttack(u.look.class === MirClass.Archer && u.hasClassWeapon, u); // a riding archer falls back to melee
    const rideAttack = canRideAttack(u.ridingMount, inventory.equipment[EquipmentSlot.Mount]);
    const inRange = (ob: MapObj) => Math.max(Math.abs(ob.location.X - u.location.X), Math.abs(ob.location.Y - u.location.Y)) <= MAX_ATTACK_RANGE;
    const tooFar = () => { if (t >= outputDelay) { outputDelay = t + 1000; extras.output("Target is too far."); } };
    const shoot = (direction: number, targetId: number, point: WirePoint) =>
      u.queue(A.AttackRange1, direction, u.location, { spell: Spell.None, targetId, targetPoint: { ...point }, cast: false, level: 0, secondaryTargetIds: [] });

    if (buttons & 1) {
      if (!(target && (target.race === ObjectType.Merchant || (target.race === ObjectType.Player && target !== u)))) {
        // MapControl.CheckInput: Alt + left = harvest a corpse
        if (altHeld && canHarvest(u)) { u.queue(A.Harvest, dir, u.location); return; }
        // Shift + left with a bow: a shot towards the mouse (at the object under it when in range)
        if (shift && archerBow) {
          if (t <= attackTime) return;
          const ob = target && !target.dead && (target.race === ObjectType.Monster || target.race === ObjectType.Player) ? target : null;
          if (ob && !inRange(ob)) { tooFar(); return; }
          shoot(dir, ob?.id ?? 0, pointMove(u.location, dir, 9));
          return;
        }
        // A monster with a bow: the ranged attack (ArcherTest - range attack)
        if (archerBow && target && !target.dead && target.race === ObjectType.Monster) {
          if (!inRange(target)) { tooFar(); return; }
          attackTargetId = target.id;
          if (t > attackTime) shoot(directionFromPoint(u.location, target.location), target.id, target.location);
          return;
        }
      }
      // Clicking the user's own cell picks up (MapControl.CheckInput)
      if (cell.X === u.location.X && cell.Y === u.location.Y) { pickUp(); return; }
      const adjacent = target && !target.dead && target.race === ObjectType.Monster && Math.max(Math.abs(target.location.X - u.location.X), Math.abs(target.location.Y - u.location.Y)) === 1;
      if (shift || adjacent) {
        if (adjacent) dir = directionFromPoint(u.location, target!.location);
        attackTargetId = adjacent ? target!.id : 0;
        if (t > attackTime && rideAttack) u.queue(A.Attack1, dir, u.location);
        return;
      }
      if (target && (target.race === ObjectType.Merchant || target.race === ObjectType.Player)) return;
      for (const d of [dir, (dir + 1) & 7, (dir + 7) & 7]) {
        const to = pointMove(u.location, d, 1);
        if (emptyCell(to) && !u.inTrapRock) { // MapControl.CanWalk: no walking while InTrapRock
          if (doorOk(to)) u.queue(A.Walking, d, to); // CanWalk picks the direction, then CheckDoorOpen
          else if (d !== u.direction) u.queue(A.Standing, d, u.location);
          return;
        }
      }
      if (dir !== u.direction) { u.queue(A.Standing, dir, u.location); return; }
      // MapControl.CheckInput: facing water with a rod casts out (GameScene.CanFish)
      if (canFish(u, dir, map, t)) { u.fishingTime = t; session.send(ClientPackets.FishingCast({ CastOut: true })); return; }
    } else if (buttons & 2) {
      if (!u.inTrapRock && tryRun(u, dir)) return;
      const to = pointMove(u.location, dir, 1);
      if (emptyCell(to) && !u.inTrapRock && doorOk(to)) { u.queue(A.Walking, dir, to); return; }
      if (dir !== u.direction) u.queue(A.Standing, dir, u.location);
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
    g2.textBaseline = "alphabetic";
  }

  let hoverId = -1;
  let shiftHeld = false, altHeld = false;
  let outputDelay = 0; // MapControl.OutputDelay ("Target is too far.")
  const MAX_ATTACK_RANGE = 9; // Globals.MaxAttackRange
  window.addEventListener("keydown", (e) => { if (e.key === "Shift") shiftHeld = true; if (e.key === "Alt") { altHeld = true; e.preventDefault(); } }, { signal });
  window.addEventListener("keyup", (e) => { if (e.key === "Shift") shiftHeld = false; if (e.key === "Alt") altHeld = false; }, { signal });
  window.addEventListener("blur", () => { shiftHeld = altHeld = false; }, { signal });

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
    extras.update(user?.location ?? null);
    characterWindow.render();
    checkInput(t);
    magicBar.tick(t);
    heroMagicWindow.tick(t);
    heroPanels.update(t);
    heroChar.render();
    buffBar.update(userBuffs, t);
    if (user) {
      const cell = view.mapLocation(mouse.x, mouse.y, user);
      hoverId = objectAt(objects.values(), cell, user)?.id ?? -1;
      weather.update(t, view.painter.width, view.painter.height);
      view.render({ map, user, objects: objects.values(), animationCount, lights: lightState, mapEffects, doors, now: t });
      miniMap.draw(map, miniMapIndex, mapTitle, user, objects.values(), (n) => group.has(n));
      bigMap.draw(map, mapTitle, user, objects.values(), group);
      drawOverlay(t);
    }
    frames++;
    if (t - fpsAt >= 1000) { fps = frames; frames = 0; fpsAt = t; }
    // MainDialog.Process (+ MiniMapDialog's placement of the mode labels and DuraStatusDialog)
    if (user) {
      mainDialog.update({
        name: user.name, cls: user.look.class, level, hp, mp, maxHp: userStats?.stats[Stat.HP] ?? 0, maxMp: userStats?.stats[Stat.MP] ?? 0,
        experience, maxExperience, gold: inventory.gold, freeSlots: inventory.inventory.filter((it) => it == null).length,
        bagWeight: userStats?.currentBagWeight ?? 0, maxBagWeight: userStats?.stats[Stat.BagWeight] ?? 0, aMode, pMode, miniMapHeight: miniMap.height,
      });
    }
    duraPanel.place(miniMap.location[0], miniMap.height);
    duraPanel.update();
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
    setUiSound(null);
    renderer?.clear();
  }

  (window as unknown as { crystalGame: unknown }).crystalGame = {
    objects, get user() { return user; }, get map() { return map; }, view, step, doors, miniMap, bigMap, group, trade, guild, gameInfo, market, storageWindow, npcServices, itemChanges, rental, relationship, mentor, inspect, ranking, mail, friends, gameShop, territories, optionWindow, helpWindow, chatOptions, keyboardLayout, weather, creatures, creatureWindow, mountWindow, fishingWindow, fishingStatus, hero, heroItems, heroInventoryWindow, heroChar, heroPanels, heroMagicWindow, quests, questWindow, get autoPath() { return autoPath; }, lightState, itemInfos, inventory, inventoryWindow, npcWindow, magic, magicBar, mapEffects, sound, get userBuffs() { return userBuffs; },
    mainDialog, chatDialog, menuDialog, duraPanel, extras, get fps() { return fps; }, get loading() { return loadingCount(); },
    /** Debug/test hook: hold a mouse button at a canvas pixel (buttons: 1 left, 2 right, 0 release). */
    mouse(x: number, y: number, b: number) { mouse = { x, y }; buttons = b; if (!(b & 2)) canRun = false; },
  };
  return { handle, dispose };
}
