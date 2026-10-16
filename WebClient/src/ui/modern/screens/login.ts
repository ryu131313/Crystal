// Ported from play/hudLogin.ts: the new UI's login screen, on the real flow of src/ui/loginScene.ts (LoginScene:
// LoginDialog, InputKeyDialog, NewAccountDialog, ChangePasswordDialog) and accountCommon.ts.
//  - Backdrop: ChrSel 0 (the closed door). Frames 1-18 are STREAMED one at a time after frame 0 (never all at once:
//    ~42 MB on a cold cache); after S.LoginSuccess the door plays only if every frame is ready within DOOR_WAIT_MS
//    (the classic rule), else the select screen opens at once.
//  - Login card: account ID / password with the Globals regexes (LOG IN is off until both pass), show / hide password,
//    Caps Lock warning, "Remember account" (localStorage 'crystal.account', the ID only, never the password), C.Login
//    with a busy guard until the answer; S.Login 0-5 (5 opens Change password pre-filled), S.LoginBanned with a LIVE
//    countdown to ExpiryDate, S.LoginFailed tries left / connection tries / lock (accountDetails store).
//  - Create account: a three-step wizard over C.NewAccount. Only ID and password are required; user name (<= 20),
//    birth date, e-mail (<= 50), secret question and answer (<= 30) are optional free text as in the classic dialog.
//    S.NewAccount 0-8 (the server decides whether an ID is taken).
//  - Change password: C.ChangePassword, S.ChangePassword 0-6, S.ChangePasswordBanned.
//  - On-screen keyboard (InputKeyDialog): digits + letters, Random shuffle, Delete, Enter, Esc; Caps Lock picks the case.
//  - Server card: connection state, the version check result (Screens calls showLogin() on S.ClientVersion 1) and the
//    ping measured from S.KeepAlive (the server echoes C.KeepAlive.Time; Session sends one when idle, this screen one
//    at showLogin()).
// Stripped from the mock: in-memory accounts and test credentials, the ID prefill, fake stages / timings / verdicts,
// local ban / lock / create / change logic, local "taken" check, fixed secret questions, mandatory wizard fields, fake
// players / ping, hard-coded server address, NEWS, the no-op language toggle and Quit = hide. Hidden (no data / no
// system): players online, news, language. Close = session.close() like the classic Close button.
// Keys: element-level handlers on this screen's own inputs / buttons only (no router before S.StartGame); the popups
// get theirs from accountRoot.ts. Timers and listeners live in the root's scope (stopped on dispose).

import type { ServerPacket, SelectInfo } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import type { Session } from "../../../game/session.ts";
import { accountDetails } from "../../../game/accountDetails.ts";
import { setSetting } from "../../../game/settings.ts";
import { Lib, libPicture, libPictureNow } from "../../mir/controls.ts";
import { Globals, Text, binaryFromWallMs, expiryClock, type SceneHost } from "../../accountCommon.ts";
import { asButton, h, injectCss, later, reducedMotion, restartAnim, setText, windowInterval } from "../core/dom.ts";
import { svg, ICONS } from "../core/icons.ts";
import { plural } from "../core/format.ts";
import { AccountRoot } from "./accountRoot.ts";

type P<T extends ServerPacket["type"]> = Extract<ServerPacket, { type: T }>;

/** What Screens uses of a login scene (the classic LoginScene has the same shape; ctor(parent, host, onExit)). */
export interface LoginUi {
  /** S.ClientVersion Result 1 (or back from the game): the server is reachable and the client is up to date. */
  showLogin(): void;
  /** Account packets + S.KeepAlive (ping); true = consumed. `onSelect` runs once S.LoginSuccess has played the door. */
  handle(p: ServerPacket, onSelect: (characters: SelectInfo[]) => void): boolean;
  dispose(): void;
}

export interface ModernLoginOptions {
  /** The connection: state for the server card, S.KeepAlive round trips, the S.LoginFailed store. */
  session: Session;
  /** Shown as "Classic interface" in the footer: UiStyle is already saved as Classic when this runs; Screens swaps to
   *  the classic LoginScene. Omitted = no link. */
  onClassic?: () => void;
}

/** The modern LoginScene; same interface as the classic one. */
export function createModernLogin(parent: HTMLElement, host: SceneHost, onExit: () => void, opts: ModernLoginOptions): LoginUi {
  return new ModernLoginScene(parent, host, onExit, opts);
}

const ID_MIN = Globals.MinAccountIDLength, ID_MAX = Globals.MaxAccountIDLength, PW_MIN = Globals.MinPasswordLength, PW_MAX = Globals.MaxPasswordLength;
const ACCOUNT_REG = new RegExp(`^[A-Za-z0-9]{${ID_MIN},${ID_MAX}}$`);
const PASSWORD_REG = new RegExp(`^[A-Za-z0-9]{${PW_MIN},${PW_MAX}}$`);
/** loginScene.ts EMAIL_REG (the desktop's). */
const EMAIL_REG = /\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*/;
/** NewAccountDialog text box limits. */
const USER_NAME_MAX = 20, EMAIL_MAX = 50, SECRET_MAX = 30;
/** loginScene.ts DOOR_WAIT_MS: how long a login waits for the door cycle (ChrSel 0-18) before skipping it. */
const DOOR_WAIT_MS = 1000;
const DOOR_FRAMES = 19, DOOR_DELAY = 100;
const REMEMBER_KEY = "crystal.account";
/** "Show password" icon (ICONS.eye) and its struck-through "hide" form. */
const EYE_OFF = ICONS.eye + "M4 4l16 16";

type View = "login" | "create" | "change";
type Check = (v: string) => string; // "" = fine, else why not
interface Field { key: string; input: HTMLInputElement; wrap: HTMLElement; check?: Check; optional?: boolean }
/** The card's message: plain text, a success, or a live countdown (ban / lock) rebuilt every second. */
interface Msg { text: string; ok?: boolean; live?: () => string }

