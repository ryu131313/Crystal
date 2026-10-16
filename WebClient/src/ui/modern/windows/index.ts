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
