// Login screen mock for the scene lab's UI tester: a modern, full-screen take on the desktop's LoginScene (LoginDialog,
// NewAccountDialog, ChangePasswordDialog) over the desktop's real login art: ChrSel 0 (the closed stone door with the
// MIR II medallion) as the backdrop, and ChrSel 0-18 at 100 ms, the door opening, when you log in (the desktop's _background). Glass login card (account ID, password with show / hide and a Caps Lock warning, remember me, staged
// progress: connecting, version check, logging in), the server's answers (no such account, wrong password with the tries
// left before Envir's lock: the 6th wrong try bans the account for 2 minutes, with a live countdown; banned accounts show
// the reason and expiry), a three-step create-account wizard and a change-password panel with the client's own checks
// (LoginScene: ID and passwords are letters and digits, Globals.Min/MaxAccountIDLength 3-15, Min/MaxPasswordLength 5-15;
// user name, birth date, email, secret question and answer), a server card (status, players, ping bars, version check),
// news, language (the client ships English and Chinese) and Quit. Logging in opens the door and fades into the HUD.
// Nothing is sent anywhere: the accounts are in-memory mock data. Simulate > Login screen.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { h, injectCss } from "./hudWindow.ts";

const ID_MIN = 3, ID_MAX = 15, PW_MIN = 5, PW_MAX = 15, LOCK_TRIES = 5, LOCK_MIN = 2;
const RX_ID = new RegExp(`^[A-Za-z0-9]{${ID_MIN},${ID_MAX}}$`), RX_PW = new RegExp(`^[A-Za-z0-9]{${PW_MIN},${PW_MAX}}$`);
interface Acct { pw: string; wrong: number; banned?: { reason: string; until: number } }
type View = "login" | "create" | "change";
const NEWS: [string, string, string][] = [
  ["Patch", "Web client update", "The browser client now has the scene lab's new HUD, chat and windows."],
  ["Event", "Double EXP weekend", "Friday 18:00 to Sunday 23:59, server time."],
  ["Guild war", "Nightfall vs GameMaster", "The siege of Bichon Wall starts Saturday at 20:00."],
];
const QUESTIONS = ["Name of your first pet?", "City you were born in?", "Favourite monster to hunt?", "Your childhood nickname?"];