const CSS = `
.hudm .lgscr { position: absolute; inset: 0; overflow: hidden; color: var(--tx); background: #000; font-size: 13px; }
.hudm .lgscr .bg { position: absolute; inset: 0; transform: scale(1.04); animation: acl-zoom 30s ease-in-out infinite alternate; }
.hudm .lgscr .bg img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: .72; }
.hudm .lgscr.opening .bg { animation: acl-push 2.2s ease-in forwards; } .hudm .lgscr.opening .bg img { opacity: 1; }
@keyframes acl-push { to { transform: scale(1.5); } }
@keyframes acl-zoom { to { transform: scale(1.1) translate(-1%, -1%); } }
.hudm .lgscr .shade { position: absolute; inset: 0; background: radial-gradient(ellipse 60% 70% at 30% 50%, transparent, rgba(0,0,0,.65) 80%), linear-gradient(90deg, rgba(0,0,0,.55), transparent 40%, transparent 60%, rgba(0,0,0,.6)); }
.hudm .lgscr .fog { position: absolute; left: -50%; right: -50%; bottom: 0; height: 45%; opacity: .35; background: radial-gradient(ellipse at 30% 80%, color-mix(in srgb, var(--ac) 25%, transparent), transparent 60%), radial-gradient(ellipse at 70% 90%, rgba(180,200,255,.18), transparent 55%); animation: acl-fog 18s linear infinite alternate; }
@keyframes acl-fog { to { transform: translateX(15%); } }
.hudm .lgscr .ember { position: absolute; bottom: -10px; width: 3px; height: 3px; border-radius: 50%; background: #ffb36a; box-shadow: 0 0 6px #ff8c3a; animation: acl-ember linear infinite; }
@keyframes acl-ember { to { transform: translateY(-820px) translateX(var(--sx)); opacity: 0; } }
.hudm .lgscr .logo { position: absolute; left: 70px; top: 46px; pointer-events: none; }
.hudm .lgscr .logo b { display: block; font: 900 50px/1 Georgia, "Times New Roman", serif; letter-spacing: .06em; color: transparent;
  background: linear-gradient(100deg, #8a6a2a 0%, #ffe9a8 30%, #fff 38%, #ffe9a8 46%, #b8902f 70%, #6a4a1a 100%); -webkit-background-clip: text; background-clip: text;
  filter: drop-shadow(0 4px 10px rgba(0,0,0,.8)); }
.hudm .lgscr .logo span { display: block; margin-top: 8px; font-size: 13px; letter-spacing: .5em; color: color-mix(in srgb, var(--ac) 80%, #fff); text-shadow: 0 0 10px var(--glow); }
.hudm .lgscr .glass { position: absolute; border-radius: calc(var(--rad) * 1.4); background: color-mix(in srgb, var(--pb) 88%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 20px 50px rgba(0,0,0,.6), inset 0 1px 0 rgba(255,255,255,.06); }
.hudm .lgscr .card { left: 70px; top: 150px; width: 340px; max-height: 568px; overflow-y: auto; padding: 20px 24px; display: flex; flex-direction: column; gap: 10px; animation: acl-in .5s ease-out; }
@keyframes acl-in { from { opacity: 0; transform: translateY(14px); } }
.hudm .lgscr .card h2 { margin: 0; font-size: 18px; color: #fff; } .hudm .lgscr .card h2 small { display: block; font-size: 11px; font-weight: 400; color: var(--dim); margin-top: 2px; }
.hudm .lgscr .fld { position: relative; }
.hudm .lgscr .fld label { display: block; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 4px; }
.hudm .lgscr .fld label i { font-style: normal; font-weight: 400; letter-spacing: .04em; text-transform: none; opacity: .8; }
.hudm .lgscr .fld input { width: 100%; height: 36px; padding: 0 38px 0 12px; border-radius: calc(var(--rad) * .9); font: inherit; font-size: 14px; color: #fff; outline: none;
  border: 1px solid color-mix(in srgb, var(--fr) 90%, transparent); background: rgba(3,5,10,.7); transition: border-color .15s, box-shadow .15s; color-scheme: dark; }
.hudm .lgscr .fld input:focus { border-color: var(--ac); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ac) 20%, transparent); }
.hudm .lgscr .fld.bad input { border-color: var(--bad); } .hudm .lgscr .fld.good input { border-color: var(--ok); }
.hudm .lgscr .fld .eye { position: absolute; right: 8px; top: 24px; width: 24px; height: 24px; padding: 0; cursor: pointer; color: var(--dim); display: flex; align-items: center; justify-content: center; border: 0; background: none; }
.hudm .lgscr .fld .eye:hover { color: #fff; } .hudm .lgscr .fld .eye svg { width: 18px; height: 18px; }
.hudm .lgscr .fld .lghint { font-size: 10px; color: var(--dim); margin-top: 3px; min-height: 12px; } .hudm .lgscr .fld.bad .lghint { color: var(--bad-soft); } .hudm .lgscr .fld.good .lghint { color: var(--ok); }
.hudm .lgscr .caps { font-size: 11px; color: var(--warn); }
.hudm .lgscr .line { display: flex; justify-content: space-between; align-items: center; gap: 8px; font-size: 12px; }
.hudm .lgscr .line a, .hudm .lgscr .foot a { color: var(--ac); cursor: pointer; } .hudm .lgscr .line a:hover { text-decoration: underline; }
.hudm .lgscr .line label { display: flex; align-items: center; gap: 6px; cursor: pointer; } .hudm .lgscr input[type=checkbox] { accent-color: var(--ac); margin: 0; }
.hudm .lgscr .go { position: relative; height: 42px; display: flex; align-items: center; justify-content: center; cursor: pointer; overflow: hidden; border-radius: calc(var(--rad) * .9); font-size: 15px; font-weight: 800; letter-spacing: .14em; color: #fff;
  border: 1px solid var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 55%, var(--pb)), color-mix(in srgb, var(--ac) 25%, var(--pb))); box-shadow: 0 0 0 1px #000, 0 0 20px var(--glow); }
.hudm .lgscr .go:hover { border-color: #fff; } .hudm .lgscr .go.busy { cursor: progress; }
.hudm .lgscr .go.busy::after { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 40%; background: linear-gradient(90deg, transparent, rgba(255,255,255,.35), transparent); animation: acl-busy 1s linear infinite; }
@keyframes acl-busy { from { transform: translateX(-100%); } to { transform: translateX(250%); } }
.hudm .lgscr .go.dis { opacity: .4; cursor: not-allowed; box-shadow: 0 0 0 1px #000; }
.hudm .lgscr .alt { height: 34px; display: flex; align-items: center; justify-content: center; gap: 6px; cursor: pointer; border-radius: calc(var(--rad) * .9); font-size: 13px; font-weight: 600; border: 1px solid var(--fr); background: rgba(3,5,10,.5); }
.hudm .lgscr .alt:hover { border-color: var(--ac); color: #fff; } .hudm .lgscr .alt.on { border-color: var(--ac); color: #fff; }
.hudm .lgscr .alt.sq { width: 34px; flex: none; }
.hudm .lgscr .err { padding: 9px 11px; border-radius: calc(var(--rad) * .8); font-size: 12px; line-height: 1.45; color: #ffd0c8; border: 1px solid color-mix(in srgb, var(--bad) 70%, #000); background: color-mix(in srgb, var(--bad) 22%, transparent); white-space: pre-line; user-select: text; }
.hudm .lgscr .err.shake { animation: acl-shake .35s; }
.hudm .lgscr .err.ok { color: #c8ffd4; border-color: color-mix(in srgb, var(--ok) 70%, #000); background: color-mix(in srgb, var(--ok) 18%, transparent); }
@keyframes acl-shake { 25%, 75% { transform: translateX(-5px); } 50% { transform: translateX(5px); } }
.hudm .lgscr .steps { display: flex; gap: 6px; } .hudm .lgscr .steps div { flex: 1; height: 4px; border-radius: 2px; background: rgba(255,255,255,.12); } .hudm .lgscr .steps div.on { background: var(--ac); }
.hudm .lgscr .str { display: flex; gap: 3px; margin-top: -6px; } .hudm .lgscr .str i { flex: 1; height: 3px; border-radius: 2px; background: rgba(255,255,255,.12); }
.hudm .lgscr .str i.on1 { background: var(--bad); } .hudm .lgscr .str i.on2 { background: var(--warn); } .hudm .lgscr .str i.on3 { background: var(--ok); }
.hudm .lgscr .two { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; } .hudm .lgscr .two .alt, .hudm .lgscr .two .go { height: 38px; }
.hudm .lgscr .row { display: flex; gap: 8px; } .hudm .lgscr .row .alt:not(.sq) { flex: 1; }
.hudm .lgscr .note { font-size: 11px; color: var(--dim); line-height: 1.4; }
.hudm .lgscr .srv { right: 60px; top: 60px; width: 300px; padding: 16px 18px; display: flex; flex-direction: column; gap: 10px; }
.hudm .lgscr .srv h4 { margin: 0; font-size: 10px; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); }
.hudm .lgscr .sv { display: flex; align-items: center; gap: 12px; }
.hudm .lgscr .sv .dot { position: relative; width: 10px; height: 10px; flex: none; border-radius: 50%; background: var(--warn); }
.hudm .lgscr .sv .dot::after { content: ""; position: absolute; inset: 0; border-radius: 50%; box-shadow: 0 0 10px 1px currentColor; color: var(--warn); opacity: 0; animation: acl-pulse 2s ease-in-out infinite; }
.hudm .lgscr .sv .dot.on { background: var(--ok); } .hudm .lgscr .sv .dot.on::after { color: var(--ok); }
.hudm .lgscr .sv .dot.off { background: var(--bad); } .hudm .lgscr .sv .dot.off::after { animation: none; }
@keyframes acl-pulse { 50% { opacity: 1; } }
.hudm .lgscr .sv b { font-size: 16px; color: #fff; } .hudm .lgscr .sv small { display: block; font-size: 11px; color: var(--dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 190px; }
.hudm .lgscr .ping { margin-left: auto; display: flex; align-items: flex-end; gap: 2px; height: 16px; } .hudm .lgscr .ping i { width: 4px; border-radius: 1px; background: rgba(255,255,255,.15); } .hudm .lgscr .ping i.on { background: var(--ok); }
.hudm .lgscr .kv { display: flex; justify-content: space-between; font-size: 12px; } .hudm .lgscr .kv span { color: var(--dim); } .hudm .lgscr .kv b { color: #fff; font-variant-numeric: tabular-nums; } .hudm .lgscr .kv b.ok { color: var(--ok); }
.hudm .lgscr .kbd { left: 430px; top: 300px; padding: 12px; display: flex; flex-direction: column; gap: 8px; animation: acl-in .25s ease-out; }
.hudm .lgscr .kbd .hd { display: flex; align-items: center; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .lgscr .kbd .grid { display: grid; grid-template-columns: repeat(6, 32px); gap: 2px; }
.hudm .lgscr .kbd .k { height: 30px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-weight: 700; border-radius: calc(var(--rad) * .6); border: 1px solid var(--fr); background: rgba(3,5,10,.6); color: var(--tx); }
.hudm .lgscr .kbd .k:hover { border-color: var(--ac); color: #fff; }
.hudm .lgscr .kbd .acts { display: grid; grid-template-columns: repeat(3, 1fr); gap: 4px; } .hudm .lgscr .kbd .acts .k { font-size: 11px; font-weight: 600; }
.hudm .lgscr .foot { position: absolute; left: 0; right: 0; bottom: 0; height: 40px; display: flex; align-items: center; gap: 16px; padding: 0 24px; font-size: 11px; color: var(--dim); background: linear-gradient(0deg, rgba(0,0,0,.7), transparent); }
.hudm .lgscr .foot .sp { flex: 1; } .hudm .lgscr .foot a { color: var(--tx); } .hudm .lgscr .foot a:hover { color: var(--ac); }
.hudm .lgscr.leave .card, .hudm .lgscr.leave .srv, .hudm .lgscr.leave .kbd, .hudm .lgscr.leave .logo { transition: opacity .4s, transform .4s; opacity: 0; transform: translateY(-20px); pointer-events: none; }
.hudm .lgscr.leave .foot, .hudm .lgscr.opening .shade, .hudm .lgscr.opening .fog { transition: opacity .5s; opacity: 0; pointer-events: none; }
.hudm .lgscr .fade { position: absolute; inset: 0; z-index: 5; background: #000; opacity: 0; transition: opacity .5s; pointer-events: none; } .hudm .lgscr .fade.on { opacity: 1; }
.hudm .lgscr .welcome { position: absolute; left: 50%; top: 50%; translate: -50% -50%; z-index: 6; font-size: 26px; font-weight: 800; letter-spacing: .2em; white-space: nowrap; color: #fff; text-shadow: 0 0 20px var(--glow); opacity: 0; transition: opacity .4s; pointer-events: none; }
.hudm .lgscr .welcome.on { opacity: 1; }
`;

