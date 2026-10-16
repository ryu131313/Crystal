// The new UI's windows (phases 3b-3f): each module calls windowRegistry.ts registerWindow(key, factory) at import time.
// modernUi.ts imports this file, so add one side-effect import per window module here.

// Phase 3b: bag, character (menu windows), storage and inspect (server-opened: their entries' watch hooks).
import "./inventory.ts";
import "./character.ts";
import "./storage.ts";
import "./inspect.ts";
