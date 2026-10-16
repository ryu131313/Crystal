// Page entry: wires the session, the account screens and (after S.StartGame) the game. Logic lives in src/.

import { Session } from "../src/game/session.ts";
import { Screens } from "../src/ui/screens.ts";
import { startGame } from "../src/game/game.ts";
import { loadUiFactory } from "../src/ui/uiLoader.ts";
import type { ServerPacket } from "../src/protocol/server.ts";
import { loadManifest } from "../src/assets/manifest.ts";

void loadManifest(); // asset versions for every later request (src/assets/manifest.ts)

// The interface is laid out in desktop pixels on the stage (1024 x 768 for the account screens, the game resolution in game; src/ui/mir/controls.ts); scale it to the window.
// Never past the scale a 1920 x 1080 window would give: a bigger window centres the stage on the page background instead of stretching it.
const MAX_WIDTH = 1920, MAX_HEIGHT = 1080;
const stage = document.getElementById("stage") as HTMLElement;
const fit = () => {
  const w = stage.offsetWidth || 1024, h = stage.offsetHeight || 768; // game.ts sets the game resolution (up to 1920 x 1080)
  const s = Math.min(Math.min(window.innerWidth, MAX_WIDTH) / w, Math.min(window.innerHeight, MAX_HEIGHT) / h);
  stage.style.transform = `scale(${s})`;
  stage.style.left = Math.max(0, (window.innerWidth - w * s) / 2) + "px";
  stage.style.top = Math.max(0, (window.innerHeight - h * s) / 2) + "px";
};
window.addEventListener("resize", fit);
fit();

const session = new Session();
let route: (p: ServerPacket) => void;
const backlog: ServerPacket[] = [];
/** The connection ended (a fatal box is up): a UI that finishes loading after that does not start the game. */
let ended = false;

/** Account screens own the packets until S.StartGame, and again after a log-out (GameScene.LogOutSuccess / ReturnToLogin). */
const toScreens = (p: ServerPacket) => {
  if (!screens.handle(p)) backlog.push(p); // game packets that race S.StartGame (MapInformation, UserInformation...)
};

// S.StartGame: load the active game UI (src/ui/uiLoader.ts; usually preloaded on the login screen), then start the game.
// Game packets that arrive while it loads wait in the backlog (toScreens).
const screens = new Screens(session, (start) => {
  screens.hide();
  loadUiFactory().then((createUi) => {
    if (ended) return;
    const game = startGame(session, start, document.getElementById("game") as HTMLCanvasElement, {
      logOut: (characters) => { route = toScreens; backlog.length = 0; screens.showSelect(characters); },
      returnToLogin: () => { route = toScreens; backlog.length = 0; screens.showLogin("The person you was observing has logged off."); },
    }, createUi);
    route = (p) => game.handle(p);
    for (const p of backlog.splice(0)) route(p);
  }, (err) => {
    console.error(err);
    screens.fatal("Could not load the game interface. Check your connection and try again.");
  });
});

route = toScreens;

session.onPacket = (p) => {
  if (p.type === "Disconnect") {
    ended = true;
    screens.fatal(["Server closing.", "Another user logged in to this account.", "Packet error.", "Server crashed."][p.Reason] ?? `Disconnected (${p.Reason}).`);
    return;
  }
  route(p);
};
session.onClose = (reason) => { ended = true; screens.fatal(`Connection closed: ${reason}`); };

(window as unknown as { crystal: unknown }).crystal = { session };