const CSS = `
.hudm .lgscr { position: absolute; left: 0; top: 0; width: 100%; height: 100%; z-index: 40; pointer-events: auto; overflow: hidden; color: var(--tx); background: #000; font-size: 13px; }
.hudm .lgscr .bg { position: absolute; inset: 0; filter: saturate(.9) brightness(.7); transform: scale(1.04); animation: hudm-lgzoom 30s ease-in-out infinite alternate; transition: filter .6s; }
.hudm .lgscr .bg img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; }
.hudm .lgscr .bg img.on { opacity: 1; }
.hudm .lgscr.opening .bg { filter: none; animation: hudm-lgpush 2.4s ease-in forwards; } @keyframes hudm-lgpush { to { transform: scale(1.6); } }
@keyframes hudm-lgzoom { to { transform: scale(1.12) translate(-1%, -1%); } }
.hudm .lgscr .shade { position: absolute; inset: 0; background: radial-gradient(ellipse 60% 70% at 30% 50%, transparent, rgba(0,0,0,.65) 80%), linear-gradient(90deg, rgba(0,0,0,.55), transparent 40%, transparent 60%, rgba(0,0,0,.6)); }
.hudm .lgscr .fog { position: absolute; left: -50%; right: -50%; bottom: 0; height: 45%; opacity: .35; background: radial-gradient(ellipse at 30% 80%, color-mix(in srgb, var(--ac) 25%, transparent), transparent 60%), radial-gradient(ellipse at 70% 90%, rgba(180,200,255,.18), transparent 55%); animation: hudm-lgfog 18s linear infinite alternate; }
@keyframes hudm-lgfog { to { transform: translateX(15%); } }
.hudm .lgscr .ember { position: absolute; bottom: -10px; width: 3px; height: 3px; border-radius: 50%; background: #ffb36a; box-shadow: 0 0 6px #ff8c3a; animation: hudm-lgember linear infinite; }
@keyframes hudm-lgember { to { transform: translateY(-820px) translateX(var(--sx)); opacity: 0; } }
.hudm .lgscr .logo { position: absolute; left: 70px; top: 70px; }
.hudm .lgscr .logo b { display: block; font: 900 54px/1 Georgia, "Times New Roman", serif; letter-spacing: .06em; color: transparent;
  background: linear-gradient(100deg, #8a6a2a 0%, #ffe9a8 30%, #fff 38%, #ffe9a8 46%, #b8902f 70%, #6a4a1a 100%); background-size: 250% 100%; -webkit-background-clip: text; background-clip: text;
  filter: drop-shadow(0 4px 10px rgba(0,0,0,.8)) drop-shadow(0 0 18px rgba(255,200,90,.25)); animation: hudm-lgshine 6s ease-in-out infinite; }
@keyframes hudm-lgshine { from { background-position: 120% 0; } to { background-position: -120% 0; } }
.hudm .lgscr .logo span { display: block; margin-top: 8px; font-size: 13px; letter-spacing: .5em; color: color-mix(in srgb, var(--ac) 80%, #fff); text-shadow: 0 0 10px var(--glow); }
.hudm .lgscr .glass { position: absolute; border-radius: calc(var(--rad) * 1.4); background: color-mix(in srgb, var(--pb) 62%, transparent); backdrop-filter: blur(10px) saturate(1.2);
  border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 20px 50px rgba(0,0,0,.6), inset 0 1px 0 rgba(255,255,255,.06); }
.hudm .lgscr .card { left: 70px; top: 210px; width: 340px; padding: 22px 24px; display: flex; flex-direction: column; gap: 11px; animation: hudm-lgin .5s ease-out; }
@keyframes hudm-lgin { from { opacity: 0; transform: translateY(14px); } }
.hudm .lgscr .card h2 { margin: 0; font-size: 18px; color: #fff; } .hudm .lgscr .card h2 small { display: block; font-size: 11px; font-weight: 400; color: var(--dim); margin-top: 2px; }
.hudm .lgscr .fld { position: relative; }
.hudm .lgscr .fld label { display: block; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 4px; }
.hudm .lgscr .fld input, .hudm .lgscr .fld select { width: 100%; height: 38px; box-sizing: border-box; padding: 0 38px 0 12px; border-radius: calc(var(--rad) * .9); font-size: 14px; color: #fff; outline: none;
  border: 1px solid color-mix(in srgb, var(--fr) 90%, transparent); background: rgba(3,5,10,.7); transition: border-color .15s, box-shadow .15s; }
.hudm .lgscr .fld input:focus, .hudm .lgscr .fld select:focus { border-color: var(--ac); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ac) 20%, transparent); }
.hudm .lgscr .fld.bad input { border-color: #e05555; } .hudm .lgscr .fld.good input { border-color: #4fbf6a; }
.hudm .lgscr .fld .eye { position: absolute; right: 8px; bottom: 9px; width: 22px; height: 20px; cursor: pointer; color: var(--dim); display: flex; align-items: center; justify-content: center; }
.hudm .lgscr .fld .eye:hover { color: #fff; } .hudm .lgscr .fld .eye svg { width: 18px; height: 18px; fill: none; stroke: currentColor; stroke-width: 1.8; }
.hudm .lgscr .fld .lghint { font-size: 10px; color: var(--dim); margin-top: 3px; min-height: 12px; } .hudm .lgscr .fld.bad .lghint { color: #ff8a7a; } .hudm .lgscr .fld.good .lghint { color: #6fe08a; }
.hudm .lgscr .caps { font-size: 11px; color: #f2c94c; }
.hudm .lgscr .line { display: flex; justify-content: space-between; align-items: center; font-size: 12px; } .hudm .lgscr .line a { color: var(--ac); cursor: pointer; } .hudm .lgscr .line a:hover { text-decoration: underline; }
.hudm .lgscr .line label { display: flex; align-items: center; gap: 6px; cursor: pointer; } .hudm .lgscr input[type=checkbox] { accent-color: var(--ac); margin: 0; width: auto; height: auto; }
.hudm .lgscr .go { position: relative; height: 44px; display: flex; align-items: center; justify-content: center; cursor: pointer; overflow: hidden; border-radius: calc(var(--rad) * .9); font-size: 15px; font-weight: 800; letter-spacing: .14em; color: #fff;
  border: 1px solid var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 55%, var(--pb)), color-mix(in srgb, var(--ac) 25%, var(--pb))); box-shadow: 0 0 0 1px #000, 0 0 20px var(--glow); }
.hudm .lgscr .go:hover { filter: brightness(1.15); } .hudm .lgscr .go.busy { pointer-events: none; }
.hudm .lgscr .go.busy::after { content: ""; position: absolute; left: -40%; top: 0; bottom: 0; width: 40%; background: linear-gradient(90deg, transparent, rgba(255,255,255,.35), transparent); animation: hudm-lgbusy 1s linear infinite; }
@keyframes hudm-lgbusy { to { left: 100%; } } .hudm .lgscr .go.dis { opacity: .4; pointer-events: none; }
.hudm .lgscr .alt { height: 36px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: calc(var(--rad) * .9); font-size: 13px; font-weight: 600; border: 1px solid var(--fr); background: rgba(3,5,10,.5); }
.hudm .lgscr .alt:hover { border-color: var(--ac); color: #fff; }
.hudm .lgscr .err { padding: 9px 11px; border-radius: calc(var(--rad) * .8); font-size: 12px; line-height: 1.45; color: #ffd0c8; border: 1px solid #c44; background: rgba(160,30,30,.3); white-space: pre-line; animation: hudm-lgshake .35s; }
.hudm .lgscr .err.ok { color: #c8ffd4; border-color: #4fbf6a; background: rgba(30,120,50,.3); animation: none; } .hudm .lgscr .err b { color: #fff; }
@keyframes hudm-lgshake { 25%, 75% { transform: translateX(-5px); } 50% { transform: translateX(5px); } }
.hudm .lgscr .steps { display: flex; gap: 6px; } .hudm .lgscr .steps div { flex: 1; height: 4px; border-radius: 2px; background: rgba(255,255,255,.12); } .hudm .lgscr .steps div.on { background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .lgscr .str { display: flex; gap: 3px; margin-top: 4px; } .hudm .lgscr .str i { flex: 1; height: 3px; border-radius: 2px; background: rgba(255,255,255,.12); }
.hudm .lgscr .str i.on1 { background: #e05555; } .hudm .lgscr .str i.on2 { background: #f2c94c; } .hudm .lgscr .str i.on3 { background: #4fbf6a; }
.hudm .lgscr .two { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; } .hudm .lgscr .two .alt, .hudm .lgscr .two .go { height: 40px; }
.hudm .lgscr .srv { right: 60px; top: 70px; width: 300px; padding: 16px 18px; display: flex; flex-direction: column; gap: 10px; }
.hudm .lgscr .srv h4, .hudm .lgscr .news h4 { margin: 0; font-size: 10px; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); }
.hudm .lgscr .sv { display: flex; align-items: center; gap: 12px; } .hudm .lgscr .sv .dot { width: 10px; height: 10px; border-radius: 50%; background: #4fd36a; box-shadow: 0 0 10px #4fd36a; animation: hudm-lgpulse 2s ease-in-out infinite; }
@keyframes hudm-lgpulse { 50% { box-shadow: 0 0 2px #4fd36a; } } .hudm .lgscr .sv b { font-size: 16px; color: #fff; } .hudm .lgscr .sv small { display: block; font-size: 11px; color: var(--dim); }
.hudm .lgscr .ping { margin-left: auto; display: flex; align-items: flex-end; gap: 2px; height: 16px; } .hudm .lgscr .ping i { width: 4px; border-radius: 1px; background: rgba(255,255,255,.15); } .hudm .lgscr .ping i.on { background: #4fd36a; }
.hudm .lgscr .kv { display: flex; justify-content: space-between; font-size: 12px; } .hudm .lgscr .kv span { color: var(--dim); } .hudm .lgscr .kv b { color: #fff; font-variant-numeric: tabular-nums; } .hudm .lgscr .kv b.ok { color: #6fe08a; }
.hudm .lgscr .news { right: 60px; top: 300px; width: 300px; padding: 16px 18px; display: flex; flex-direction: column; gap: 10px; }
.hudm .lgscr .nw { padding: 8px 10px; border-radius: calc(var(--rad) * .8); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); background: rgba(3,5,10,.35); cursor: default; }
.hudm .lgscr .nw:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); } .hudm .lgscr .nw em { font-style: normal; font-size: 9px; font-weight: 800; letter-spacing: .1em; padding: 1px 6px; border-radius: 6px; color: #000; background: var(--ac); }
.hudm .lgscr .nw b { display: block; margin: 4px 0 2px; color: #fff; } .hudm .lgscr .nw small { color: var(--dim); font-size: 11px; line-height: 1.4; }
.hudm .lgscr .foot { position: absolute; left: 0; right: 0; bottom: 0; height: 40px; display: flex; align-items: center; gap: 14px; padding: 0 24px; font-size: 11px; color: var(--dim); background: linear-gradient(0deg, rgba(0,0,0,.7), transparent); }
.hudm .lgscr .foot .sp { flex: 1; } .hudm .lgscr .foot a { cursor: pointer; color: var(--tx); } .hudm .lgscr .foot a:hover { color: var(--ac); }
.hudm .lgscr .foot .lang { display: flex; border: 1px solid var(--fr); border-radius: 10px; overflow: hidden; } .hudm .lgscr .foot .lang a { padding: 2px 9px; } .hudm .lgscr .foot .lang a.on { color: #fff; background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .lgscr.leave .card, .hudm .lgscr.leave .srv, .hudm .lgscr.leave .news, .hudm .lgscr.leave .logo { transition: opacity .5s, transform .5s; opacity: 0; transform: translateY(-20px); }
.hudm .lgscr.opening .shade, .hudm .lgscr.opening .fog, .hudm .lgscr.opening .foot { transition: opacity .6s; opacity: 0; }
.hudm .lgscr .white { position: absolute; inset: 0; z-index: 5; background: #000; opacity: 0; transition: opacity .7s; pointer-events: none; } .hudm .lgscr .white.on { opacity: 1; }
.hudm .lgscr .welcome { position: absolute; left: 50%; top: 50%; translate: -50% -50%; z-index: 6; font-size: 26px; font-weight: 800; letter-spacing: .2em; color: #fff; text-shadow: 0 0 20px var(--glow); opacity: 0; transition: opacity .4s; }
.hudm .lgscr .welcome.on { opacity: 1; }
`;
const eye = (open: boolean) => `<svg viewBox="0 0 24 24"><path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/>${open ? '<circle cx="12" cy="12" r="3"/>' : '<path d="M4 4l16 16"/>'}</svg>`;

