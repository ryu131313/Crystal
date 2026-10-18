// Account screens: the desktop LoginScene (loginScene.ts) and SelectScene (selectScene.ts, with NewCharacterDialog) drawn
// with the .Lib artwork in #screen over the canvas, plus LoginScene's connect box and MirScene's disconnect box (fatal).
// Runs the account flow until S.StartGame succeeds; after a log-out the game hands the character list back (showSelect),
// or returns to the login screen with a message (showLogin).
//
// Web-only (plan phase 4): with settings.ts uiStyle() "Modern" the new UI's login / select screens (src/ui/modern/screens)
// replace them, chosen again at every login / select screen (a UiStyle change applies at the next screen, no reload).
// The modern module is loaded lazily (loadModernScreens: a Classic player never fetches it); a failed load falls back to
// the classic scene. The modern login has no connect box (its LOG IN button and server card show the connection), and
// showLogin(message) / fatal() use the modern box (modern/screens/notice.ts) while Modern. S.KeepAlive replies (Session's
// idle pings, the modern login's first ping) are consumed here: the modern login shows the round trip.

import type { Session } from "../game/session.ts";
import type { ServerPacket, SelectInfo } from "../protocol/server.ts";
import { messageBox, inputBox } from "./messageBox.ts";
import { LoginScene } from "./loginScene.ts";
import { SelectScene } from "./selectScene.ts";
import { Text, type SceneHost } from "./accountCommon.ts";
import { serverRules } from "../game/serverRules.ts";
import { accountDetails } from "../game/accountDetails.ts";
import { uiStyle } from "../game/settings.ts";
import { preloadUi } from "./uiLoader.ts";
import type { LoginUi, SelectUi } from "./modern/screens/index.ts";

const root = () => document.getElementById("screen")!;

type Props = Record<string, unknown> & { class?: string };

export function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props = {}, ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  const { class: cls, ...rest } = props;
  if (cls) e.className = cls;
  Object.assign(e, rest);
  e.append(...children);
  return e;
}

export interface StartInfo { character: SelectInfo; resolution: number }

/** The new UI's account screens + loading overlay (src/ui/modern/screens/index.ts). */
export type ModernScreens = typeof import("./modern/screens/index.ts");

let modernLoad: Promise<ModernScreens> | null = null;
let modernLoaded: ModernScreens | null = null;

/** Loads the new UI's account screens module once (a failed load is forgotten, so the next call retries). */
export function loadModernScreens(): Promise<ModernScreens> {
  if (!modernLoad) {
    const p = import("./modern/screens/index.ts");
    modernLoad = p;
    p.then((m) => { modernLoaded = m; }, () => { if (modernLoad === p) modernLoad = null; });
  }
  return modernLoad;
}

/** Runs the account flow until S.StartGame succeeds; `handle` is fed every packet until then. */
export class Screens {
  private readonly session: Session;
  private readonly onStart: (r: StartInfo) => void;
  private login: LoginUi | null = null;
  private select: SelectUi | null = null;
  private connectBox: (() => void) | null = null;
  private fatalShown = false;
  /** S.ClientVersion said yes (LoginScene.Shown -> showLogin once known). */
  private versionOk = false;
  /** Bumped whenever the scenes change: a lazily built modern scene that arrives after that is dropped. */
  private gen = 0;
  /** S.StartGame.Resolution of the last reply. */
  private resolution = 0;
  /** Message boxes the scenes opened (MirMessageBox.Parent = the scene: they go with it). */
  private readonly boxes = new Set<() => void>();
  private readonly host: SceneHost;

  constructor(session: Session, onStart: (r: StartInfo) => void) {
    this.session = session;
    this.onStart = onStart;
    this.host = {
      send: (f) => this.session.send(f),
      box: (text, buttons) => this.track(messageBox(document.body, text, buttons)),
      input: (text, onOk, onCancel) => this.track(inputBox(document.body, text, "", onOk, onCancel)),
      track: (close) => { this.track(close); },
    };
    // LoginScene ctor + Shown: the background, then (classic) the connect box until S.ClientVersion
    this.showLoginScene();
  }

  private track(close: () => void): () => void {
    this.boxes.add(close);
    return () => { this.boxes.delete(close); close(); };
  }

  private closeBoxes(): void {
    for (const close of [...this.boxes]) close();
    this.boxes.clear();
  }

  /** The modern module when the setting is Modern and it is already loaded (sync paths: boxes). */
  private modernNow(): ModernScreens | null {
    return uiStyle() === "Modern" ? modernLoaded : null;
  }

  /** A message over the account screens: the classic MirMessageBox, or the modern box while Modern (once its module is in;
   *  dropped if the scenes change first, classic box if the module fails). Closed on hide. */
  private notice(text: string): void {
    const m = this.modernNow();
    if (m) { this.track(m.showNotice(text)); return; }
    if (uiStyle() !== "Modern") { this.host.box(text); return; }
    const gen = this.gen;
    void loadModernScreens().then((mod) => { if (gen === this.gen) this.track(mod.showNotice(text)); },
      () => { if (gen === this.gen) this.host.box(text); });
  }

