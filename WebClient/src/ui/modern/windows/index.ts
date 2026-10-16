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