export class LoginMock {
  readonly el: HTMLElement;
  /** Called when the login finishes and the screen has faded out. */
  onDone: () => void = () => {};
  private accounts = new Map<string, Acct>([["gmryu", { pw: "crystal1", wrong: 0 }], ["banned", { pw: "banned1", wrong: 0, banned: { reason: "Botting (GM decision)", until: Date.now() + 3 * 864e5 } }]]);
  private view: View = "login";
  private step = 0;
  private form: Record<string, string> = {};
  private msg: [string, string] = ["", ""];
  private remember = true;
  private showPw = false;
  private busy = false;
  private lang = "English";
  private frames: string[] = [];
  private frame = 0;
  private opening = false;
  private bg!: HTMLElement;
  private players = 128;
  private ping = 24;
  private timer = 0;
  /** Pending login / door timeouts: cleared by show() and hide() (Quit), so a closed screen never moves on by itself. */
  private later: number[] = [];
  private after(ms: number, fn: () => void): void { this.later.push(window.setTimeout(fn, ms)); }
  private entering = false;

  constructor() {
    injectCss("hudm-login-css", CSS);
    this.el = h("div", "lgscr");
    this.el.hidden = true;
    void Promise.all(Array.from({ length: 19 }, (_, i) => libPicture(Lib.ChrSel, i))).then((ps) => { this.frames = ps.filter((p) => !!p).map((p) => p!.url); if (!this.el.hidden && !this.busy) this.render(); });
  }

