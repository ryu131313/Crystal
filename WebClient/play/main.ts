// Page entry: wires the session, the account screens and (after S.StartGame) the game. Logic lives in src/.

import { Session } from "../src/game/session.ts";
import { Screens } from "../src/ui/screens.ts";
import { startGame } from "../src/game/game.ts";
import type { ServerPacket } from "../src/protocol/server.ts";

const session = new Session();
let route: (p: ServerPacket) => void;
const backlog: ServerPacket[] = [];

/** Account screens own the packets until S.StartGame, and again after a log-out (GameScene.LogOutSuccess / ReturnToLogin). */
const toScreens = (p: ServerPacket) => {
  if (!screens.handle(p)) backlog.push(p); // game packets that race S.StartGame (MapInformation, UserInformation...)
};

const screens = new Screens(session, (start) => {
  screens.hide();
  const game = startGame(session, start, document.getElementById("game") as HTMLCanvasElement, {
    logOut: (characters) => { route = toScreens; backlog.length = 0; screens.showSelect(characters); },
    returnToLogin: () => { route = toScreens; backlog.length = 0; screens.showLogin("The person you was observing has logged off."); },
  });
  route = (p) => game.handle(p);
  for (const p of backlog.splice(0)) route(p);
});

route = toScreens;

session.onPacket = (p) => {
  if (p.type === "Disconnect") {
    screens.fatal(["Server closing.", "Another user logged in to this account.", "Packet error.", "Server crashed."][p.Reason] ?? `Disconnected (${p.Reason}).`);
    return;
  }
  route(p);
};
session.onClose = (reason) => screens.fatal(`Connection closed: ${reason}`);

(window as unknown as { crystal: unknown }).crystal = { session };
