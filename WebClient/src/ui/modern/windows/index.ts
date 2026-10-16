// The new UI's windows (phases 3b-3f): each module calls windowRegistry.ts registerWindow(key, factory) at import time.
// modernUi.ts imports this file, so add one side-effect import per window module here.

// Phase 3b: bag, character (menu windows), storage and inspect (server-opened: their entries' watch hooks).
import "./inventory.ts";
import "./character.ts";
import "./storage.ts";
import "./inspect.ts";

// Phase 3c: skills, options, key bindings, help (showTopic: ModernUiHost.help) and the big map (the minimap's World map
// button and B open it).
import "./skills.ts";
import "./options.ts";
import "./keybinds.ts";
import "./help.ts";
import "./bigMap.ts";

// Phase 3d: the NPC service windows. All server-opened (watch hooks): the dialogue on each S.NPCResponse page, the
// merchant on goods / sell / repair panels, the item box on the one-item drop panels (disassemble, downgrade, reset,
// wedding ring, consign), craft on the craft list, refine on the refine / check-refine panels, awaken on S.NPCAwakening,
// the trust merchant on S.NPCMarket; each closes with the NPC session (NPCDialog.Hide) or the next page, and its
// closed() sends what the classic hide sends (C.RefineCancel, the awakening / drop-panel unlocks, Market.reset).
// Sockets open from Ctrl + right-click (core/drag.ts hooks.onSockets), rental from S.ItemRentalRequest / the Rental key.
import "./npc.ts";
import "./merchant.ts";
import "./npcBox.ts";
import "./craft.ts";
import "./refine.ts";
import "./awaken.ts";
import "./socket.ts";
import "./trust.ts";
import "./rent.ts";

// Phase 3e: the social windows. Menu windows (guild, group, friends, mail, mentor, relationship, ranking, territories)
// plus trade (no hotkey: T sends C.TradeRequest through GameUi.command; it opens on S.TradeAccept). Their watch hooks
// run at UI start and own the incoming prompts as request toasts: group invite (group.ts), trade request (trade.ts),
// marriage / divorce (relationship.ts), mentor (mentor.ts); the territory NPC page opens territories; mail's composeTo
// is what inspect / friends / relationship / territories call to write a letter.
import "./guild.ts";
import "./territories.ts";
import "./ranking.ts";
import "./group.ts";
import "./trade.ts";
import "./friends.ts";
import "./mail.ts";
import "./mentor.ts";
import "./relationship.ts";

// Phase 3f: the hero, creatures, mount, fishing, game shop and quests windows (menu windows; every window of the plan's
// phase 3 now exists). Their watch hooks run at UI start, so the prompts work while the window is closed or not yet
// built: hero.ts opens on S.ManageHeroes / a new S.HeroCreateRequest (the NPC hero-manager page) and shows the
// S.NewHero result; creatures.ts owns the new-creature naming popup (S.NewIntelligentCreature), the rename grant and
// the pickup count; fishing.ts opens on a cast / bite while a rod is held; quests.ts records the NPCs met and shows the
// shared-quest invite (S.ShareQuest) as a request toast. The HUD hero frame opens 'hero'; a quest tracker click opens
// 'quests' on that quest (focusQuest).
import "./hero.ts";
import "./creatures.ts";
import "./mount.ts";
import "./fishing.ts";
import "./gameShop.ts";
import "./quests.ts";