  /** Shows the login screen (lab Simulate button). */
  show(): void {
    for (const t of this.later) window.clearTimeout(t); this.later = [];
    this.view = "login"; this.msg = ["", ""]; this.busy = false; this.entering = false; this.form = { id: this.remember ? "GMRyu" : "" };
    this.el.hidden = false; this.el.classList.remove("leave", "opening"); this.opening = false; this.frame = 0;
    this.render();
    window.clearInterval(this.timer);
    this.timer = window.setInterval(() => this.tick(), 100);
  }
  hide(): void { this.el.hidden = true; window.clearInterval(this.timer); for (const t of this.later) window.clearTimeout(t); this.later = []; }

  private n = 0;
  private showFrame(): void { this.bg.querySelectorAll("img").forEach((im, i) => im.classList.toggle("on", i === this.frame)); }
  private tick(): void {
    if (this.el.hidden) return;
    // The door only moves while opening (ChrSel 0-18 once, 100 ms a frame); otherwise it stays closed on frame 0.
    if (this.opening && this.frame < this.frames.length - 1) { this.frame++; this.showFrame(); }
    if (++this.n % 20 === 0) {
      this.players = Math.max(80, this.players + Math.round((Math.random() - 0.45) * 4)); this.ping = Math.max(8, Math.min(140, this.ping + Math.round((Math.random() - 0.5) * 10)));
      const p = this.el.querySelector(".pl"); if (p) p.textContent = String(this.players);
      const g = this.el.querySelector(".pg"); if (g) g.textContent = `${this.ping} ms`;
      this.el.querySelectorAll(".ping i").forEach((e, i) => e.classList.toggle("on", i < (this.ping < 40 ? 4 : this.ping < 80 ? 3 : this.ping < 120 ? 2 : 1)));
    }
  }