/** The NewAccountDialog birth date (an <input type=date> value, YYYY-MM-DD) as wall-clock ms (UTC midnight), or null. */
function birthDateMs(v: string): number | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(v);
  if (!m) return null;
  const ms = Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
  return Number.isNaN(ms) ? null : ms;
}

function readRemembered(): string | null {
  try { return localStorage.getItem(REMEMBER_KEY); } catch { return null; }
}

/** "Hh Mm Ss" until `ms` (0 when passed). */
function timeLeft(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(s / 3600).toLocaleString("en-US")}h ${Math.floor(s / 60) % 60}m ${s % 60}s`;
}

export class ModernLoginScene implements LoginUi {
  private readonly acct: AccountRoot;
  private readonly el: HTMLElement;
  private readonly host: SceneHost;
  private readonly session: Session;
  private readonly onExit: () => void;
  private readonly signal: AbortSignal;

  private view: View = "login";
  private step = 0;
  /** Field values of the current view (passwords live only here, only while the screen does). */
  private form: Record<string, string> = {};
  private fields = new Map<string, Field>();
  private msg: Msg | null = null;
  private msgEl: HTMLElement | null = null;
  private goBtn: HTMLElement | null = null;
  private showPw = false;
  private remember = true;
  /** S.ClientVersion said yes (showLogin): LOG IN can be used. */
  private ready = false;
  /** Waiting for the server's answer to the current view's request (C.Login / C.NewAccount / C.ChangePassword). */
  private busy = false;
  /** S.LoginSuccess: the door is playing, nothing else may run. */
  private entering = false;
  /** The login field the on-screen keyboard types into (InputKeyDialog: GetFocussedTextBox). */
  private kbdTarget = "id";
  private kbd: HTMLElement | null = null;
  private letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");
  private numbers = "0123456789".split("");

  private readonly card: HTMLElement;
  private readonly bgImg: HTMLImageElement;
  private readonly status: { dot: HTMLElement; name: HTMLElement; sub: HTMLElement; bars: HTMLElement[]; ping: HTMLElement; version: HTMLElement };
  private ping = -1;
  private lastState = "";

  constructor(parent: HTMLElement, host: SceneHost, onExit: () => void, opts: ModernLoginOptions) {
    this.host = host;
    this.session = opts.session;
    this.onExit = onExit;
    this.acct = new AccountRoot(parent, { cls: "acct-login" });
    this.signal = this.acct.signal;
    injectCss("screens-login", CSS);

    const remembered = readRemembered();
    this.remember = remembered !== ""; // "" = the player unticked "Remember account"; null = never asked (default on)
    this.form = { id: remembered ?? "" };

    // — Backdrop: ChrSel 0, then frames 1-18 streamed one at a time (LoginScene's _background) —
    this.bgImg = h("img");
    this.bgImg.alt = "";
    this.bgImg.draggable = false;
    void this.streamDoor();
    const embers: HTMLElement[] = [];
    if (!reducedMotion()) {
      for (let i = 0; i < 14; i++) {
        const e = h("div", "ember");
        Object.assign(e.style, { left: Math.random() * 100 + "%", animationDuration: 7 + Math.random() * 8 + "s", animationDelay: -Math.random() * 12 + "s" });
        e.style.setProperty("--sx", Math.random() * 120 - 60 + "px");
        embers.push(e);
      }
    }
    const logo = h("div", "logo", h("b", "", "LEGEND OF MIR"), h("span", "", "CRYSTAL"));

    // — Server card: state, address, ping, version (no player count: no data) —
    const bars: HTMLElement[] = [];
    const barsEl = h("div", "ping");
    for (let i = 0; i < 4; i++) { const b = h("i"); b.style.height = 4 + i * 4 + "px"; bars.push(b); barsEl.append(b); }
    const dot = h("span", "dot"), name = h("b", "", "Server"), sub = h("small", "", location.host);
    const ping = h("b", "", "—"), version = h("b", "", "Checking…");
    this.status = { dot, name, sub, bars, ping, version };
    const srv = h("div", "glass srv", h("h4", "", "Server"), h("div", "sv", dot, h("div", "", name, sub), barsEl),
      h("div", "kv", h("span", "", "Latency"), ping), h("div", "kv", h("span", "", "Client version"), version));

    // — Footer: build label (LoginScene's version label), interface switch, Close —
    const foot = h("div", "foot", h("span", "", "Build: Crystal.Release.Web"), h("span", "sp"));
    if (opts.onClassic) {
      const onClassic = opts.onClassic;
      const classic = asButton(h("a", "", "Classic interface"));
      classic.title = "Switch to the classic (desktop-look) interface";
      classic.addEventListener("click", () => { if (this.entering) return; setSetting("UiStyle", "Classic"); onClassic(); }, { signal: this.signal });
      foot.append(classic);
    }
    const close = asButton(h("a", "", "Close"));
    close.addEventListener("click", () => { if (!this.entering) this.onExit(); }, { signal: this.signal });
    foot.append(close);

    this.card = h("div", "glass card");
    this.el = h("div", "lgscr", h("div", "bg", this.bgImg), h("div", "shade"), h("div", "fog"), ...embers, logo, this.card, srv, foot,
      h("div", "fade"), h("div", "welcome"));
    this.acct.scene.append(this.el);

    // S.LoginFailed (consumed by Screens into the store, right after S.Login): tries left / connection / lock lines.
    const off = accountDetails(this.session).changed.on((what) => { if (what === "loginFailed") this.loginFailed(); });
    this.signal.addEventListener("abort", off, { once: true });

    // Connection state + live countdowns: once a second, only while this screen exists.
    windowInterval(() => this.tick(), 1000, this.signal).on();
    this.tick();
    this.render();
  }

  // — LoginUi —

  showLogin(): void {
    if (this.acct.disposed) return;
    this.ready = true;
    setText(this.status.version, "✓ Up to date");
    this.status.version.classList.add("ok");
    this.tick();
    // a first ping now (Session only sends C.KeepAlive after 4 s idle)
    this.host.send(ClientPackets.KeepAlive(this.session.now()));
    this.refreshGo();
    if (this.view === "login") this.focusFirst();
  }

  handle(p: ServerPacket, onSelect: (characters: SelectInfo[]) => void): boolean {
    if (this.acct.disposed) return false;
    switch (p.type) {
      case "KeepAlive": this.keepAlive(p); return true;
      case "Login": this.loginResult(p); return true;
      case "LoginBanned":
        this.busy = false;
        if (this.view !== "login") this.setView("login");
        this.form.pw = "";
        this.setBan(p.Reason, p.ExpiryDate);
        this.render();
        return true;
      case "LoginSuccess": this.loginSuccess(p.Characters, onSelect); return true;
      case "NewAccount": this.newAccount(p); return true;
      case "ChangePassword": this.changePassword(p); return true;
      case "ChangePasswordBanned":
        this.busy = false;
        this.setView("login");
        this.setBan(p.Reason, p.ExpiryDate);
        this.render();
        return true;
      default: return false;
    }
  }

  dispose(): void {
    this.acct.dispose();
  }

  // — Server card —

  private keepAlive(p: P<"KeepAlive">): void {
    const rtt = Number(this.session.now() - p.Time);
    if (!Number.isFinite(rtt) || rtt < 0 || rtt > 60_000) return;
    this.ping = rtt;
    setText(this.status.ping, `${rtt} ms`);
    const n = rtt < 40 ? 4 : rtt < 80 ? 3 : rtt < 150 ? 2 : 1;
    this.status.bars.forEach((b, i) => b.classList.toggle("on", i < n));
  }

  /** Once a second: the connection state (Session has no state event) and the live ban / lock countdown. */
  private tick(): void {
    const st = this.session.state, key = st + (this.ready ? "+" : "");
    if (key !== this.lastState) {
      this.lastState = key;
      const { dot, name, ping, bars } = this.status;
      dot.classList.toggle("on", st === "open" && this.ready);
      dot.classList.toggle("off", st === "closed");
      setText(name, st === "closed" ? "Offline" : st === "connecting" ? "Connecting…" : this.ready ? "Online" : "Checking…");
      if (st === "closed") { setText(ping, "—"); for (const b of bars) b.classList.remove("on"); this.ping = -1; }
      this.refreshGo();
    }
    if (this.msg?.live && this.msgEl) setText(this.msgEl, this.msg.live());
  }

  // — Door —

  private async streamDoor(): Promise<void> {
    const first = await libPicture(Lib.ChrSel, 0);
    if (this.signal.aborted) return;
    if (first && !this.entering) this.bgImg.src = first.url;
    for (let i = 1; i < DOOR_FRAMES && !this.signal.aborted; i++) await libPicture(Lib.ChrSel, i);
  }

  private doorReady(): boolean {
    for (let i = 0; i < DOOR_FRAMES; i++) if (libPictureNow(Lib.ChrSel, i) === undefined) return false;
    return true;
  }

  /** LoginScene.Login(S.LoginSuccess): the door plays if its frames are ready within DOOR_WAIT_MS, then the select
   *  screen; otherwise the select screen at once (a returning player has the door cached). */
  private loginSuccess(characters: SelectInfo[], onSelect: (characters: SelectInfo[]) => void): void {
    if (this.entering) return;
    this.entering = true;
    this.busy = false;
    this.form = { id: this.form.id ?? "" }; // the password goes now
    this.closeKeyboard();
    for (const f of this.fields.values()) f.input.disabled = true;
    this.el.classList.add("leave");
    const t0 = performance.now();
    let done = false;
    const finish = () => { if (done || this.signal.aborted) return; done = true; onSelect(characters); };
    const wait = () => {
      if (this.signal.aborted) return;
      if (this.doorReady()) { this.playDoor(finish); return; }
      if (performance.now() - t0 >= DOOR_WAIT_MS) { finish(); return; }
      later(wait, DOOR_DELAY, this.signal);
    };
    wait();
  }

  private playDoor(finish: () => void): void {
    this.el.classList.add("opening");
    const welcome = this.el.querySelector<HTMLElement>(".welcome");
    if (welcome && this.form.id) { setText(welcome, `WELCOME BACK, ${this.form.id.toUpperCase()}`); later(() => welcome.classList.add("on"), 700, this.signal); }
    let frame = 0;
    const step = () => {
      if (this.signal.aborted) return;
      frame++;
      if (frame < DOOR_FRAMES) {
        const pic = libPictureNow(Lib.ChrSel, frame);
        if (pic) this.bgImg.src = pic.url;
        later(step, DOOR_DELAY, this.signal);
        return;
      }
      this.el.querySelector(".fade")?.classList.add("on");
      later(finish, 450, this.signal);
    };
    later(step, DOOR_DELAY, this.signal);
  }

  // — Rendering —

  private setView(v: View): void {
    this.view = v;
    this.step = 0;
    this.msg = null;
    this.busy = false;
    if (v !== "login") this.closeKeyboard();
  }

  private say(text: string, ok = false): void { this.msg = { text, ok }; }

  private setBan(reason: string, expiry: bigint): void {
    const exp = expiryClock(expiry);
    const head = `This account is banned.\nReason: ${reason}\nExpires: ${exp.label}`;
    this.msg = { text: "", live: () => { const left = exp.leftMs(); return left > 0 ? `${head}\nTime left: ${timeLeft(left)}` : `${head}\nThe ban has ended: you can log in again.`; } };
  }

  /** Rebuilds the card for the current view (on view / step changes and server answers, never per frame). */
  private render(): void {
    if (this.signal.aborted) return;
    this.fields.clear();
    this.goBtn = null;
    const kids = this.view === "login" ? this.loginCard() : this.view === "create" ? this.createCard() : this.changeCard();
    this.card.replaceChildren(...kids);
    this.card.classList.toggle("busy", this.busy);
    this.refreshGo();
  }

  private messageEl(): HTMLElement | null {
    this.msgEl = null;
    const m = this.msg;
    if (!m) return null;
    const el = h("div", "err" + (m.ok ? " ok" : ""));
    el.setAttribute("role", m.ok ? "status" : "alert");
    setText(el, m.live ? m.live() : m.text);
    if (!m.ok) restartAnim(el, "shake");
    this.msgEl = el;
    return el;
  }

  private focusFirst(): void {
    later(() => {
      if (this.entering) return;
      for (const f of this.fields.values()) if (!f.input.disabled && (!f.input.value || (f.check && f.check(f.input.value)))) { f.input.focus(); return; }
      this.fields.values().next().value?.input.focus();
    }, 0, this.signal);
  }

  private focus(key: string, clear = false): void {
    const f = this.fields.get(key);
    if (!f) return;
    if (clear) { f.input.value = ""; this.form[key] = ""; this.validate(f); }
    later(() => f.input.focus(), 0, this.signal);
  }

  // — Fields —

  private field(key: string, label: string, o: { password?: boolean; type?: string; max?: number; check?: Check; optional?: boolean; placeholder?: string; describe?: string } = {}): HTMLElement {
    const input = h("input");
    input.type = o.password ? (this.showPw ? "text" : "password") : o.type ?? "text";
    input.value = this.form[key] ?? "";
    if (o.max) input.maxLength = o.max;
    input.placeholder = o.placeholder ?? "";
    input.autocomplete = "off";
    input.spellcheck = false;
    input.name = "acl-" + key;
    if (o.password) input.dataset.pw = "1";
    if (o.describe) input.title = o.describe;
    const wrap = h("div", "fld", h("label", "", label, o.optional ? h("i", "", " (optional)") : null), input);
    const f: Field = { key, input, wrap, check: o.check, optional: o.optional };
    this.fields.set(key, f);
    input.addEventListener("input", () => {
      this.form[key] = input.value;
      this.validate(f);
      if (key === "npw") { const c = this.fields.get("npw2"); if (c) this.validate(c); this.strength(); }
      this.refreshGo();
    }, { signal: this.signal });
    input.addEventListener("focus", () => { if (key === "id" || key === "pw") this.kbdTarget = key; }, { signal: this.signal });
    const caps = (e: KeyboardEvent) => { if (o.password) { const c = this.card.querySelector<HTMLElement>(".caps"); if (c) c.hidden = !e.getModifierState("CapsLock"); } };
    input.addEventListener("keyup", caps, { signal: this.signal });
    input.addEventListener("keydown", (e) => {
      e.stopPropagation();
      caps(e);
      if (e.repeat) return;
      if (e.key === "Enter") { e.preventDefault(); this.submit(); }
      else if (e.key === "Escape" && this.view !== "login") { e.preventDefault(); this.back(); }
    }, { signal: this.signal });
    if (o.password) {
      const eye = h("button", "eye", svg(this.showPw ? EYE_OFF : "eye", "ic"));
      eye.type = "button";
      eye.tabIndex = -1;
      eye.title = this.showPw ? "Hide password" : "Show password";
      eye.setAttribute("aria-label", eye.title);
      eye.addEventListener("mousedown", (e) => e.preventDefault(), { signal: this.signal }); // keep the focus in the field
      eye.addEventListener("click", () => this.togglePasswords(), { signal: this.signal });
      wrap.append(eye);
    }
    if (o.check) wrap.append(h("div", "lghint"));
    this.validate(f);
    return wrap;
  }

  /** Show / hide every password field of the card in place (no rebuild: the focus and caret stay). */
  private togglePasswords(): void {
    this.showPw = !this.showPw;
    for (const f of this.fields.values()) {
      if (!f.input.dataset.pw) continue;
      f.input.type = this.showPw ? "text" : "password";
      const eye = f.wrap.querySelector<HTMLButtonElement>(".eye");
      if (eye) {
        eye.replaceChildren(svg(this.showPw ? EYE_OFF : "eye", "ic"));
        eye.title = this.showPw ? "Hide password" : "Show password";
        eye.setAttribute("aria-label", eye.title);
      }
    }
  }

  /** The desktop's red / green text-box borders: empty = neutral; optional fields may stay empty. */
  private validate(f: Field): void {
    const v = f.input.value, hint = f.wrap.querySelector(".lghint");
    if (!f.check || !v) { f.wrap.classList.remove("bad", "good"); if (hint) setText(hint, ""); return; }
    const why = f.check(v);
    f.wrap.classList.toggle("bad", !!why);
    f.wrap.classList.toggle("good", !why);
    if (hint) setText(hint, why || "✓");
  }

  private valid(key: string): boolean {
    const f = this.fields.get(key);
    if (!f) return true;
    const v = f.input.value;
    if (!v) return !!f.optional;
    return !f.check || !f.check(v);
  }

  private checkId: Check = (v) => (ACCOUNT_REG.test(v) ? "" : `${ID_MIN}-${ID_MAX} letters or digits`);
  private checkPw: Check = (v) => (PASSWORD_REG.test(v) ? "" : `${PW_MIN}-${PW_MAX} letters or digits`);

  private strength(): void {
    const v = this.form.npw ?? "", s = !v ? 0 : PASSWORD_REG.test(v) ? (/[0-9]/.test(v) && /[A-Za-z]/.test(v) ? (v.length >= 10 ? 3 : 2) : 1) : 1;
    this.card.querySelectorAll(".str i").forEach((e, i) => { e.className = i < s ? "on" + s : ""; });
  }

  private goButton(label: string): HTMLElement {
    const go = asButton(h("div", "go", label));
    go.addEventListener("click", () => this.submit(), { signal: this.signal });
    this.goBtn = go;
    return go;
  }

  private altButton(label: string, onClick: () => void, cls = ""): HTMLElement {
    const b = asButton(h("div", "alt" + (cls ? " " + cls : ""), label));
    b.addEventListener("click", () => { if (!this.busy && !this.entering) onClick(); }, { signal: this.signal });
    return b;
  }

  /** Whether the current view's primary button may be pressed. */
  private canSubmit(): boolean {
    if (this.busy || this.entering || !this.ready || this.session.state !== "open") return false;
    if (this.view === "login") return this.valid("id") && this.valid("pw") && !!this.form.id && !!this.form.pw;
    if (this.view === "change") return ["id", "cpw", "npw", "npw2"].every((k) => !!this.form[k] && this.valid(k));
    return this.stepOk();
  }

  private refreshGo(): void {
    const go = this.goBtn;
    if (!go) return;
    const label = this.busy ? (this.view === "login" ? "LOGGING IN…" : "PLEASE WAIT…")
      : !this.ready ? (this.session.state === "closed" ? "OFFLINE" : "CONNECTING…")
      : this.view === "login" ? "LOG IN" : this.view === "change" ? "CHANGE PASSWORD" : this.step < 2 ? "NEXT" : "CREATE ACCOUNT";
    setText(go, label);
    go.classList.toggle("busy", this.busy || (!this.ready && this.session.state !== "closed"));
    const can = this.canSubmit();
    go.classList.toggle("dis", !can && !this.busy);
    go.setAttribute("aria-disabled", String(!can));
  }

  private submit(): void {
    if (this.view === "login") this.login();
    else if (this.view === "change") this.sendChange();
    else this.nextStep();
  }

  private back(): void {
    if (this.busy || this.entering) return;
    if (this.view === "create" && this.step > 0) { this.step--; this.msg = null; this.render(); this.focusFirst(); return; }
    const id = this.view === "change" ? this.form.id ?? "" : readRemembered() ?? this.form.id ?? "";
    this.setView("login");
    this.form = { id };
    this.render();
    this.focusFirst();
  }

  // — Login (LoginDialog) —

  private loginCard(): HTMLElement[] {
    const rem = h("input");
    rem.type = "checkbox";
    rem.checked = this.remember;
    rem.addEventListener("change", () => { this.remember = rem.checked; }, { signal: this.signal });
    const change = asButton(h("a", "", "Change password"));
    change.addEventListener("click", () => { if (this.busy || this.entering) return; const id = this.form.id ?? ""; this.setView("change"); this.form = { id }; this.render(); this.focusFirst(); }, { signal: this.signal });
    const caps = h("div", "caps", "⇪ Caps Lock is on");
    caps.hidden = true;
    const kbd = this.altButton("", () => this.toggleKeyboard(), "sq" + (this.kbd ? " on" : ""));
    kbd.append(svg("keyboard", "ic"));
    kbd.title = "On-screen keyboard";
    kbd.setAttribute("aria-label", kbd.title);
    const create = this.altButton("Create a new account", () => { this.setView("create"); this.form = {}; this.render(); this.focusFirst(); });
    return [h("h2", "", "Welcome back", h("small", "", "Log in to your account")), this.messageEl(),
      this.field("id", "Account ID", { max: ID_MAX, check: this.checkId, describe: `Letters and digits, ${ID_MIN}-${ID_MAX} characters` }),
      this.field("pw", "Password", { password: true, max: PW_MAX, describe: `Letters and digits, ${PW_MIN}-${PW_MAX} characters` }),
      caps, h("div", "line", h("label", "", rem, "Remember account"), change), this.goButton("LOG IN"), h("div", "row", create, kbd)].filter((k): k is HTMLElement => !!k);
  }

  /** LoginDialog.Login: C.Login, the button off until the answer. */
  private login(): void {
    if (!this.canSubmit()) {
      if (!this.busy && !this.entering && this.ready) { if (!this.valid("id") || !this.form.id) this.focus("id"); else this.focus("pw"); }
      return;
    }
    const id = this.form.id ?? "", pw = this.form.pw ?? "";
    try { localStorage.setItem(REMEMBER_KEY, this.remember ? id : ""); } catch { /* storage blocked */ }
    this.busy = true;
    this.msg = null;
    this.msgEl?.remove();
    this.msgEl = null;
    this.refreshGo();
    this.host.send(ClientPackets.Login({ AccountID: id, Password: pw }));
  }

  /** S.Login (LoginScene.Login(S.Login)). */
  private loginResult(p: P<"Login">): void {
    this.busy = false;
    if (this.view !== "login") { this.setView("login"); }
    let focus = "pw", clearPw = false;
    switch (p.Result) {
      case 0: this.say(Text.LoginDisabled); this.form = { id: "" }; focus = "id"; break;
      case 1: this.say(Text.AccountIdNotAcceptable); focus = "id"; break;
      case 2: this.say(Text.PasswordNotAcceptable); break;
      case 3: this.say(Text.NoAccountID); focus = "id"; break;
      case 4: this.say(Text.IncorrectPasswordAccountID); clearPw = true; break;
      case 5: {
        // the password must change first: Change password, pre-filled with the ID and the password just typed
        const id = this.form.id ?? "", cpw = this.form.pw ?? "";
        this.setView("change");
        this.form = { id, cpw };
        this.say(Text.AccountPasswordMustChangeBeforeLogin);
        this.render();
        this.focus("npw");
        return;
      }
      default: this.say(`Login failed (${p.Result}).`); break;
    }
    if (clearPw) this.form.pw = "";
    this.render();
    this.focus(focus);
  }

  /** S.LoginFailed (right after S.Login 3 / 4): how many tries are left before the account lock / the connection
   *  closes, and when a lock ends. */
  private loginFailed(): void {
    const info = accountDetails(this.session).loginFailed;
    if (!info || this.view !== "login" || !this.msg || this.msg.ok || this.msg.live) return;
    const lines = [this.msg.text];
    const a = info.AccountTriesLeft, c = info.ConnectionTriesLeft;
    if (a > 0) lines.push(`${plural(a, "more wrong password")} allowed before the account is locked.`);
    else if (a === 0) lines.push("One more wrong password locks this account.");
    const until = accountDetails(this.session).lockedUntilMs;
    if (c === 0) {
      const head = lines.join("\n") + "\nToo many failed log-ins: this connection will close.";
      if (until > 0) this.msg = { text: "", live: () => { const left = until - Date.now(); return left > 0 ? `${head}\nTry again in ${timeLeft(left)}.` : `${head}\nYou can try again now.`; } };
      else this.msg = { text: head };
    } else {
      if (c > 0 && c <= 2) lines.push(`This connection closes after ${plural(c, "more failed log-in")}.`);
      this.msg = { text: lines.join("\n") };
    }
    const el = this.msgEl;
    if (el) setText(el, this.msg.live ? this.msg.live() : this.msg.text);
  }

  // — On-screen keyboard (InputKeyDialog) —

  private toggleKeyboard(): void {
    if (this.kbd) { this.closeKeyboard(); return; }
    if (this.view !== "login" || this.entering) return;
    const kbd = h("div", "glass kbd");
    kbd.addEventListener("mousedown", (e) => e.preventDefault(), { signal: this.signal }); // keep the focus in the text box
    this.kbd = kbd;
    this.buildKeyboard();
    this.el.insertBefore(kbd, this.el.querySelector(".foot"));
    this.card.querySelector(".alt.sq")?.classList.add("on");
    this.fields.get(this.kbdTarget)?.input.focus();
  }

  private closeKeyboard(): void {
    this.kbd?.remove();
    this.kbd = null;
    this.card.querySelector(".alt.sq")?.classList.remove("on");
  }

  /** UpdateKeys: digits then letters, 6 a row; Esc / Delete / Enter / Random. */
  private buildKeyboard(): void {
    const kbd = this.kbd;
    if (!kbd) return;
    const key = (label: string, onClick: (e: MouseEvent) => void) => {
      const k = asButton(h("div", "k", label));
      k.addEventListener("click", onClick, { signal: this.signal });
      return k;
    };
    const grid = h("div", "grid");
    for (const ch of [...this.numbers, ...this.letters]) grid.append(key(ch, (e) => this.typeKey(ch, e)));
    const shuffle = (a: string[]) => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j]!, a[i]!]; } };
    const esc = key(Text.BtnEsc, () => this.closeKeyboard());
    kbd.replaceChildren(
      h("div", "hd", h("span", "", `Keyboard · ${this.kbdTarget === "pw" ? "password" : "account ID"}`), esc),
      grid,
      h("div", "acts",
        key(Text.BtnRandom, () => { shuffle(this.letters); shuffle(this.numbers); this.buildKeyboard(); }),
        key(Text.BtnDelete, () => this.deleteKey()),
        key(Text.BtnEnter, () => this.login())));
    esc.style.height = "22px";
    esc.style.padding = "0 8px";
  }

  private kbdField(): Field | undefined {
    return this.fields.get(this.kbdTarget) ?? this.fields.get("id");
  }

  private afterKey(f: Field): void {
    f.input.focus();
    f.input.setSelectionRange(f.input.value.length, f.input.value.length);
    f.input.dispatchEvent(new Event("input"));
    const hd = this.kbd?.querySelector(".hd span");
    if (hd) setText(hd, `Keyboard · ${this.kbdTarget === "pw" ? "password" : "account ID"}`);
  }

  /** SecureKeyPress: Caps Lock picks the case. */
  private typeKey(ch: string, e: MouseEvent): void {
    const f = this.kbdField();
    if (!f || f.input.disabled || f.input.value.length >= f.input.maxLength) return;
    f.input.value += e.getModifierState("CapsLock") ? ch.toUpperCase() : ch.toLowerCase();
    this.afterKey(f);
  }

  /** SecureKeyDelete: the selection, else the last character. */
  private deleteKey(): void {
    const f = this.kbdField();
    if (!f || f.input.disabled) return;
    const t = f.input.value, start = f.input.selectionStart ?? t.length, end = f.input.selectionEnd ?? start;
    f.input.value = end > start ? t.slice(0, start) + t.slice(end) : t.slice(0, -1);
    this.afterKey(f);
  }

  // — Create account (NewAccountDialog) —

  private createCard(): HTMLElement[] {
    const steps = h("div", "steps");
    for (let i = 0; i < 3; i++) steps.append(h("div", i <= this.step ? "on" : ""));
    const titles = ["Account", "About you", "Security"];
    const body: HTMLElement[] = [];
    if (this.step === 0) {
      body.push(
        this.field("nid", "Account ID", { max: ID_MAX, check: this.checkId }),
        this.field("npw", "Password", { password: true, max: PW_MAX, check: this.checkPw }), h("div", "str", h("i"), h("i"), h("i")),
        this.field("npw2", "Confirm password", { password: true, max: PW_MAX, check: (v) => this.checkPw(v) || (v === this.form.npw ? "" : "The passwords don't match") }));
      later(() => this.strength(), 0, this.signal);
    } else if (this.step === 1) {
      body.push(
        this.field("uname", "User name", { optional: true, max: USER_NAME_MAX, check: (v) => (v.length <= USER_NAME_MAX ? "" : `At most ${USER_NAME_MAX} characters`) }),
        this.field("birth", "Birth date", { optional: true, type: "date", check: (v) => (birthDateMs(v) !== null ? "" : "Not a date") }),
        this.field("email", "E-mail", { optional: true, max: EMAIL_MAX, check: (v) => (EMAIL_REG.test(v) && v.length <= EMAIL_MAX ? "" : "Not an e-mail address") }));
    } else {
      body.push(
        this.field("q", "Secret question", { optional: true, max: SECRET_MAX, placeholder: "Write your own question", check: (v) => (v.length <= SECRET_MAX ? "" : `At most ${SECRET_MAX} characters`) }),
        this.field("ans", "Secret answer", { optional: true, max: SECRET_MAX, check: (v) => (v.length <= SECRET_MAX ? "" : `At most ${SECRET_MAX} characters`) }),
        h("div", "note", "Optional: the server keeps them with your account."));
    }
    const back = this.altButton(this.step ? "Back" : "Cancel", () => this.back());
    return [h("h2", "", "Create account", h("small", "", `Step ${this.step + 1} of 3 · ${titles[this.step]}`)), steps, this.messageEl(), ...body,
      h("div", "two", back, this.goButton("NEXT"))].filter((k): k is HTMLElement => !!k);
  }

  /** Every field of the step valid (the classic NewAccountDialog enables OK only when all fields are). */
  private stepOk(): boolean {
    const f = this.form;
    if (this.step === 0) return ACCOUNT_REG.test(f.nid ?? "") && PASSWORD_REG.test(f.npw ?? "") && f.npw === f.npw2;
    if (this.step === 1) return ["uname", "birth", "email"].every((k) => this.valid(k));
    return ["q", "ans"].every((k) => this.valid(k));
  }

  private nextStep(): void {
    if (!this.canSubmit()) return;
    if (this.step < 2) { this.step++; this.msg = null; this.render(); this.focusFirst(); return; }
    const f = this.form, birth = f.birth ? birthDateMs(f.birth) : null;
    this.busy = true;
    this.refreshGo();
    this.host.send(ClientPackets.NewAccount({
      AccountID: f.nid ?? "", Password: f.npw ?? "", EMailAddress: f.email ?? "",
      // DateTime.MinValue (ToBinary() = 0) when left blank, as the classic dialog
      BirthDate: birth === null ? 0n : binaryFromWallMs(birth),
      UserName: f.uname ?? "", SecretQuestion: f.q ?? "", SecretAnswer: f.ans ?? "",
    }));
  }

  /** S.NewAccount (LoginScene.NewAccount). */
  private newAccount(p: P<"NewAccount">): void {
    if (this.view !== "create") return;
    this.busy = false;
    const at = (step: number, text: string, key: string, clear = false) => { this.step = step; this.say(text); this.render(); this.focus(key, clear); };
    switch (p.Result) {
      case 0: this.setView("login"); this.form = { id: readRemembered() ?? "" }; this.say(Text.AccountCreationDisabled); this.render(); break;
      case 1: at(0, Text.AccountIdNotAcceptable, "nid"); break;
      case 2: at(0, Text.PasswordNotAcceptable, "npw"); break;
      case 3: at(1, Text.EmailAddressNotAcceptable, "email"); break;
      case 4: at(1, Text.UserNameNotAcceptable, "uname"); break;
      case 5: at(2, Text.SecretQuestionNotAcceptable, "q"); break;
      case 6: at(2, Text.SecretAnswerNotAcceptable, "ans"); break;
      case 7: at(0, Text.AccountIdAlreadyExists, "nid", true); break;
      case 8: {
        const id = this.form.nid ?? "";
        this.setView("login");
        this.form = { id };
        this.say(Text.AccountCreatedSuccessfully, true);
        this.render();
        this.focus("pw");
        break;
      }
      default: this.say(`Account creation failed (${p.Result}).`); this.render(); break;
    }
  }

  // — Change password (ChangePasswordDialog) —

  private changeCard(): HTMLElement[] {
    const back = this.altButton("Back to log in", () => this.back());
    return [h("h2", "", "Change password", h("small", "", "You need your current password")), this.messageEl(),
      this.field("id", "Account ID", { max: ID_MAX, check: this.checkId }),
      this.field("cpw", "Current password", { password: true, max: PW_MAX, check: this.checkPw }),
      this.field("npw", "New password", { password: true, max: PW_MAX, check: this.checkPw }), h("div", "str", h("i"), h("i"), h("i")),
      this.field("npw2", "Confirm new password", { password: true, max: PW_MAX, check: (v) => this.checkPw(v) || (v === this.form.npw ? "" : "The passwords don't match") }),
      this.goButton("CHANGE PASSWORD"), back].filter((k): k is HTMLElement => !!k);
  }

  private sendChange(): void {
    if (!this.canSubmit()) return;
    this.busy = true;
    this.refreshGo();
    this.host.send(ClientPackets.ChangePassword({ AccountID: this.form.id ?? "", CurrentPassword: this.form.cpw ?? "", NewPassword: this.form.npw ?? "" }));
  }

  /** S.ChangePassword (LoginScene.ChangePassword). */
  private changePassword(p: P<"ChangePassword">): void {
    if (this.view !== "change") return;
    this.busy = false;
    const fail = (text: string, key: string, clear = false) => { this.say(text); this.render(); this.focus(key, clear); };
    switch (p.Result) {
      case 0: { const id = this.form.id ?? ""; this.setView("login"); this.form = { id }; this.say(Text.PasswordChangingDisabled); this.render(); this.focusFirst(); break; }
      case 1: fail(Text.AccountIdNotAcceptable, "id"); break;
      case 2: fail(Text.CurrentPasswordNotAcceptable, "cpw"); break;
      case 3: fail(Text.NewPasswordNotAcceptable, "npw"); break;
      case 4: fail(Text.NoAccountID, "id"); break;
      case 5: this.form.cpw = ""; fail(Text.IncorrectPasswordAccountID, "cpw", true); break;
      case 6: {
        const id = this.form.id ?? "";
        this.setView("login");
        this.form = { id };
        this.say(Text.PasswordChangedSuccessfully, true);
        this.render();
        this.focus("pw");
        break;
      }
      default: fail(`Password change failed (${p.Result}).`, "id"); break;
    }
  }
}
