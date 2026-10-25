// The classic game UI: the desktop-look windows (src/ui/*.ts, the GameScene dialogs with the desktop .Lib artwork)
// behind the GameUi seam (gameUi.ts). createClassicUi builds them in the GameScene ctor's order that game.ts used, wires
// the cross-window hooks, and feeds them the packets and frame calls game.ts used to make itself. Window listeners
// register in the same order as before (they run before game.ts's key router, which is created after this). Since
// phase 1b the packet-driven state lives in src/game models (chatLog, hudState, account, currentMap, itemPick,
// heroStats, mount, fishing, group, trade, rental, creatures; since phase 1c also the NPC session, recipes, awakening,
// market, guild, mail, friends, relationship, mentor, inspect, rankings, shop and territories): game.ts feeds them, and
// the windows here are views that subscribe to their events.

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, MirClassName, MirGridType, Stat, StatName } from "../protocol/enums.generated.ts";
import { KeybindOptions, keyLabel, matches, type KeybindOption } from "../game/keybinds.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { ItemDrop } from "../game/itemDrop.ts";
import type { InputRouter } from "../game/input.ts";
import { setUiSound } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { InventoryWindow, classicDropPrompts } from "./inventoryWindow.ts";
import { NpcWindow } from "./npcWindow.ts";
import { MagicBar } from "./magicBar.ts";
import { BuffBar } from "./buffBar.ts";
import { MainDialog } from "./mainDialog.ts";
import { MenuDialog } from "./menuDialog.ts";
import { ChatDialog } from "./chatDialog.ts";
import { DuraPanel } from "./duraPanel.ts";
import { MiniMap } from "./miniMap.ts";
import { BigMap } from "./bigMap.ts";
import { Group } from "./groupWindow.ts";
import { TradeWindow } from "./tradeWindow.ts";
import { HudExtras } from "./hudExtras.ts";
import { MarketWindow } from "./marketWindow.ts";
import { StorageWindow } from "./storageWindow.ts";
import { NpcServices } from "./npcServices.ts";
import { SocketWindow } from "./socketWindow.ts";
import { RentalWindow } from "./rentalWindow.ts";
import { RelationshipWindow, MentorWindow } from "./relationshipWindow.ts";
import { InspectWindow } from "./inspectWindow.ts";
import { RankingWindow } from "./rankingWindow.ts";
import { MailWindow } from "./mailWindow.ts";
import { FriendWindow } from "./friendWindow.ts";
import { GameShopWindow } from "./gameShopWindow.ts";
import { CreatureWindow } from "./creatureWindow.ts";
import { HeroMagicWindow } from "./heroMagicWindow.ts";
import { HeroPanels } from "./heroPanels.ts";
import { HeroManageWindow, NewHeroWindow } from "./heroManageWindow.ts";
import { HeroCharacterWindow } from "./heroCharacterWindow.ts";
import { OptionWindow } from "./optionWindow.ts";
import { HelpWindow, HELP_DEFAULT_KEYS } from "./helpWindow.ts";
import { ChatOptionWindow } from "./chatOptionWindow.ts";
import { installClickSounds } from "./clickSounds.ts";
import { KeyboardLayoutWindow } from "./keyboardLayoutWindow.ts";
import { HeroInventoryWindow } from "./heroInventoryWindow.ts";
import { MountWindow } from "./mountWindow.ts";
import { FishingWindow, FishingStatusWindow } from "./fishingWindow.ts";
import { GuildTerritoryWindow } from "./guildTerritoryWindow.ts";
import { QuestWindow } from "./questWindow.ts";
import { CharacterWindow } from "./characterWindow.ts";
import { Guild } from "./guildWindow.ts";
import type { GameUi, GameUiContext, UiEvent, UiCommand, FramePoint } from "./gameUi.ts";

/** GameScene.ChangeAMode / ChangePMode chat texts (MainDialog's mode labels); game.ts reads them through here. */
export { AMODE_TEXT, PMODE_TEXT } from "./mainDialog.ts";

/** ItemInfo.FriendlyName + UserItem.FriendlyName's " (count)". */
const friendlyName = (info: Parameters<typeof itemFriendlyName>[0], count: number) => itemFriendlyName(info) + (count > 1 ? ` (${count})` : "");