  private render(): void {
    const bg = h("div", "bg"); this.bg = bg;
    for (const u of this.frames) { const im = h("img"); im.src = u; im.alt = ""; bg.append(im); }
    window.setTimeout(() => this.showFrame());
    const embers: HTMLElement[] = [];
    for (let i = 0; i < 18; i++) { const e = h("div", "ember"); Object.assign(e.style, { left: Math.random() * 100 + "%", animationDuration: 7 + Math.random() * 8 + "s", animationDelay: -Math.random() * 12 + "s" }); e.style.setProperty("--sx", Math.random() * 120 - 60 + "px"); embers.push(e); }
    const logo = h("div", "logo", h("b", "", "LEGEND OF MIR"), h("span", "", "CRYSTAL"));
    const card = h("div", "glass card", ...(this.view === "login" ? this.loginCard() : this.view === "create" ? this.createCard() : this.changeCard()));
    const bars = h("div", "ping"); for (let i = 0; i < 4; i++) { const b = h("i", i < 4 ? "on" : ""); b.style.height = 4 + i * 4 + "px"; bars.append(b); }
    const srv = h("div", "glass srv", h("h4", "", "Server"), h("div", "sv", h("span", "dot"), h("div", "", h("b", "", "Crystal"), h("small", "", "Online · 127.0.0.1:7100")), bars),
      h("div", "kv", h("span", "", "Players online"), h("b", "pl", String(this.players))), h("div", "kv", h("span", "", "Latency"), h("b", "pg", `${this.ping} ms`)),
      h("div", "kv", h("span", "", "Client version"), h("b", "ok", "✓ up to date")));
    const news = h("div", "glass news", h("h4", "", "News"));
    for (const [tag, t, s] of NEWS) news.append(h("div", "nw", h("em", "", tag.toUpperCase()), h("b", "", t), h("small", "", s)));
    const lang = h("div", "lang"); for (const l of ["English", "中文"]) { const a = h("a", this.lang === l ? "on" : "", l); a.addEventListener("click", () => { this.lang = l; this.render(); }); lang.append(a); }
    const quit = h("a", "", "Quit"); quit.addEventListener("click", () => this.hide());
    const foot = h("div", "foot", h("span", "", "Crystal · web client mock"), h("span", "sp"), lang, quit);
    this.el.replaceChildren(bg, h("div", "shade"), h("div", "fog"), ...embers, logo, card, srv, news, foot, h("div", "white"), h("div", "welcome", "WELCOME BACK"));
    const first = this.el.querySelector<HTMLInputElement>(".card input:not([type=checkbox])");
    if (first && !first.value) window.setTimeout(() => first.focus());
  }

