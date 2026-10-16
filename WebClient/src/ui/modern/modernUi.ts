// The new game UI (plan phase 3a): the GameUi (src/ui/gameUi.ts) built from the src/ui/modern core and HUD, wired to the
// models game.ts builds. Chosen by settings.ts uiStyle() === "Modern" and loaded lazily (src/ui/uiLoader.ts), so a
// classic player never downloads this tree (and the reverse).
//
// Built per game (startGame -> createModernUi): one UiRuntime (core/runtime.ts: the '.hudm' root appended to #stage after
// the 2D overlay canvas, the layers, the window manager, popups, ticker, theme and HUD scale), then the services in
// dependency order (drag controller + item cells before the HUD's belt, the tooltip), the bottom HUD (hud/hud.ts) and the
// HUD parts (chat, minimap, extras, party frames, hero frame, quest tracker). Windows (phases 3b-3f) come from the
// window catalog (windowRegistry.ts, filled by windows/index.ts) and are built on their first open. Server-opened
// windows (storage, inspect: phase 3b; the NPC services: phase 3d; hero manage / create: phase 3f) and the start-time
// prompts (social: 3e; new creature name, shared quest: 3f) open from their catalog entry's `watch` hook, run here once per game; GameUi.handle
// feeds every packet to host.packets for the hooks whose packets have no model event (it never consumes).
//
// Keys: everything goes through game.ts's input router (src/game/input.ts), registered in bindInput:
//   modal     popups (core/popups.ts), the drag / pick (Esc cancels)
//   menu      the HUD's open list and the game menu (hud.ts)
//   window    the front window's key() hook, Esc closes the front window, Shift+Esc closes all (core/wm.ts)
//   hotkeys   the chat (Enter / Space / @ / ! / '/'), Esc with nothing open = the game menu (hud.ts), the window toggles
//             (KeybindOptions, registered windows only), Closeall when rebound off Esc, R (Skillbar) / Z (Belt)
//   gameplay  Mount (ride) and the creature pickups (the classic UI registers the same two); the belt keys 1-8, hero
//             belt 9 / 0, skill keys and Ranking / Minimap stay game.ts's (Ranking / Minimap arrive as UiCommands)
//   watchers  held Ctrl / Shift (the skill row's bar 2 / hero view: hud.ts; the tooltip's compare card)
//
// Incoming social prompts are request toasts (core/popups.ts request) wired once per game, window open or not: group and
// trade invites by windows/group.ts / trade.ts (their sessions, built by the watch hooks), marriage / divorce / mentor
// requests by windows/relationship.ts / mentor.ts's watch hooks, and the friend request + guild invite here; the guild
// name / war inputs (S.GuildNameRequest / GuildRequestWar) are input popups here. Each answers with the classic packet.
//
// Settings (settings.ts): UiScale / UiAutoScale -> the runtime's HUD scale, UiTheme + the custom colours -> the theme,
// UiOpacity -> the bottom HUD's opacity, UiPlates -> the stat plates; all live (onSettingChange).

import { ClientPackets } from "../../protocol/client.ts";
import { ItemType, MirClassName } from "../../protocol/enums.generated.ts";
import type { ServerPacket, UserItem } from "../../protocol/server.ts";
import { Emitter } from "../../game/emitter.ts";
import { KeybindOptions, matches, type KeybindOption } from "../../game/keybinds.ts";
import { isTextTarget, type InputRouter } from "../../game/input.ts";
import { gameSettings, onSettingChange, setSetting, type GameSettings } from "../../game/settings.ts";
import { SoundList } from "../../game/audio.ts";
import { correctSlot } from "../../game/heroItems.ts";
import type { GameUi, GameUiContext, UiEvent, UiCommand, FramePoint } from "../gameUi.ts";
import type { ItemLabelContext } from "../itemLabel.ts";
import { later } from "./core/dom.ts";
import type { ThemeSettings } from "./core/theme.ts";
import { GOLD_IMG } from "./core/popups.ts";
import { UiRuntime } from "./core/runtime.ts";
import { dragController } from "./core/drag.ts";
import { MIR_GRID, type CellRef } from "./core/dragRoutes.ts";
import { itemTooltip } from "./core/tooltip.ts";
import { Hud } from "./hud/hud.ts";
import { MENU_WINDOWS, WINDOW_KEYS } from "./hud/gameMenu.ts";
import { parsePlates, formatPlates } from "./hud/statPlates.ts";
import { ChatHud } from "./hud/chat.ts";
import { MinimapHud } from "./hud/minimap.ts";
import { HudExtras } from "./hud/extras.ts";
import { PartyFrames } from "./hud/party.ts";
import { HeroFrame } from "./hud/heroFrame.ts";
import { QuestTracker } from "./hud/tracker.ts";
import { onWindowRegistered, windowCatalog, type ModernUiHost, type WindowEntry } from "./windowRegistry.ts";
import "./windows/index.ts";
import { showTopic } from "./windows/help.ts";
import { SOCKET_KEY } from "./windows/socket.ts";

