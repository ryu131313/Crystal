// Lazy loading of the game UI (web-only; plan phase 3a): only the active UI tree is downloaded. play/main.ts imports this
// file statically; the two UI trees (classicUi.ts + the classic windows, modern/modernUi.ts + src/ui/modern/**) are
// reached only through the dynamic import()s below, and WebRelay/PageBuild.cs modulepreloads main.js's static import
// graph only, so a player fetches the other tree never.
//
// Screens calls preloadUi(uiStyle()) when the login screen shows (the download overlaps logging in); play/main.ts awaits
// loadUiFactory() after S.StartGame and passes the factory to startGame (game packets that arrive meanwhile wait in
// main.ts's backlog). A failed load is forgotten, so the next call retries.

import { uiStyle } from "../game/settings.ts";
import type { GameUiFactory } from "./gameUi.ts";

const loads = new Map<"Modern" | "Classic", Promise<GameUiFactory>>();

function load(style: "Modern" | "Classic"): Promise<GameUiFactory> {
  let p = loads.get(style);
  if (!p) {
    p = style === "Modern"
      ? import("./modern/modernUi.ts").then((m) => m.createModernUi)
      : import("./classicUi.ts").then((m) => m.createClassicUi);
    loads.set(style, p);
    p.catch(() => { if (loads.get(style) === p) loads.delete(style); });
  }
  return p;
}

/** Starts downloading a UI tree ahead of S.StartGame (errors are reported by the later loadUiFactory). */
export function preloadUi(style: "Modern" | "Classic" = uiStyle()): void {
  load(style).catch(() => { /* retried by loadUiFactory */ });
}

/** The factory of the UI settings.ts uiStyle() picks now (a UiStyle change applies at the next log-in). */
export function loadUiFactory(style: "Modern" | "Classic" = uiStyle()): Promise<GameUiFactory> {
  return load(style);
}
