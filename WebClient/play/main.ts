// Page entry: wires the session, the account screens and (after S.StartGame) the game. Logic lives in src/.

import { Session } from "../src/game/session.ts";
import { Screens, loadModernScreens, type ModernScreens } from "../src/ui/screens.ts";
import { mobileResolution, startGame } from "../src/game/game.ts";
import { loadUiFactory } from "../src/ui/uiLoader.ts";
import type { ServerPacket } from "../src/protocol/server.ts";
import { loadManifest } from "../src/assets/manifest.ts";
import { isMobileUi, uiStyle } from "../src/game/settings.ts";
import { installMobileShell } from "../src/ui/modern/mobile/shell.ts";

void loadManifest(); // asset versions for every later request (src/assets/manifest.ts)

// The interface is laid out in desktop pixels on the stage (1024 x 768 for the account screens, the game resolution in game; src/ui/mir/controls.ts); scale it to the window.
// Never past the scale a 1920 x 1080 window would give: a bigger window centres the stage on the page background instead of stretching it.
const MAX_WIDTH = 1920, MAX_HEIGHT = 1080;
const stage = document.getElementById("stage") as HTMLElement;
// The touch layout (settings.ts isMobileUi, read once per page): no 1920 cap (a phone is far below it anyway), and the
// on-screen keyboard never shrinks the stage (a height drop while a text field has focus keeps the last fit).
const mobile = isMobileUi();
if (mobile) {
  installMobileShell();
  stage.style.width = mobileResolution(1920)[0] + "px"; // the account screens fill the phone's width (768 high)
}
let lastH = 0;
/** The visible viewport (CSS px). Some phone browsers report 0 or a stale size for a moment after load or a rotation. */
const viewport = (): [number, number] => {
  const vv = window.visualViewport;
  let w = Math.round(vv?.width || window.innerWidth || document.documentElement.clientWidth);
  let h = Math.round(vv?.height || window.innerHeight || document.documentElement.clientHeight);
  // iOS Safari reports the OLD orientation's size for a while after a rotation (resize / visualViewport fire early or
  // not at all), which scaled the stage for portrait and pushed it off the bottom of the landscape screen: a black page.
  // The orientation media query flips reliably, so trust it over the numbers.
  if (mobile) {
    try {
      const land = matchMedia("(orientation: landscape)").matches;
      if (land !== w > h && w !== h) [w, h] = [h, w];
    } catch { /* no matchMedia */ }
  }
  return [w, h];
};
let retry = 0;
const fit = () => {
  const w = stage.offsetWidth || 1024, h = stage.offsetHeight || 768; // game.ts sets the game resolution (up to 1920 x 1080)
  let [vw, vh] = viewport();
  // No size yet (phones while loading / waking): try again next frame instead of scaling the stage to nothing (a black page).
  if (vw < 50 || vh < 50) { if (retry++ < 120) requestAnimationFrame(fit); return; }
  retry = 0;
  if (mobile) {
    const typing = document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement;
    if (typing && lastH && vh < lastH) vh = lastH; else lastH = vh;
  }
  const s = mobile ? Math.min(vw / w, vh / h) : Math.min(Math.min(vw, MAX_WIDTH) / w, Math.min(vh, MAX_HEIGHT) / h);
  if (mobile && (window.scrollX || window.scrollY)) window.scrollTo(0, 0); // iOS scrolls the layout viewport on rotation
  stage.style.transform = `scale(${s})`;
  stage.style.left = Math.max(0, (vw - w * s) / 2) + "px";
  stage.style.top = Math.max(0, (vh - h * s) / 2) + "px";
};
window.addEventListener("resize", fit);
/** After a rotation: re-fit every frame until the viewport has been stable for 10 frames (max ~2 s); iOS settles late. */
let settle = 0;
const settleFit = (rotated = false) => {
  cancelAnimationFrame(settle);
  let last = "", same = 0, frames = 0;
  const step = () => {
    const [w, h] = viewport(), key = w + "x" + h;
    if (key !== last) { last = key; same = 0; fit(); } else same++;
    if (same < 10 && ++frames < 120) settle = requestAnimationFrame(step);
    else if (rotated) repaint();
  };
  settle = requestAnimationFrame(step);
};
/** iOS Safari sometimes leaves the transformed stage unpainted (black) after a rotation until something relayouts it:
 *  hide / show it once the size has settled. */
const ios = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
const repaint = () => {
  const typing = document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement;
  if (!ios || typing) return; // hiding the stage would drop the focus (and the keyboard)
  stage.style.display = "none";
  void stage.offsetHeight;
  stage.style.display = "";
};
if (mobile) {
  // Phones change the viewport without a resize event often enough (rotation, the URL bar, waking the screen, bfcache)
  window.addEventListener("orientationchange", () => settleFit(true));
  window.addEventListener("resize", () => settleFit());
  try { matchMedia("(orientation: landscape)").addEventListener("change", () => settleFit(true)); } catch { /* old Safari */ }
  window.visualViewport?.addEventListener("resize", fit);
  window.addEventListener("pageshow", () => settleFit(true));
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") settleFit(true); });
  window.addEventListener("load", () => { fit(); settleFit(true); });
}
fit();

