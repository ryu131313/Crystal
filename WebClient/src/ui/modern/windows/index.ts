// The new UI's windows (phases 3b-3f): each module calls windowRegistry.ts registerWindow(key, factory) at import time.
// modernUi.ts imports this file, so add one side-effect import per window module here, e.g.
//   import "./inventory.ts";
// (Phase 3a ships the framework and the HUD only: no windows yet.)

export {};