  // — Fields —
  private field(key: string, label: string, opts: { type?: string; check?: (v: string) => string; max?: number; placeholder?: string } = {}): HTMLElement {
    const inp = h("input"); inp.type = opts.type === "password" && !this.showPw ? "password" : opts.type === "password" ? "text" : opts.type ?? "text";
    inp.value = this.form[key] ?? ""; if (opts.max) inp.maxLength = opts.max; inp.placeholder = opts.placeholder ?? ""; inp.autocomplete = "off"; inp.spellcheck = false;
    const fld = h("div", "fld", h("label", "", label), inp);
    const hint = h("div", "lghint");
    const validate = () => { const v = inp.value; if (!opts.check || !v) { fld.classList.remove("bad", "good"); hint.textContent = ""; return; } const w = opts.check(v); fld.classList.toggle("bad", !!w); fld.classList.toggle("good", !w); hint.textContent = w || "✓"; };
    inp.addEventListener("input", () => { this.form[key] = inp.value; validate(); if (key === "npw") this.strength(); });
    inp.addEventListener("keydown", (e) => {
      e.stopPropagation();
      const caps = this.el.querySelector<HTMLElement>(".caps"); if (caps && opts.type === "password") caps.hidden = !e.getModifierState("CapsLock");
      if (e.key === "Enter") { e.preventDefault(); (this.el.querySelector<HTMLElement>(".card .go"))?.click(); }
    });
    if (opts.type === "password") { const ey = h("div", "eye"); ey.innerHTML = eye(this.showPw); ey.title = this.showPw ? "Hide" : "Show"; ey.addEventListener("click", () => { this.showPw = !this.showPw; this.render(); }); fld.append(ey); }
    if (opts.check) fld.append(hint);
    validate();
    return fld;
  }
  private checkId = (v: string) => RX_ID.test(v) ? "" : `${ID_MIN}-${ID_MAX} letters or digits`;
  private checkPw = (v: string) => RX_PW.test(v) ? "" : `${PW_MIN}-${PW_MAX} letters or digits`;
  private strength(): void {
    const v = this.form.npw ?? "", s = !v ? 0 : RX_PW.test(v) ? (/[0-9]/.test(v) && /[A-Za-z]/.test(v) ? (v.length >= 10 ? 3 : 2) : 1) : 1;
    this.el.querySelectorAll(".str i").forEach((e, i) => { e.className = i < s ? "on" + s : ""; });
  }
  private error(): HTMLElement[] { return this.msg[0] ? [h("div", "err" + (this.msg[1] === "ok" ? " ok" : ""), this.msg[0])] : []; }

