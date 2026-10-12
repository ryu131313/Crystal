// Account screens: the desktop LoginScene (loginScene.ts) and SelectScene (selectScene.ts, with NewCharacterDialog) drawn
// with the .Lib artwork in #screen over the canvas, plus LoginScene's connect box and MirScene's disconnect box (fatal).
// Runs the account flow until S.StartGame succeeds; after a log-out the game hands the character list back (showSelect),
// or returns to the login screen with a message (showLogin).

import type { Session } from "../game/session.ts";
import type { ServerPacket, SelectInfo } from "../protocol/server.ts";
import { messageBox, inputBox } from "./messageBox.ts";
import { LoginScene } from "./loginScene.ts";
import { SelectScene } from "./selectScene.ts";
import { Text, type SceneHost } from "./accountCommon.ts";

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

/** Runs the account flow until S.StartGame succeeds; `handle` is fed every packet until then. */
export class Screens {
  private readonly session: Session;
  private readonly onStart: (r: StartInfo) => void;
  private login: LoginScene | null = null;
  private select: SelectScene | null = null;
  private connectBox: (() => void) | null = null;
  private fatalShown = false;
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
    // LoginScene ctor + Shown: the background, then the connect box until S.ClientVersion
    this.showLoginScene();
    this.connectBox = messageBox(document.body, Text.AttemptingConnectServer, [{ label: "Cancel", onClick: () => this.session.close() }]);
  }

  private track(close: () => void): () => void {
    this.boxes.add(close);
    return () => { this.boxes.delete(close); close(); };
  }

  private closeBoxes(): void {
    for (const close of [...this.boxes]) close();
    this.boxes.clear();
  }

  private showLoginScene(): LoginScene {
    this.login = new LoginScene(root(), this.host, () => this.session.close());
    return this.login;
  }

  private disposeScenes(): void {
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
    this.select = new SelectScene(root(), this.host, characters, (c) => this.start(c));
  }

  /** GameScene.ReturnToLogin -> LoginScene + a message box. */
  showLogin(message: string): void {
    this.hide();
    this.showLoginScene().showLogin();
    this.host.box(message);
  }

  /** MirScene.ProcessPacket(S.Disconnect) / Network disconnect: a message box; OK reloads the page (reconnects). */
  fatal(text: string): void {
    if (this.fatalShown) return;
    this.fatalShown = true;
    this.closeBoxes();
    this.connectBox?.();
    this.connectBox = null;
    messageBox(document.body, text, [{ label: "OK", onClick: () => location.reload() }]);
  }

  /** SelectScene.StartGame(S.StartGame) Result 4: the game scene takes over. */
  private start(character: SelectInfo): void {
    this.onStart({ character, resolution: this.resolution });
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "ClientVersion":
        if (p.Result !== 1) this.fatal("Wrong client version (the relay's --version-path does not match the server's VersionPath).");
        else {
          this.connectBox?.();
          this.connectBox = null;
          this.login?.showLogin();
        }
        return true;
      case "Login": case "LoginBanned": case "LoginSuccess": case "NewAccount": case "ChangePassword": case "ChangePasswordBanned":
        this.login?.handle(p, (characters) => {
          // LoginScene.Login(S.LoginSuccess) -> AfterAnimation -> SelectScene(p.Characters)
          this.login?.dispose();
          this.login = null;
          root().replaceChildren();
          this.select = new SelectScene(root(), this.host, characters, (c) => this.start(c));
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