export { registerWindow, type ModernUiHost, type ModernWindowFactory, type WindowOptions } from "./windowRegistry.ts";

/** The *2 alternates of the MENU_WINDOWS bindings (KeybindOptions Inventory2 / Equipment2 / Skills2 / Options2). */
const ALT_BINDS: Partial<Record<KeybindOption, KeybindOption>> = {
  [KeybindOptions.Inventory]: KeybindOptions.Inventory2, [KeybindOptions.Equipment]: KeybindOptions.Equipment2,
  [KeybindOptions.Skills]: KeybindOptions.Skills2, [KeybindOptions.Options]: KeybindOptions.Options2,
};
/** Extra default hotkeys of a window key beyond its MENU_WINDOWS binding. */
const EXTRA_BINDS: Partial<Record<string, readonly KeybindOption[]>> = {
  [WINDOW_KEYS.hero]: [KeybindOptions.HeroInventory, KeybindOptions.HeroEquipment, KeybindOptions.HeroSkills],
};

/** MirItemCell.PlayItemSound (the click sound of picking an item up). */
function itemSound(type: number | undefined): number {
  switch (type) {
    case ItemType.Weapon: return SoundList.ClickWeapon;
    case ItemType.Armour: return SoundList.ClickArmour;
    case ItemType.Helmet: return SoundList.ClickHelmet;
    case ItemType.Necklace: return SoundList.ClickNecklace;
    case ItemType.Bracelet: return SoundList.ClickBracelet;
    case ItemType.Ring: return SoundList.ClickRing;
    case ItemType.Boots: return SoundList.ClickBoots;
    case ItemType.Potion: return SoundList.ClickDrug;
    default: return SoundList.ClickItem;
  }
}

/** The theme settings (preset + custom colours) from settings.ts. */
function themeSettings(s: Readonly<GameSettings>): ThemeSettings {
  return {
    theme: s.UiTheme,
    custom: { accent: s.UiAccent, frame: s.UiFrame, text: s.UiText, hue: s.UiHue, sat: s.UiSat, lit: s.UiLit, glow: s.UiGlow, radius: s.UiRadius },
  };
}
const THEME_KEYS = new Set<keyof GameSettings>(["UiTheme", "UiAccent", "UiFrame", "UiText", "UiHue", "UiSat", "UiLit", "UiGlow", "UiRadius"]);