const session = new Session();
let route: (p: ServerPacket) => void;
const backlog: ServerPacket[] = [];
/** The connection ended (a fatal box is up): a UI that finishes loading after that does not start the game. */
let ended = false;
/** The new UI's loading overlay (Modern only) from S.StartGame until the player continues or the game ends. */
let loading: InstanceType<ModernScreens["LoadingOverlay"]> | null = null;
const endLoading = () => { loading?.dispose(); loading = null; };

/** Account screens own the packets until S.StartGame, and again after a log-out (GameScene.LogOutSuccess / ReturnToLogin). */
const toScreens = (p: ServerPacket) => {
  if (!screens.handle(p)) backlog.push(p); // game packets that race S.StartGame (MapInformation, UserInformation...)
};

// S.StartGame: load the active game UI (src/ui/uiLoader.ts; usually preloaded on the login screen), then start the game.
// Game packets that arrive while it loads wait in the backlog (toScreens). Modern: the loading overlay (in #stage, so
// neither Screens.hide nor the game's dispose removes it) goes up as soon as its module is loaded, then follows the
// game's progress events once the game starts; a failed overlay load only skips the overlay.
/** Bumped per S.StartGame: a late overlay load for an older start is dropped. */
let startSeq = 0, startedSeq = 0;
const screens = new Screens(session, (start) => {
  screens.hide();
  const seq = ++startSeq;
  endLoading();
  // The overlay goes up as soon as its module is in (usually already, from the modern login), not after the game UI.
  if (uiStyle() === "Modern") {
    loadModernScreens().then((m) => {
      if (ended || seq !== startSeq || startedSeq === seq || loading) return; // the game beat it: no overlay

      loading = new m.LoadingOverlay(stage, { level: start.character.Level });
    }, (err) => { console.error(err); });
  }
  const fail = (err: unknown) => { // a failed UI load, or startGame / the overlay hookup throwing: never a hung overlay
    console.error(err);
    if (seq !== startSeq) return;
    startedSeq = seq; // an overlay module that arrives later is not built for this start
    endLoading();
    screens.fatal("Could not load the game interface. Check your connection and try again.");
  };
  loadUiFactory().then((createUi) => {
    if (ended || seq !== startSeq) return;
    startedSeq = seq;
    const ld = loading;
    let game: ReturnType<typeof startGame>;
    try {
      game = startGame(session, start, document.getElementById("game") as HTMLCanvasElement, {
        logOut: (characters) => { endLoading(); route = toScreens; backlog.length = 0; screens.showSelect(characters); },
        returnToLogin: () => { endLoading(); route = toScreens; backlog.length = 0; screens.showLogin("The person you was observing has logged off."); },
      }, createUi);
      if (ld) {
        ld.onDone = () => { if (loading === ld) loading = null; };
        ld.follow(game.progress);
        ld.attach(game);
      }
    } catch (err) { fail(err); return; }
    route = (p) => game.handle(p);
    for (const p of backlog.splice(0)) route(p);
  }, fail);
});

route = toScreens;

session.onPacket = (p) => {
  if (p.type === "Disconnect") {
    ended = true;
    endLoading();
    screens.fatal(["Server closing.", "Another user logged in to this account.", "Packet error.", "Server crashed."][p.Reason] ?? `Disconnected (${p.Reason}).`);
    return;
  }
  route(p);
};
/** When the page was last hidden (a phone app switch / screen off): a close soon after that gets the friendlier text. */
let hiddenAt = -Infinity;
session.onClose = (reason) => {
  ended = true;
  endLoading();
  const slept = document.visibilityState === "hidden" || performance.now() - hiddenAt < 60000;
  screens.fatal(mobile && slept ? "The connection was lost while the game was in the background. Press OK to log in again." : `Connection closed: ${reason}`);
};
if (mobile) {
  // A phone back from the background: the socket can look open but be dead. Ask the server; no answer = the box above.
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") hiddenAt = performance.now();
    else session.probe();
  });
  window.addEventListener("pageshow", (e) => { if (e.persisted) session.probe(); });
}

// Offline start + a cache that survives (phones, Add to Home Screen): play/sw.js. Touch layout only, https or localhost.
if (mobile && "serviceWorker" in navigator && (location.protocol === "https:" || location.hostname === "localhost" || location.hostname === "127.0.0.1")) {
  navigator.serviceWorker.register("sw.js").catch((e: unknown) => console.warn("[sw] not registered:", e));
}

(window as unknown as { crystal: unknown }).crystal = { session };