  // — Login —
  private loginCard(): HTMLElement[] {
    const go = h("div", "go" + (this.busy ? " busy" : ""), this.busy ? this.busyText : "LOG IN");
    go.addEventListener("click", () => this.login());
    const rem = h("input"); rem.type = "checkbox"; rem.checked = this.remember; rem.addEventListener("change", () => { this.remember = rem.checked; });
    const forgot = h("a", "", "Change password"); forgot.addEventListener("click", () => { this.view = "change"; this.msg = ["", ""]; this.render(); });
    const create = h("div", "alt", "Create a new account"); create.addEventListener("click", () => { this.view = "create"; this.step = 0; this.form = {}; this.msg = ["", ""]; this.render(); });
    const caps = h("div", "caps", "⇪ Caps Lock is on"); caps.hidden = true;
    return [h("h2", "", "Welcome back", h("small", "", "Log in to your Crystal account")), ...this.error(), this.field("id", "Account ID", { max: ID_MAX }), this.field("pw", "Password", { type: "password", max: PW_MAX }), caps,
      h("div", "line", h("label", "", rem, "Remember account"), forgot), go, create];
  }
  private busyText = "";
  /** Envir.Login: no account, banned (with expiry), wrong password (the 6th bans for 2 minutes), else in. */
  private login(): void {
    // Enter in a field clicks the button with .click(), which .go.busy's pointer-events: none does not stop.
    if (this.busy || this.entering) return;
    const id = (this.form.id ?? "").trim(), pw = this.form.pw ?? "";
    if (!RX_ID.test(id)) { this.msg = [`The account ID must be ${ID_MIN}-${ID_MAX} letters or digits.`, ""]; this.render(); return; }
    if (!RX_PW.test(pw)) { this.msg = [`The password must be ${PW_MIN}-${PW_MAX} letters or digits.`, ""]; this.render(); return; }
    this.busy = true; this.msg = ["", ""];
    const stages = ["CONNECTING…", "CHECKING VERSION…", "LOGGING IN…"];
    stages.forEach((s, i) => this.after(i * 450, () => { this.busyText = s; const g = this.el.querySelector(".card .go"); if (g) g.textContent = s; }));
    this.busyText = stages[0]!; this.render();
    this.after(1400, () => {
      this.busy = false;
      const a = this.accounts.get(id.toLowerCase());
      if (!a) { this.msg = ["That account ID does not exist.", ""]; this.render(); return; }
      if (a.banned && a.banned.until > Date.now()) { this.msg = [this.banText(a), ""]; this.render(); return; }
      if (a.pw !== pw) {
        if (a.wrong++ >= LOCK_TRIES) { a.banned = { reason: "Too many wrong login attempts", until: Date.now() + LOCK_MIN * 60000 }; a.wrong = 0; this.msg = [this.banText(a), ""]; }
        else this.msg = [`Incorrect password.\n${LOCK_TRIES + 1 - a.wrong} more tr${LOCK_TRIES + 1 - a.wrong === 1 ? "y" : "ies"} before the account is locked for ${LOCK_MIN} minutes.`, ""];
        this.render(); return;
      }
      a.wrong = 0;
      this.enter(id);
    });
  }
  private banText(a: Acct): string {
    const s = Math.max(0, Math.round((a.banned!.until - Date.now()) / 1000));
    return `This account is banned.\nReason: ${a.banned!.reason}\nExpires: ${new Date(a.banned!.until).toLocaleString("en-GB")}\nTime left: ${Math.floor(s / 3600)}h ${Math.floor(s / 60) % 60}m ${s % 60}s`;
  }
  /** The desktop's door: the UI fades, ChrSel plays 0-18 while the view pushes in, then a welcome and a fade to the HUD. */
  private enter(id: string): void {
    this.entering = true;
    this.el.classList.add("leave");
    this.after(400, () => { this.el.classList.add("opening"); this.frame = 0; this.opening = true; });
    this.after(2000, () => { const w = this.el.querySelector(".welcome")!; w.textContent = `WELCOME BACK, ${id.toUpperCase()}`; w.classList.add("on"); });
    this.after(2700, () => this.el.querySelector(".white")?.classList.add("on"));
    this.after(3500, () => { this.hide(); this.opening = false; this.frame = 0; this.el.classList.remove("opening"); this.onDone(); });
  }