  /** LoginScene ctor + Shown (+ showLogin once the version is known), classic or modern by uiStyle(). */
  private showLoginScene(): void {
    // the account screens module first (nothing paints until it is in; PageBuild preloads only main.js's static graph),
    // then the active game UI tree, which downloads while the player logs in (uiLoader.ts)
    if (uiStyle() === "Modern" && !modernLoaded) void loadModernScreens().catch(() => { /* handled below */ });
    preloadUi(uiStyle());
    const gen = ++this.gen;
    if (uiStyle() === "Modern") {
      const build = (m: ModernScreens) => {
        if (gen !== this.gen) return;
        this.login = m.createModernLogin(root(), this.host, () => this.session.close(), {
          session: this.session,
          onClassic: () => this.rebuildLogin(),
        });
        if (this.versionOk) this.login.showLogin();
      };
      if (modernLoaded) build(modernLoaded);
      else void loadModernScreens().then(build, (err) => {
        console.error(err);
        if (gen === this.gen) this.showClassicLogin();
      });
      return;
    }
    this.showClassicLogin();
  }

  private showClassicLogin(): void {
    this.login = new LoginScene(root(), this.host, () => this.session.close());
    if (this.versionOk) this.login.showLogin();
    else if (!this.connectBox && !this.fatalShown) {
      this.connectBox = messageBox(document.body, Text.AttemptingConnectServer, [{ label: "Cancel", onClick: () => this.session.close() }]);
    }
  }

  /** The modern login's "classic interface" link (the setting is already saved): the classic scene, same connection. */
  private rebuildLogin(): void {
    this.login?.dispose();
    this.login = null;
    root().replaceChildren();
    this.showLoginScene();
  }

  /** SelectScene(characters), classic or modern by uiStyle(). */
  private showSelectScene(characters: SelectInfo[]): void {
    const gen = ++this.gen;
    if (uiStyle() === "Modern") {
      const build = (m: ModernScreens) => {
        if (gen !== this.gen) return;
        this.select = m.createSelectScene(root(), this.host, characters, (c) => this.start(c), {
          details: accountDetails(this.session),
          rules: serverRules(this.session),
          onClassic: () => {
            // the modern scene keeps `characters` current (new / deleted characters): the classic one takes it over
            this.select?.dispose();
            this.select = null;
            root().replaceChildren();
            preloadUi(uiStyle()); // the classic game UI tree now (the login's link does this through showLoginScene)
            this.showSelectScene(characters);
          },
        });
      };
      if (modernLoaded) build(modernLoaded);
      else void loadModernScreens().then(build, (err) => {
        console.error(err);
        if (gen === this.gen) this.select = new SelectScene(root(), this.host, characters, (c) => this.start(c));
      });
      return;
    }
    this.select = new SelectScene(root(), this.host, characters, (c) => this.start(c));
  }

  private disposeScenes(): void {
    this.gen++;
    this.closeBoxes();
    this.connectBox?.();
    this.connectBox = null;
    this.login?.dispose();
    this.login = null;
    this.select?.dispose();
    this.select = null;
  }

  hide(): void {
    this.disposeScenes();
    root().replaceChildren();
  }

  /** GameScene.LogOutSuccess -> SelectScene(p.Characters). */
  showSelect(characters: SelectInfo[]): void {
    this.hide();
    preloadUi(uiStyle()); // after a log-out: the style may have changed in game
    this.showSelectScene(characters);
  }

  /** GameScene.ReturnToLogin -> LoginScene + a message box. */
  showLogin(message: string): void {
    this.hide();
    this.showLoginScene();
    this.notice(message);
  }

  /** MirScene.ProcessPacket(S.Disconnect) / Network disconnect: a message box; OK reloads the page (reconnects). */
  fatal(text: string): void {
    if (this.fatalShown) return;
    this.fatalShown = true;
    this.gen++; // a modern scene still loading is not built behind the box
    this.closeBoxes();
    this.connectBox?.();
    this.connectBox = null;
    const classic = () => { messageBox(document.body, text, [{ label: "OK", onClick: () => location.reload() }]); };
    const modern = (m: ModernScreens) => { m.showNotice(text, { title: "Disconnected", kind: "warning", onOk: () => location.reload() }); };
    const m = this.modernNow();
    if (m) modern(m);
    else if (uiStyle() === "Modern") void loadModernScreens().then(modern, classic);
    else classic();
  }

  /** SelectScene.StartGame(S.StartGame) Result 4: the game scene takes over. */
  private start(character: SelectInfo): void {
    this.onStart({ character, resolution: this.resolution });
  }

  handle(p: ServerPacket): boolean {
    if (serverRules(this.session).handle(p)) return true; // S.ServerRules after S.LoginSuccess: stored, never backlogged
    if (accountDetails(this.session).handle(p)) return true; // S.LoginFailed / S.SelectDetails (web-only): stored, never backlogged
    switch (p.type) {
      case "ClientVersion":
        if (p.Result !== 1) this.fatal("Wrong client version (the relay's --version-path does not match the server's VersionPath).");
        else {
          this.versionOk = true;
          this.connectBox?.();
          this.connectBox = null;
          this.login?.showLogin();
        }
        return true;
      case "KeepAlive":
        // Session's idle pings: the modern login shows the round trip; never backlogged for the game
        this.login?.handle(p, () => {});
        return true;
      case "Login": case "LoginBanned": case "LoginSuccess": case "NewAccount": case "ChangePassword": case "ChangePasswordBanned":
        this.login?.handle(p, (characters) => {
          // LoginScene.Login(S.LoginSuccess) -> AfterAnimation -> SelectScene(p.Characters)
          this.login?.dispose();
          this.login = null;
          root().replaceChildren();
          this.showSelectScene(characters);
        });
        return true;
      case "StartGame":
        this.resolution = p.Resolution;
        this.select?.handle(p);
        return true;
      case "NewCharacter": case "NewCharacterSuccess": case "DeleteCharacter": case "DeleteCharacterSuccess":
      case "StartGameBanned": case "StartGameDelay":
        this.select?.handle(p);
        return true;
      default:
        return false;
    }
  }
}