export function createClassicUi(ctx: GameUiContext): GameUi {
  const { stage, screen, session, sound, player, world, actions } = ctx;
  const {
    itemInfos, gameInfo, inventory, hero, heroItems, itemChanges, magic, quests, creatures, chatLog, hudState, account, currentMap,
    itemPick, heroStats, mount, fishing, group: groupState, trade: tradeState, rental: rentalState,
    npcSession, recipes, awakening, market: marketState, guild: guildState, mail: mailBox, friends: friendList,
    relationship: relationshipState, mentor: mentorState, inspect: inspectState, rankings, shop: shopState, territories: territoryState,
  } = ctx.models;
  const [width] = screen;
  const send = (f: Uint8Array) => ctx.send(f);
  const chat = (text: string, type: number) => chatLog.receive(text, type);
  const userName = () => player.user?.name ?? "";

  // — HUD (GameScene ctor): MainDialog, ChatDialog + ChatControlBar, MenuDialog; the rest below —
  const mainDialog = new MainDialog(screen, {
    inventory: () => inventoryWindow.toggle(),
    character: () => characterWindow.openPage("character"),
    skills: () => magicBar.toggle(),
    quests: () => questWindow.toggleDiary(),
    options: () => optionWindow.toggle(),
    menu: () => menuDialog.toggle(),
    gameShop: () => gameShop.toggle(),
    gold: () => itemPick.toggleHudGold(inventory.gold), // GameScene.PickedUpGold (itemPick.ts hudGold)
  });
  const chatDialog = new ChatDialog({
    screen, mainX: mainDialog.location[0], log: chatLog,
    describe: (item) => inventoryWindow.describe(item),
    chatOptions: () => chatOptions.toggle(),
    trade: () => trade.request(),
  });
  const menuDialog = new MenuDialog(width, mainDialog.location[1], {
    exit: () => actions.logOut(true), logOut: () => actions.logOut(false),
    help: () => helpWindow.toggle(), keyboard: () => keyboardLayout.toggle(), ranking: () => ranking.toggle(),
    creatures: () => creatureWindow.toggle(), mount: () => mountWindow.toggle(), fishing: () => fishingWindow.toggle(),
    friends: () => friends.toggle(), mentor: () => mentor.toggle(), relationship: () => relationship.toggle(),
    group: () => group.toggle(), guild: () => guild.toggle(),
  });

  const inventoryWindow = new InventoryWindow(stage, inventory, () => performance.now(), itemPick);
  const npcWindow = new NpcWindow(stage, npcSession, inventory, itemInfos, inventoryWindow, gameInfo, currentMap);
  /** TrustMerchantDialog (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess; closes with the NPC dialog). */
  const market = new MarketWindow(stage, marketState, inventory, inventoryWindow, npcSession);
  /** StorageDialog (opened by S.NPCStorage; the items live in inventory.storage). */
  const storageWindow = new StorageWindow(stage, inventoryWindow, send, (t, ty) => chat(t, ty), npcSession, account);
  /** NPC item services: refine, wedding ring, crafting, awakening, disassemble / downgrade / reset, pearl shop. */
  const npcServices = new NpcServices({
    stage, send, inv: inventory, infos: itemInfos, invWindow: inventoryWindow, npc: npcWindow, session: npcSession, recipes, awakening,
    chat: (t, ty) => chat(t, ty), stat: (st) => player.stats?.stats[st] ?? 0,
  });
  /** Sockets / mount and fishing slots, seal, combine, upgrade, rental items (itemChanges.ts) + their windows. */
  inventoryWindow.changes = itemChanges; // the bag's slot-item cells (was set by the SocketWindow ctor)
  new SocketWindow(stage, inventory, inventoryWindow, itemChanges, (i) => gameInfo.requestItem(i));
  const rental = new RentalWindow(stage, inventory, inventoryWindow, itemChanges, rentalState, userName);
  /** MountDialog, FishingDialog, FishingStatusDialog (J / N / Esc are registered by the windows; M rides via bindInput). */
  const mountWindow = new MountWindow(stage, inventory, inventoryWindow, itemChanges, mount, (i) => gameInfo.requestItem(i));
  const fishingWindow = new FishingWindow(stage, inventory, inventoryWindow, fishing, (i) => gameInfo.requestItem(i));
  const fishingStatus = new FishingStatusWindow(stage, inventoryWindow, fishing); // after FishingWindow: same subscription order
  const magicBar = new MagicBar(stage, magic, send);
  /** Hero skills (Ctrl+S, registered by the window; Shift+F1-F8 cast for the hero). */
  const heroMagicWindow = new HeroMagicWindow(stage, magic);
  setUiSound((id) => sound.play(id));
  /** GameScene.BuffsDialog. */
  const buffBar = new BuffBar(stage, { screenWidth: width, guildStats: () => guildBuffText() });
  // quests / lightState are read only from the frame loop, after they exist
  const miniMap = new MiniMap(stage, {
    questIcon: (id) => quests.npcQuestIcon(id), timeOfDay: () => world.lights.timeOfDay, screenWidth: width,
    openMail: () => mail.toggle(), openBigMap: () => bigMap.toggle(), newMail: () => mail.newMail,
  });
  /** DuraStatusDialog + CharacterDuraPanel (under the minimap). */
  const duraPanel = new DuraPanel(width, miniMap.location[0], miniMap.height, {
    equipment: () => inventory.equipment, info: (item) => inventory.info(item),
  });
  /** OptionDialog (F12 / O), HelpDialog (H), ChatOptionDialog; MirButton click sounds on every stage button. */
  const optionWindow = new OptionWindow(stage, send, (t, ty) => chat(t, ty), sound, account);
  optionWindow.onLogOut = () => actions.logOut(false, true); // "New interface": log out now (combat lock kept)
  const helpWindow = new HelpWindow(stage, (o) => { // the shortcut pages show the live bindings (keybinds.ts)
    const opt = (KeybindOptions as Record<string, KeybindOption | undefined>)[o];
    return opt === undefined ? HELP_DEFAULT_KEYS[o] ?? "" : keyLabel(opt);
  });
  const chatOptions = new ChatOptionWindow(stage); // ChatDialog follows the [Filters] / TransparentChat settings itself
  const keyboardLayout = new KeyboardLayoutWindow(stage); // KeyboardLayoutDialog (U), rebinding through keybinds.ts
  installClickSounds(stage, (id) => sound.play(id));
  const bigMap = new BigMap(stage, gameInfo, send, () => inventory.gold, currentMap);
  const group = new Group(stage, groupState, (t, ty) => chat(t, ty), userName);
  bigMap.onWalk = (cell) => world.walkTo(cell); // MapControl.AutoPath
  const trade = new TradeWindow(stage, inventory, inventoryWindow, tradeState, userName);
  const guild = new Guild(stage, guildState, send, (t, ty) => chat(t, ty), userName, () => performance.now(),
    () => chatDialog.setText("!~"), inventoryWindow, () => inventory.gold);
  const extras = new HudExtras({ stage, state: hudState, now: () => performance.now(), screen });
  // RollDialog.ReturnResult: C.CallNPC "[page]" through the NPC dialog (GameScene.NPCID, NPCTime)
  const offRoll = hudState.rollDone.on(({ page }) => npcSession.callPage(page));
  ctx.signal.addEventListener("abort", offRoll);
  const characterWindow = new CharacterWindow(stage, () => {
    const user = player.user;
    return {
      name: user?.name ?? "", cls: user?.look.class ?? 0, level: player.level, hp: player.hp, mp: player.mp,
      experience: player.experience, maxExperience: player.maxExperience, stats: player.stats,
      gender: user?.look.gender, hair: user?.look.hair, guildName: user?.guildName,
    };
  });
  // ItemLabel context for the inventory's item labels (GameScene.CreateItemLabel)
  inventoryWindow.labelContext = () => ({
    info: (i) => itemInfos.get(i.ItemIndex), realItem: (info) => itemInfos.realItem(info, player.level, player.user?.look.class ?? 0),
    level: player.level, job: player.user?.look.class ?? 0, gender: player.user?.look.gender ?? 0, stat: (k) => player.stats?.stats[k] ?? 0,
  });
  // ChatDialog.LinkedItems: Shift+right-click puts "<FriendlyName> " in the chat box (MirItemCell)
  inventoryWindow.onLink = (item) => {
    const info = itemInfos.get(item.ItemIndex);
    if (!info) return;
    if (!chatDialog.addLink(item.UniqueID, friendlyName(info, item.Count), MirGridType.Inventory)) chat("Unable to link item, message exceeds allowed length", ChatType.System);
  };
  inventoryWindow.notify = (text) => chat(text, ChatType.System);

  /** GuildDialog.UpdateActiveStats: the Guild buff's hint lines (ClientTextKeys.ValueByOwnerPercent). */
  function guildBuffText(): string {
    let text = "";
    for (const [k, v] of Object.entries(player.guildBuffStats ?? {})) {
      if (!v) continue;
      const name = StatName[Number(k)] ?? k;
      text += `${v < 0 ? "Decreases" : "Increases"} ${name} by: ${v}${name.includes("Percent") ? "%" : ""}.\n`;
    }
    return text;
  }

  let closeRevive: (() => void) | null = null;
  /** GameScene: the DiedTip box once the user is dead (Yes = C.TownRevive while still dead). */
  function showRevive(): void {
    closeRevive?.();
    setTimeout(() => {
      if (ctx.signal.aborted || !player.user?.dead) return;
      closeRevive = messageBox(stage, "You have died, Do you want to revive in town?", [
        { label: "Yes", primary: true, onClick: () => { closeRevive = null; if (player.user?.dead) session.send(ClientPackets.TownRevive()); } },
        { label: "No", onClick: () => { closeRevive = null; } },
      ]);
    }, 1500);
  }
  /** GameScene.RequestReincarnation: a Taoist offers to revive the dead user (Yes = C.AcceptReincarnation); like the
   *  desktop, an offer that arrives during the death animation is dropped (game.ts checks). */
  function showReincarnation(): void {
    closeRevive?.();
    closeRevive = messageBox(stage, "Would you like to be revived?", [
      { label: "Yes", primary: true, onClick: () => { closeRevive = null; if (player.user?.dead) session.send(ClientPackets.AcceptReincarnation()); } },
      { label: "No", onClick: () => { closeRevive = null; } },
    ]);
  }
  /** Hero panels (info, menu, behaviour, buffs, summon), hero character (Ctrl+C), manage + create dialogs. */
  const heroChar = new HeroCharacterWindow(stage, hero, heroStats);
  /** The hero's bag, belt (9 / 0), equipment and auto-pot (Ctrl+I); cells share the inventory pick. */
  const heroInventoryWindow = new HeroInventoryWindow(stage, hero, heroItems, inventoryWindow, () => performance.now());
  /** MapControl.OnMouseClick with a picked item (itemDrop.ts): hero bag item, bag item, picked HUD gold. */
  const itemDrop = new ItemDrop({ pick: itemPick, inventory, itemInfos, hero, heroItems, clearPick: () => inventoryWindow.clearPick() }, classicDropPrompts);
  const openHeroInventory = (): void => heroInventoryWindow.toggle();
  const heroPanels = new HeroPanels(stage, hero, send, {
    openSkills: () => heroMagicWindow.toggle(),
    openInventory: () => openHeroInventory(),
    openCharacter: () => heroChar.toggle(),
  }, {
    main: mainDialog.win, mainLocation: mainDialog.location, screen,
    heroStats, itemImage: (i) => itemInfos.get(i)?.Image,
  });
  new HeroManageWindow(stage, hero, send);
  new NewHeroWindow(stage, hero, send);
  /** Quests: the NPC quest list, details, log (Q), tracking panel, quest items (state: quests.ts). */
  const questWindow = new QuestWindow(stage, quests, npcWindow, npcSession, inventoryWindow, itemInfos, gameInfo);
  questWindow.onHelp = () => helpWindow.displayPageNamed("Quests");
  questWindow.notify = (text) => chat(text, ChatType.System); // S.QuestRefused
  questWindow.bindGroup(groupState); // Share needs a group
  mountWindow.onHelp = () => helpWindow.displayPageNamed("Mounts");
  npcWindow.onHelp = () => helpWindow.displayPageNamed("Purchasing");
  inventoryWindow.onQuestTab = () => questWindow.toggleItems(true);
  // — Section 4: social windows (RankingWindow renders in its constructor and reads the user) —
  const prefillChat = (text: string) => chatDialog.setText(text);
  const relationship = new RelationshipWindow(stage, relationshipState, (t, ty) => chat(t, ty), prefillChat);
  const mentor = new MentorWindow(stage, mentorState, () => ({ name: userName(), level: player.level, cls: player.user?.look.class ?? 0 }));
  const inspect = new InspectWindow(stage, inspectState, prefillChat, inventoryWindow, gameInfo, account);
  const ranking = new RankingWindow(stage, rankings, userName, inspectState);
  const mail = new MailWindow(stage, mailBox, inventory, inventoryWindow, (t, ty) => chat(t, ty));
  inspect.composeMail = (n) => mail.composeLetter(n);
  market.mailTo = (name, msg) => mail.composeLetter(name, msg);
  mail.onHelp = () => helpWindow.toggle(true);
  const friends = new FriendWindow(stage, friendList, (t, ty) => chat(t, ty), (name) => mail.composeLetter(name), prefillChat);
  /** S.FriendRequest: a yes/no box per requester (Yes = accept, No = decline), closed when the 60 s request runs out,
   *  is settled elsewhere or the requester asks again. Results need no echo: the server sends a chat line with every S.FriendRequestResult. */
  const friendPrompts = new Map<string, { close: () => void; timer: ReturnType<typeof setTimeout> }>();
  const closeFriendPrompt = (key: string) => {
    const open = friendPrompts.get(key);
    if (!open) return;
    friendPrompts.delete(key);
    clearTimeout(open.timer);
    open.close();
  };
  const offFriendRequest = friendList.request.on((r) => {
    const key = r.name.toLowerCase();
    closeFriendPrompt(key);
    const cls = (MirClassName[r.cls] ?? "").toLowerCase();
    const answer = (accept: boolean) => () => {
      const open = friendPrompts.get(key);
      if (open) { clearTimeout(open.timer); friendPrompts.delete(key); }
      friendList.reply(r.name, accept);
    };
    const close = messageBox(stage, `${r.name} (${cls ? `${cls}, ` : ""}level ${r.level}) wants to add you as a friend.`, [
      { label: "Yes", primary: true, onClick: answer(true) }, { label: "No", onClick: answer(false) }]);
    const timer = setTimeout(() => closeFriendPrompt(key), Math.max(0, r.expires - performance.now()));
    friendPrompts.set(key, { close, timer });
  });
  // a request settled elsewhere (crossed accept, a list update, expiry) closes its box
  const offFriendRequests = friendList.requestsChanged.on(() => {
    if (friendPrompts.size === 0) return;
    const live = new Set(friendList.requests.map((r) => r.name.toLowerCase()));
    for (const key of [...friendPrompts.keys()]) if (!live.has(key)) closeFriendPrompt(key);
  });
  ctx.signal.addEventListener("abort", () => {
    offFriendRequest();
    offFriendRequests();
    for (const key of [...friendPrompts.keys()]) closeFriendPrompt(key);
  });
  relationship.composeMail = (n) => mail.composeLetter(n);
  /** Section 5: game shop (Y / Shop button) and the guild territory list (opened by the server). */
  const gameShop = new GameShopWindow(stage, shopState, inventoryWindow, (t, ty) => chat(t, ty),
    () => inventory.gold, () => inventory.credit, () => player.user?.look.class ?? 0);
  const territories = new GuildTerritoryWindow(stage, territoryState, (name) => mail.composeLetter(name));
  /** IntelligentCreatureDialog (E; X / Alt+A pick up at the mouse cell, via bindInput). */
  const creatureWindow = new CreatureWindow(stage, creatures, send, (t, ty) => chat(t, ty), () => world.mouseCell());

  /** The window half of GameScene.ProcessPacket, in game.ts's old order (the models game.ts runs first took rental,
   *  AllowObserve, creatures, the HUD packets, group, trade and, since phase 1c, the social, shop, guild and NPC
   *  packets out of this chain; the territory and market models run right after it). Never consumes. */
  function handle(p: ServerPacket): boolean {
    questWindow.handle(p);
    storageWindow.handle(p);
    gameShop.process(); // GameShopDialog.Process: the money labels, on every packet that reached the old window
    npcServices.handle(p);
    bigMap.handle(p);
    return market.handle(p);
  }

  function notify(e: UiEvent): void {
    switch (e.kind) {
      case "itemInfo": inventoryWindow.render(); break;
      case "gameDataApplied":
        if (e.shopChanged && gameShop.visible) gameShop.updateShop();
        break;
    }
  }

  /** game.ts step()'s HUD rows, at their old points. */
  function frame(t: number, at: FramePoint): void {
    switch (at) {
      case "beforeInput":
        extras.update(player.user?.location ?? null);
        characterWindow.render();
        break;
      case "afterInput":
        magicBar.tick(t);
        heroMagicWindow.tick(t);
        heroPanels.update(t);
        heroChar.render();
        buffBar.update(player.buffs, t);
        break;
      case "afterRender": {
        const user = player.user;
        if (!user) break;
        miniMap.draw(world.map, world.miniMapIndex, world.mapTitle, user, world.objects.values(), (n) => groupState.has(n));
        bigMap.draw(world.map, world.mapTitle, user, world.objects.values(), groupState);
        break;
      }
      case "end": {
        // MainDialog.Process (+ MiniMapDialog's placement of the mode labels and DuraStatusDialog)
        const user = player.user;
        if (user) {
          const stats = player.stats;
          mainDialog.update({
            name: user.name, cls: user.look.class, level: player.level, hp: player.hp, mp: player.mp,
            maxHp: stats?.stats[Stat.HP] ?? 0, maxMp: stats?.stats[Stat.MP] ?? 0,
            experience: player.experience, maxExperience: player.maxExperience, gold: inventory.gold,
            freeSlots: inventory.inventory.filter((it) => it == null).length,
            bagWeight: stats?.currentBagWeight ?? 0, maxBagWeight: stats?.stats[Stat.BagWeight] ?? 0,
            aMode: player.aMode, pMode: player.pMode, miniMapHeight: miniMap.height,
          });
        }
        duraPanel.place(miniMap.location[0], miniMap.height);
        duraPanel.update();
        break;
      }
    }
  }

  /** MapControl.OnMouseClick with a picked item (left button): hero bag item, bag item (GameScene.SelectedCell), picked
   *  HUD gold, in that order (itemDrop.ts). */
  function mapMouseDown(e: MouseEvent): boolean {
    if (e.button !== 0) return false;
    return itemDrop.mapClick();
  }

  function command(c: UiCommand): void {
    switch (c.kind) {
      case "trade": trade.request(); break;
      case "toggle": if (c.window === "ranking") ranking.toggle(); else miniMap.toggle(); break;
      case "inspect": inspect.inspect(c.objectId); break;
      case "talk": npcSession.talk(c.objectId, c.name); break; // MapControl NPC click: C.CallNPC "[@Main]"
    }
  }

  /** The gameplay keys that used to sit in window listeners (the windows keep their toggle / Escape keys). */
  function bindInput(input: InputRouter): void {
    // MountDialog: KeybindOptions.Mount rides (MountDialog.CanRide + Ride); same else-if chain as the window's listener
    input.register("gameplay", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (!matches(e, KeybindOptions.MountWindow) && matches(e, KeybindOptions.Mount) && mount.canRide()) mount.ride();
    });
    // GameScene KeybindOptions.CreaturePickup (X) / CreatureAutoPickup (Alt+A); same else-if chain as the window's listener
    input.register("gameplay", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Creature) && !e.repeat) return;
      if (matches(e, KeybindOptions.CreaturePickup)) creatureWindow.pickup(true);
      else if (matches(e, KeybindOptions.CreatureAutoPickup)) { e.preventDefault(); creatureWindow.pickup(false); }
    });
  }

  return {
    style: "Classic",
    handle,
    notify,
    frame,
    confirm: (text, yes, opts) => messageBox(stage, text, [
      { label: "Yes", primary: opts?.primary, onClick: yes }, { label: "No", onClick: opts?.no }]),
    amount: (title, image, max, ok) => amountBox(title, image, max, ok),
    revive: (show) => { if (show) showRevive(); else { closeRevive?.(); closeRevive = null; } },
    reincarnation: () => showReincarnation(),
    get typing() { return chatDialog.typing; },
    blurChat: () => chatDialog.input.blur(),
    prefillChat,
    mapMouseDown,
    command,
    bindInput,
    debug: {
      miniMap, bigMap, group, trade, guild, market, storageWindow, npcServices, rental, relationship, mentor, inspect, ranking, mail, friends,
      gameShop, territories, optionWindow, helpWindow, chatOptions, keyboardLayout, creatureWindow, mountWindow, fishingWindow, fishingStatus,
      heroInventoryWindow, heroChar, heroPanels, heroMagicWindow, questWindow, inventoryWindow, npcWindow, magicBar, mainDialog, chatDialog,
      menuDialog, duraPanel, extras, characterWindow, buffBar,
    },
    dispose: () => setUiSound(null),
  };
}
