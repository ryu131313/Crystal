// The classic game UI: the desktop-look windows (src/ui/*.ts, the GameScene dialogs with the desktop .Lib artwork)
// behind the GameUi seam (gameUi.ts). createClassicUi builds them in the GameScene ctor's order that game.ts used, wires
// the cross-window hooks, and feeds them the packets and frame calls game.ts used to make itself. Window listeners
// register in the same order as before (they run before game.ts's key router, which is created after this).

import type { ServerPacket, GameShopItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, MirGridType, BindMode, EquipmentSlot, Stat, StatName } from "../protocol/enums.generated.ts";
import { KeybindOptions, keyLabel, matches, type KeybindOption } from "../game/keybinds.ts";
import { itemFriendlyName } from "../game/gameInfo.ts";
import { canWearItem } from "../game/canWear.ts";
import type { InputRouter } from "../game/input.ts";
import { setUiSound } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { InventoryWindow } from "./inventoryWindow.ts";
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
  const { itemInfos, gameInfo, inventory, hero, heroItems, itemChanges, magic, quests, creatures, chatItems } = ctx.models;
  const [width] = screen;
  const send = (f: Uint8Array) => ctx.send(f);
  const chat = (text: string, type: number) => chatDialog.receiveChat(text, type);
  const userName = () => player.user?.name ?? "";

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
  const menuDialog = new MenuDialog(width, mainDialog.location[1], {
    exit: () => actions.logOut(true), logOut: () => actions.logOut(false),
    help: () => helpWindow.toggle(), keyboard: () => keyboardLayout.toggle(), ranking: () => ranking.toggle(),
    creatures: () => creatureWindow.toggle(), mount: () => mountWindow.toggle(), fishing: () => fishingWindow.toggle(),
    friends: () => friends.toggle(), mentor: () => mentor.toggle(), relationship: () => relationship.toggle(),
    group: () => group.toggle(), guild: () => guild.toggle(),
  });

  const inventoryWindow = new InventoryWindow(stage, inventory, () => performance.now());
  const npcWindow = new NpcWindow(stage, session, inventory, itemInfos, inventoryWindow, () => performance.now(), gameInfo);
  /** TrustMerchantDialog (NPCMarket / NPCMarketPage / MarketFail / MarketSuccess; closes with the NPC dialog). */
  const market = new MarketWindow(stage, inventory, inventoryWindow, send, (t, ty) => chat(t, ty), () => performance.now(), npcWindow);
  /** StorageDialog (opened by S.NPCStorage; the items live in inventory.storage). */
  const storageWindow = new StorageWindow(stage, inventoryWindow, send, (t, ty) => chat(t, ty), npcWindow);
  /** NPC item services: refine, wedding ring, crafting, awakening, disassemble / downgrade / reset, pearl shop. */
  const npcServices = new NpcServices({
    stage, send, inv: inventory, infos: itemInfos, invWindow: inventoryWindow, npc: npcWindow,
    chat: (t, ty) => chat(t, ty), stat: (st) => player.stats?.stats[st] ?? 0,
  });
  /** Sockets / mount and fishing slots, seal, combine, upgrade, rental items (itemChanges.ts) + their windows. */
  inventoryWindow.changes = itemChanges; // the bag's slot-item cells (was set by the SocketWindow ctor)
  new SocketWindow(stage, inventory, inventoryWindow, itemChanges, (i) => gameInfo.requestItem(i));
  const rental = new RentalWindow(stage, inventory, inventoryWindow, itemChanges, send,
    (t, ty) => chat(t, ty), userName, (i) => gameInfo.requestItem(i), () => performance.now());
  /** MountDialog, FishingDialog, FishingStatusDialog (J / N / Esc are registered by the windows; M rides via bindInput). */
  const mountWindow = new MountWindow(stage, inventory, inventoryWindow, itemChanges, send,
    () => { const u = player.user; return u && { id: u.id, mountType: u.mountType, ridingMount: u.ridingMount, action: u.action, fishing: u.fishing }; },
    (i) => gameInfo.requestItem(i), () => performance.now());
  const fishingWindow = new FishingWindow(stage, inventory, inventoryWindow, send, (i) => gameInfo.requestItem(i), () => performance.now());
  const fishingStatus = new FishingStatusWindow(stage, inventory, inventoryWindow, send);
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
  const optionWindow = new OptionWindow(stage, send, (t, ty) => chat(t, ty), sound);
  const helpWindow = new HelpWindow(stage, (o) => { // the shortcut pages show the live bindings (keybinds.ts)
    const opt = (KeybindOptions as Record<string, KeybindOption | undefined>)[o];
    return opt === undefined ? HELP_DEFAULT_KEYS[o] ?? "" : keyLabel(opt);
  });
  const chatOptions = new ChatOptionWindow(stage); // ChatDialog follows the [Filters] / TransparentChat settings itself
  const keyboardLayout = new KeyboardLayoutWindow(stage); // KeyboardLayoutDialog (U), rebinding through keybinds.ts
  installClickSounds(stage, (id) => sound.play(id));
  const bigMap = new BigMap(stage, gameInfo, send, () => inventory.gold);
  const group = new Group(stage, send, (t, ty) => chat(t, ty), userName);
  bigMap.onWalk = (cell) => world.walkTo(cell); // MapControl.AutoPath
  const trade = new TradeWindow(stage, inventory, inventoryWindow, send, (t, ty) => chat(t, ty), userName);
  const guild = new Guild(stage, send, (t, ty) => chat(t, ty), userName, () => performance.now(),
    () => chatDialog.setText("!~"), inventoryWindow, () => inventory.gold,
    (stats) => actions.setGuildBuff(stats));
  const extras = new HudExtras({
    stage, send, now: () => performance.now(), play: (id) => sound.play(id), screen,
    callNpcPage: (page) => npcWindow.callPage(page),
  });
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
  const heroChar = new HeroCharacterWindow(stage, hero, itemInfos, () => performance.now(), () => magic.heroMagics);
  /** The hero's bag, belt (7 / 8), equipment and auto-pot (Ctrl+I); cells share the inventory pick. */
  const heroInventoryWindow = new HeroInventoryWindow(stage, hero, heroItems, inventoryWindow, () => performance.now());
  // MirItemCell.CanWearItem with the hero as the wearer (UserHeroObject stats from heroCharacterWindow; a model hook set
  // here until the hero stats leave the window, phase 1b heroStats.ts)
  heroItems.canWear = (item, slot) => {
    const h = hero.info, stats = heroChar.stats(), info = itemInfos.get(item.ItemIndex);
    if (!h || !stats || !info) return null; // no data yet: let the server decide
    const cur = h.equipment[slot] ?? null, curInfo = cur ? itemInfos.get(cur.ItemIndex) : undefined;
    const weapon = h.equipment[EquipmentSlot.Weapon];
    return canWearItem({ cls: h.cls, gender: h.gender, level: h.level, stats, weaponInfo: weapon ? itemInfos.get(weapon.ItemIndex) : undefined },
      item, info, cur && curInfo ? { item: cur, info: curInfo } : null);
  };
  const openHeroInventory = (): void => heroInventoryWindow.toggle();
  const heroPanels = new HeroPanels(stage, hero, send, {
    openSkills: () => heroMagicWindow.toggle(),
    openInventory: () => openHeroInventory(),
    openCharacter: () => heroChar.toggle(),
  }, {
    main: mainDialog.win, mainLocation: mainDialog.location, screen,
    maxHp: () => heroChar.maxHp(), maxMp: () => heroChar.maxMp(), itemImage: (i) => itemInfos.get(i)?.Image,
  });
  new HeroManageWindow(stage, hero, send);
  new NewHeroWindow(stage, hero, send);
  /** Quests: the NPC quest list, details, log (Q), tracking panel, quest items (state: quests.ts). */
  const questWindow = new QuestWindow(stage, quests, npcWindow, inventoryWindow, itemInfos, gameInfo);
  questWindow.onHelp = () => helpWindow.displayPageNamed("Quests");
  mountWindow.onHelp = () => helpWindow.displayPageNamed("Mounts");
  npcWindow.onHelp = () => helpWindow.displayPageNamed("Purchasing");
  inventoryWindow.onQuestTab = () => questWindow.toggleItems(true);
  // — Section 4: social windows (RankingWindow renders in its constructor and reads the user) —
  const prefillChat = (text: string) => chatDialog.setText(text);
  const relationship = new RelationshipWindow(stage, send, (t, ty) => chat(t, ty), prefillChat);
  const mentor = new MentorWindow(stage, send, (t, ty) => chat(t, ty), () => ({ name: userName(), level: player.level, cls: player.user?.look.class ?? 0 }));
  const inspect = new InspectWindow(stage, send, (t, ty) => chat(t, ty), prefillChat,
    () => group.list, userName, () => performance.now(), inventoryWindow, gameInfo);
  const ranking = new RankingWindow(stage, send, userName, inspect);
  const mail = new MailWindow(stage, inventory, inventoryWindow, send, (t, ty) => chat(t, ty), (i) => gameInfo.requestItem(i));
  inspect.composeMail = (n) => mail.composeLetter(n);
  market.mailTo = (name, msg) => mail.composeLetter(name, msg);
  mail.onHelp = () => helpWindow.toggle(true);
  const friends = new FriendWindow(stage, send, (t, ty) => chat(t, ty), (name) => mail.composeLetter(name));
  relationship.composeMail = (n) => mail.composeLetter(n);
  /** Section 5: game shop (Y / Shop button) and the guild territory list (opened by the server). */
  const gameShop = new GameShopWindow(stage, inventoryWindow, send, (t, ty) => chat(t, ty),
    () => inventory.gold, () => inventory.credit, () => player.user?.look.class ?? 0);
  const territories = new GuildTerritoryWindow(stage, send, (name) => mail.composeLetter(name));
  /** IntelligentCreatureDialog (E; X / Alt+A pick up at the mouse cell, via bindInput). */
  const creatureWindow = new CreatureWindow(stage, creatures, send, (t, ty) => chat(t, ty), () => world.mouseCell());

  /** The window half of GameScene.ProcessPacket, in game.ts's old order and with its early returns. */
  function handle(p: ServerPacket): boolean {
    rental.handle(p);
    questWindow.handle(p);
    // S.AllowObserve: GameScene.AllowObserve, shown by OptionDialog and checked by InspectDialog (both get it)
    if (p.type === "AllowObserve") { optionWindow.handle(p); inspect.handle(p); return true; }
    creatureWindow.handle(p); // the creature list + window (false for UserInformation, so nothing is skipped)
    if (extras.handle(p)) return true;
    storageWindow.handle(p);
    if (relationship.handle(p) || mentor.handle(p) || inspect.handle(p) || ranking.handle(p) || mail.handle(p) || friends.handle(p)) return true;
    if (gameShop.handle(p) || territories.handle(p)) return true;
    group.handle(p);
    trade.handle(p);
    guild.handle(p, (name) => actions.setUserGuild(name));
    npcWindow.handle(p);
    npcServices.handle(p);
    bigMap.handle(p);
    // GameScene.MapInformation / MapChanged: BigMapDialog index (was set in game.ts's switch; nothing reads it in between)
    if (p.type === "MapInformation" || p.type === "MapChanged") bigMap.bigMapIndex = p.BigMap;
    return market.handle(p);
  }

  function notify(e: UiEvent): void {
    switch (e.kind) {
      case "itemInfo": inventoryWindow.render(); break;
      case "allowObserve": optionWindow.setAllowObserve(e.allow); inspect.setUserAllowObserve(e.allow); break; // GameScene.UserInformation: AllowObserve
      case "mountUpdate": mountWindow.handle(e.packet); break;
      case "fishingUpdate": {
        const u = player.user;
        if (u) { fishingWindow.handle(e.packet, u.id); fishingStatus.handle(e.packet, u.id); }
        break;
      }
      case "heroStats": heroChar.invalidate(); break;
      case "gameDataApplied":
        heroChar.invalidate();
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
        miniMap.draw(world.map, world.miniMapIndex, world.mapTitle, user, world.objects.values(), (n) => group.has(n));
        bigMap.draw(world.map, world.mapTitle, user, world.objects.values(), group);
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

  /** MapControl.OnMouseClick with a picked item: hero bag item, bag item (GameScene.SelectedCell), picked gold. */
  function mapMouseDown(e: MouseEvent): boolean {
    // An inventory item is picked: a click on the map drops it (GameScene: SelectedCell + map click -> DropItem)
    if (e.button === 0 && heroInventoryWindow.mapDrop()) return true; // a picked hero item dropped on the map
    const slot = inventoryWindow.pickedInventorySlot;
    if (slot >= 0 && e.button === 0) {
      // MapControl.OnMouseClick with GameScene.SelectedCell: DontDrop refusal, YesNo for one item, MirAmountBox for a stack
      const item = inventory.inventory[slot];
      const info = item ? itemInfos.get(item.ItemIndex) : undefined;
      if (item) {
        const name = info ? friendlyName(info, item.Count) : "this item";
        if (info && (info.Bind & BindMode.DontDrop)) messageBox(stage, `You cannot drop ${name}`, [{ label: "OK" }]);
        else if (item.Count === 1) messageBox(stage, `Are you sure you want to drop ${name}?`, [{ label: "Yes", onClick: () => inventory.drop(slot) }, { label: "No" }]);
        else amountBox("Drop Amount:", info?.Image ?? 0, item.Count, (n) => { if (n > 0) inventory.drop(slot, n); });
      }
      inventoryWindow.clearPick();
      return true;
    }
    // GameScene.PickedUpGold: MirAmountBox (Items 116) -> C.DropGold
    if (pickedUpGold && e.button === 0) {
      pickedUpGold = false;
      amountBox("Drop Amount:", 116, inventory.gold, (n) => { if (n > 0) inventory.dropGold(n); });
      return true;
    }
    return false;
  }

  function command(c: UiCommand): void {
    switch (c.kind) {
      case "trade": trade.request(); break;
      case "toggle": if (c.window === "ranking") ranking.toggle(); else miniMap.toggle(); break;
      case "inspect": inspect.inspect(c.objectId); break;
      case "talk": npcWindow.talk(c.objectId, c.name); break;
    }
  }

  /** The gameplay keys that used to sit in window listeners (the windows keep their toggle / Escape keys). */
  function bindInput(input: InputRouter): void {
    // MountDialog: KeybindOptions.Mount rides (MountDialog.CanRide + Ride); same else-if chain as the window's listener
    input.register("gameplay", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (!matches(e, KeybindOptions.MountWindow) && matches(e, KeybindOptions.Mount) && mountWindow.canRide()) mountWindow.ride();
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
    chat,
    output: (text) => extras.output(text),
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
    inGroup: (name) => group.has(name),
    shopItems: (): GameShopItem[] => gameShop.items,
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