export function createModernUi(ctx: GameUiContext): GameUi {
  const { player, models: m } = ctx;

  // — Runtime + services (order: drag before the HUD's belt cells, tooltip before the hover hook) —
  const rt = new UiRuntime({
    stage: ctx.stage, signal: ctx.signal, screen: ctx.screen,
    scale: gameSettings.UiScale, autoScale: gameSettings.UiAutoScale, theme: themeSettings(gameSettings),
  });
  const sig = rt.signal;
  const events = new Emitter<UiEvent>();
  const packets = new Emitter<ServerPacket>();
  const drag = dragController(rt, { models: m, now: () => ctx.now() });
  const cells = drag.itemCells;
  const tooltip = itemTooltip(rt, m.gameInfo);

  /** GameScene.CreateItemLabel's context for the user. */
  const labelContext = (): ItemLabelContext => ({
    info: (i) => m.itemInfos.get(i.ItemIndex), realItem: (info) => m.itemInfos.realItem(info, player.level, player.user?.look.class ?? 0),
    level: player.level, job: player.user?.look.class ?? 0, gender: player.user?.look.gender ?? 0, stat: (k) => player.stats?.stats[k] ?? 0,
  });
  /** The worn item a bag-side item would replace (the tooltip's compare row / Shift card); none for worn or hero cells. */
  const compareWith = (ref: CellRef, item: UserItem): UserItem | null => {
    if (ref.grid === "eq" || ref.grid === "heroEq" || ref.grid === "heroInv" || ref.grid === "heroHP" || ref.grid === "heroMP") return null;
    const info = m.itemInfos.get(item.ItemIndex);
    if (!info) return null;
    for (let slot = 0; slot < m.inventory.equipment.length; slot++) {
      const worn = m.inventory.equipment[slot];
      if (worn && correctSlot(info, slot)) return worn; // rings / bracelets: the first worn one
    }
    return null;
  };
  cells.onHover = (cell, item) => {
    if (!cell || !item) { tooltip.hide(); return; }
    tooltip.show(cell.el, item, labelContext(), { compareWith: compareWith(cell.ref, item) });
  };

  const openWindow = (key: string, show?: boolean): boolean => rt.toggleWindow(key, show);

  /** The game menu's "Switch to classic interface" (the mirror of the classic Options "New interface (preview)"). */
  const switchToClassic = (): void => {
    if (gameSettings.UiStyle !== "Classic") setSetting("UiStyle", "Classic");
    void rt.popups.ask("The classic interface applies at your next log-in. Log out now?", { title: "Classic interface" })
      .then((yes) => { if (yes && !sig.aborted) ctx.actions.logOut(false, true); });
  };

  const hud = new Hud({
    rt, ctx,
    plates: () => parsePlates(gameSettings.UiPlates),
    savePlates: (keys) => setSetting("UiPlates", formatPlates(keys)),
    switchToClassic,
    command: openWindow,
  });
  const chat = new ChatHud(rt, ctx, { labelContext });
  const minimap = new MinimapHud(rt, ctx);
  const extras = new HudExtras(rt, ctx);
  const party = new PartyFrames(rt, ctx);
  const heroFrame = new HeroFrame(rt, ctx);
  const tracker = new QuestTracker(rt, ctx);

  drag.hooks = {
    onLink: (item, cell) => chat.linkItem(item, cell.grid in MIR_GRID ? MIR_GRID[cell.grid as keyof typeof MIR_GRID] ?? undefined : undefined),
    onSockets: () => { openWindow(SOCKET_KEY, true); }, // windows/socket.ts's watch hook replaces it (it also shows the item)
    onPickSound: (item) => ctx.sound.play(itemSound(m.itemInfos.get(item.ItemIndex)?.Type)),
  };

  // — The window catalog (windowRegistry.ts) into this game's runtime —
  const host: ModernUiHost = {
    rt, ctx, drag, cells, tooltip, chat, hud, events, packets, labelContext, openWindow,
    help: (topic = null) => showTopic(host, topic),
  };
  /** Key -> the removal of its entry's watch hook (server-opened windows). */
  const watches = new Map<string, () => void>();
  const add = (key: string, e: WindowEntry) => {
    if (sig.aborted) return;
    rt.registerWindow(key, () => e.factory(host));
    watches.get(key)?.();
    watches.delete(key);
    const off = e.options.watch?.(host, (show) => openWindow(key, show));
    if (off) watches.set(key, off);
  };
  for (const [key, e] of windowCatalog()) add(key, e);
  const offCatalog = onWindowRegistered(add);

  /** Window key -> the hotkeys that toggle it (the catalog's binds, else MENU_WINDOWS + alternates). K (Ranking) is
   *  left out: game.ts sends it as a UiCommand. */
  const hotkeys = (): [string, readonly KeybindOption[]][] => {
    const out: [string, readonly KeybindOption[]][] = [];
    const seen = new Set<string>();
    for (const [key, e] of windowCatalog()) if (e.options.binds) { out.push([key, e.options.binds]); seen.add(key); }
    for (const w of MENU_WINDOWS) {
      if (seen.has(w.key) || w.key === WINDOW_KEYS.ranking) continue;
      const binds: KeybindOption[] = [];
      if (w.bind !== undefined) { binds.push(w.bind); const alt = ALT_BINDS[w.bind]; if (alt !== undefined) binds.push(alt); }
      for (const b of EXTRA_BINDS[w.key] ?? []) if (!binds.includes(b)) binds.push(b);
      if (binds.length) { out.push([w.key, binds]); seen.add(w.key); }
    }
    return out;
  };

  // — Settings (live) —
  const applyOpacity = () => { rt.layers.rig.style.opacity = gameSettings.UiOpacity >= 1 ? "" : String(gameSettings.UiOpacity); };
  applyOpacity();
  const offSettings = onSettingChange((key) => {
    if (key === "UiScale" || key === "UiAutoScale") rt.setScale(gameSettings.UiScale, gameSettings.UiAutoScale);
    else if (THEME_KEYS.has(key)) rt.setTheme(themeSettings(gameSettings));
    else if (key === "UiOpacity") applyOpacity();
    else if (key === "UiPlates") hud.reloadPlates();
  });

  // — Request prompts (GameScene's MirMessageBox / MirInputBox handlers; the classic windows open the same) —
  /** S.FriendRequest: an invite toast per requester, closed when the request runs out or is settled elsewhere. */
  const friendPrompts = new Map<string, { close(): void }>();
  const offs: (() => void)[] = [
    offCatalog, offSettings,
    () => { for (const off of watches.values()) off(); watches.clear(); },
    // Group / trade invites: windows/group.ts / trade.ts (their sessions' toasts); marriage / divorce / mentor requests:
    // windows/relationship.ts / mentor.ts (their watch hooks). The guild ones stay here (the guild window has none).
    m.guild.invite.on(({ name }) => {
      // S.GuildInvite carries the guild name. MirMessageBox Yes/No as a request toast; running out declines (C.GuildInvite false clears the pending invite).
      void rt.popups.request({ from: name, tag: "Guild", text: "guild has invited you to join.", seconds: 30, accept: "Join" })
        .then((yes) => { if (!sig.aborted) m.guild.answerInvite(yes); });
    }),
    m.guild.nameRequest.on(() => {
      void rt.popups.input({ title: "Guild name", text: "Please enter a guild name, length must be 3~20 characters.", min: 3, max: 20 })
        .then((name) => { if (name !== null && !sig.aborted) m.guild.returnName(name); });
    }),
    m.guild.warRequest.on(() => {
      void rt.popups.input({ title: "Guild war", text: "Please enter the guild you would like to go to war with." })
        .then((name) => { if (name !== null && !sig.aborted) m.guild.returnWar(name); });
    }),
    m.friends.request.on((r) => {
      const key = r.name.toLowerCase();
      friendPrompts.get(key)?.close();
      const cls = (MirClassName[r.cls] ?? "").toLowerCase();
      let settled = false;
      const p = rt.popups.request({
        from: r.name, tag: "Friend", text: `(${cls ? `${cls}, ` : ""}level ${r.level}) wants to add you as a friend.`,
        seconds: Math.max(1, Math.ceil((r.expires - ctx.now()) / 1000)), // ends just after the server's expiry (no reply then)
      });
      const entry = { close: () => { settled = true; p.close(); } };
      friendPrompts.set(key, entry);
      void p.then((yes) => {
        if (friendPrompts.get(key) === entry) friendPrompts.delete(key);
        // Run out (the server's 60 s) or closed because it was settled elsewhere: no reply. Decline answers.
        if (settled || sig.aborted || r.expires <= ctx.now()) return;
        m.friends.reply(r.name, yes);
      });
    }),
    m.friends.requestsChanged.on(() => {
      if (friendPrompts.size === 0) return;
      const live = new Set(m.friends.requests.map((r) => r.name.toLowerCase()));
      for (const [key, e] of [...friendPrompts]) if (!live.has(key)) { friendPrompts.delete(key); e.close(); }
    }),
  ];

  // — Revive / reincarnation (GameScene's DiedTip box after S.Death; RequestReincarnation) —
  let closeRevive: (() => void) | null = null;
  let cancelReviveTimer: (() => void) | null = null;
  const clearRevive = () => { cancelReviveTimer?.(); cancelReviveTimer = null; closeRevive?.(); closeRevive = null; };
  function showRevive(): void {
    clearRevive();
    cancelReviveTimer = later(() => {
      cancelReviveTimer = null;
      if (sig.aborted || !player.user?.dead) return;
      const q = rt.popups.ask("You have died, Do you want to revive in town?", { title: "You died", kind: "warning" });
      closeRevive = q.close;
      void q.then((yes) => {
        if (closeRevive === q.close) closeRevive = null;
        if (yes && !sig.aborted && player.user?.dead) ctx.send(ClientPackets.TownRevive());
      });
    }, 1500, sig);
  }
  function showReincarnation(): void {
    clearRevive();
    const q = rt.popups.ask("Would you like to be revived?", { title: "Revive" });
    closeRevive = q.close;
    void q.then((yes) => {
      if (closeRevive === q.close) closeRevive = null;
      if (yes && !sig.aborted && player.user?.dead) ctx.send(ClientPackets.AcceptReincarnation());
    });
  }

  // — GameUi —
  function notify(e: UiEvent): void {
    if (e.kind === "itemInfo") {
      cells.refresh();
      hud.refresh();
      tooltip.refresh();
    }
    events.emit(e);
  }

  function frame(t: number, at: FramePoint): void {
    // One point: every HUD part runs on the ticker (HudState.process included: hud/extras.ts), after the world render.
    if (at === "end") rt.frame(t);
  }

  function confirm(text: string, yes: () => void, opts?: { primary?: boolean; no?: () => void }): () => void {
    let closed = false;
    const q = rt.popups.ask(text);
    void q.then((ok) => {
      if (closed || sig.aborted) return;
      if (ok) yes(); else opts?.no?.();
    });
    return () => { closed = true; q.close(); };
  }

  function amount(title: string, image: number, max: number, ok: (n: number) => void): () => void {
    let closed = false;
    const gold = image === GOLD_IMG;
    const q = rt.popups.amount({ title, max, gold, item: gold ? undefined : { name: title, img: image } });
    void q.then((n) => { if (!closed && !sig.aborted && n !== null) ok(n); });
    return () => { closed = true; q.close(); };
  }

  /** MapControl.OnMouseClick (left) with a picked item / gold: dropped on the world (core/drag.ts mapClick). */
  function mapMouseDown(e: MouseEvent): boolean {
    if (e.button !== 0) return false;
    return drag.mapClick();
  }

  function command(c: UiCommand): void {
    switch (c.kind) {
      case "trade": m.trade.request(); break;
      case "toggle": if (c.window === "miniMap") minimap.toggle(); else openWindow(WINDOW_KEYS.ranking); break;
      case "inspect": m.inspect.inspect(c.objectId); break; // InspectDialog: windows/inspect.ts opens on the reply
      case "talk": m.npcSession.talk(c.objectId, c.name); break; // C.CallNPC "[@Main]"; windows/npc.ts opens on the page
    }
  }

  let pickupTime = 0;
  /** IntelligentCreatureDialog pickup (X at the mouse cell / Alt+A auto): C.IntelligentCreaturePickup, 200 ms apart. */
  function creaturePickup(mouseMode: boolean): void {
    const t = ctx.now();
    if (t <= pickupTime) return;
    const cell = ctx.world.mouseCell();
    if (!cell) return;
    pickupTime = t + 200;
    ctx.send(ClientPackets.IntelligentCreaturePickup({ MouseMode: mouseMode, Location: { X: cell.X, Y: cell.Y } }));
  }

  /** R (Skillbar) / Z (Belt): show / hide the skill rows / the belt rows (kept in place, so the wings do not jump). */
  const toggleRows = (rows: readonly HTMLElement[]) => {
    const hide = rows[0]?.style.visibility !== "hidden";
    for (const r of rows) r.style.visibility = hide ? "hidden" : "";
  };

  function bindInput(input: InputRouter): void {
    const reg: (() => void)[] = [
      input.register("modal", (e) => rt.popups.handleKey(e)),
      input.register("modal", (e) => drag.handleKey(e)),
      input.register("window", (e) => rt.wm.handleKey(e)),
      input.register("hotkeys", (e) => chat.handleKey(e)),
      input.register("hotkeys", (e) => {
        if (rt.root.hidden || isTextTarget(e)) return false;
        for (const [key, binds] of hotkeys()) {
          if (!rt.hasWindow(key) || !binds.some((b) => matches(e, b))) continue;
          e.preventDefault();
          if (!e.repeat) openWindow(key);
          return true;
        }
        if (e.key !== "Escape" && matches(e, KeybindOptions.Closeall)) { if (rt.wm.closeAll()) { e.preventDefault(); return true; } return false; }
        if (e.repeat) return false;
        if (matches(e, KeybindOptions.Skillbar)) { toggleRows([hud.skills.left, hud.skills.right]); return true; }
        if (matches(e, KeybindOptions.Belt)) { toggleRows([hud.belt.left, hud.belt.right]); return true; }
        return false;
      }),
      // MountDialog: KeybindOptions.Mount rides (same else-if chain as the classic UI's)
      input.register("gameplay", (e) => {
        if (chat.typing || isTextTarget(e)) return;
        if (!matches(e, KeybindOptions.MountWindow) && matches(e, KeybindOptions.Mount) && m.mount.canRide()) m.mount.ride();
      }),
      // GameScene KeybindOptions.CreaturePickup (X) / CreatureAutoPickup (Alt+A)
      input.register("gameplay", (e) => {
        if (chat.typing || isTextTarget(e)) return;
        if (matches(e, KeybindOptions.Creature) && !e.repeat) return;
        if (matches(e, KeybindOptions.CreaturePickup)) creaturePickup(true);
        else if (matches(e, KeybindOptions.CreatureAutoPickup)) { e.preventDefault(); creaturePickup(false); }
      }),
      // held Shift: the tooltip's equipped card (the HUD tracks Ctrl / Shift for the skill row itself)
      input.watch((e) => { if (e.isTrusted) tooltip.setShift(e.shiftKey); }),
      input.onKeyUp((e) => { if (e.isTrusted) tooltip.setShift(e.shiftKey); }),
      input.onBlur(() => tooltip.setShift(false)),
    ];
    hud.bindInput(input); // menu: the open list + the game menu; hotkeys: Esc with nothing open = the game menu
    sig.addEventListener("abort", () => { for (const off of reg) off(); }, { once: true });
  }

  let disposed = false;
  function dispose(): void {
    if (disposed) return;
    disposed = true;
    clearRevive();
    for (const off of offs) off();
    for (const e of friendPrompts.values()) e.close();
    friendPrompts.clear();
    for (const part of [chat, minimap, extras, party, heroFrame, tracker, hud]) {
      try { part.dispose(); } catch (err) { console.error(err); }
    }
    rt.dispose(); // windows, popups, services (drag, item cells, tooltip), the UI scope, the ticker, the root
  }
  sig.addEventListener("abort", dispose, { once: true });

  return {
    style: "Modern",
    // No UI-only leftovers: every packet the HUD shows lives in a model; the watch hooks only read (storage).
    handle: (p: ServerPacket) => { packets.emit(p); return false; },
    notify,
    frame,
    confirm,
    amount,
    revive: (show) => { if (show) showRevive(); else clearRevive(); },
    reincarnation: showReincarnation,
    get typing() { return chat.typing; },
    blurChat: () => chat.blur(),
    prefillChat: (text) => chat.prefill(text),
    mapMouseDown,
    command,
    bindInput,
    debug: { rt, hud, chat, minimap, extras, party, heroFrame, tracker, drag, cells, tooltip, windows: () => rt.windowKeys },
    dispose,
  };
}