  // — Create account (NewAccountDialog) —
  private createCard(): HTMLElement[] {
    const steps = h("div", "steps"); for (let i = 0; i < 3; i++) steps.append(h("div", i <= this.step ? "on" : ""));
    const titles = ["Account", "About you", "Security"];
    const body: HTMLElement[] = [];
    if (this.step === 0) {
      body.push(this.field("nid", "Account ID", { max: ID_MAX, check: (v) => this.checkId(v) || (this.accounts.has(v.toLowerCase()) ? "That account ID is taken" : "") }),
        this.field("npw", "Password", { type: "password", max: PW_MAX, check: this.checkPw }), h("div", "str", h("i"), h("i"), h("i")),
        this.field("npw2", "Confirm password", { type: "password", max: PW_MAX, check: (v) => v === this.form.npw ? "" : "Passwords don't match" }));
      window.setTimeout(() => this.strength());
    } else if (this.step === 1) {
      body.push(this.field("uname", "User name", { max: 20, check: (v) => v.trim().length >= 2 ? "" : "At least 2 characters" }),
        this.field("birth", "Birth date", { type: "date", check: (v) => v ? "" : "Pick a date" }),
        this.field("email", "Email", { max: 50, check: (v) => /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(v) ? "" : "Not an email address" }));
    } else {
      const sel = h("select"); for (const q of QUESTIONS) { const o = h("option", "", q); if (this.form.q === q) o.selected = true; sel.append(o); }
      this.form.q ??= QUESTIONS[0]!; sel.addEventListener("change", () => { this.form.q = sel.value; });
      sel.addEventListener("keydown", (e) => e.stopPropagation());
      body.push(h("div", "fld", h("label", "", "Secret question"), sel), this.field("ans", "Secret answer", { max: 30, check: (v) => v.trim().length >= 2 ? "" : "At least 2 characters" }));
    }
    const ok = this.stepOk();
    const back = h("div", "alt", this.step ? "Back" : "Cancel"); back.addEventListener("click", () => { if (this.step) this.step--; else { this.view = "login"; this.form = {}; } this.msg = ["", ""]; this.render(); });
    const next = h("div", "go" + (ok ? "" : " dis"), this.step < 2 ? "NEXT" : "CREATE");
    next.addEventListener("click", () => {
      if (!this.stepOk()) { this.render(); return; }
      if (this.step < 2) { this.step++; this.render(); return; }
      this.accounts.set(this.form.nid!.toLowerCase(), { pw: this.form.npw!, wrong: 0 });
      const id = this.form.nid!; this.view = "login"; this.form = { id }; this.msg = [`Account ${id} created. You can log in now.`, "ok"]; this.render();
    });
    // Re-check the button as you type.
    window.setTimeout(() => this.el.querySelectorAll(".card input, .card select").forEach((i) => i.addEventListener("input", () => this.el.querySelector(".card .go")?.classList.toggle("dis", !this.stepOk()))));
    return [h("h2", "", "Create account", h("small", "", `Step ${this.step + 1} of 3 · ${titles[this.step]}`)), steps, ...this.error(), ...body, h("div", "two", back, next)];
  }
  private stepOk(): boolean {
    const f = this.form;
    if (this.step === 0) return !this.checkId(f.nid ?? "") && !this.accounts.has((f.nid ?? "").toLowerCase()) && !this.checkPw(f.npw ?? "") && f.npw === f.npw2;
    if (this.step === 1) return (f.uname ?? "").trim().length >= 2 && !!f.birth && /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(f.email ?? "");
    return (f.ans ?? "").trim().length >= 2;
  }

  // — Change password (ChangePasswordDialog) —
  private changeCard(): HTMLElement[] {
    const go = h("div", "go", "CHANGE PASSWORD");
    go.addEventListener("click", () => {
      const f = this.form, a = this.accounts.get((f.id ?? "").toLowerCase());
      if (!a) this.msg = ["That account ID does not exist.", ""];
      else if (a.pw !== f.cpw) this.msg = ["The current password is incorrect.", ""];
      else if (this.checkPw(f.npw ?? "")) this.msg = [`The new password must be ${PW_MIN}-${PW_MAX} letters or digits.`, ""];
      else if (f.npw !== f.npw2) this.msg = ["The new passwords don't match.", ""];
      else { a.pw = f.npw!; this.view = "login"; this.form = { id: f.id! }; this.msg = ["Password changed. Log in with the new one.", "ok"]; }
      this.render();
    });
    const back = h("div", "alt", "Back to log in"); back.addEventListener("click", () => { this.view = "login"; this.msg = ["", ""]; this.render(); });
    return [h("h2", "", "Change password", h("small", "", "You need your current password")), ...this.error(), this.field("id", "Account ID", { max: ID_MAX, check: this.checkId }),
      this.field("cpw", "Current password", { type: "password", max: PW_MAX }), this.field("npw", "New password", { type: "password", max: PW_MAX, check: this.checkPw }), h("div", "str", h("i"), h("i"), h("i")),
      this.field("npw2", "Confirm new password", { type: "password", max: PW_MAX, check: (v) => v === this.form.npw ? "" : "Passwords don't match" }), go, back];
  }
}
