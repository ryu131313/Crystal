// Ported from play/hudSelect.ts: the new UI's character select screen (the desktop SelectScene + NewCharacterDialog),
// wired to the real select flow of src/ui/selectScene.ts.
//  - Roster: S.LoginSuccess / S.LogOutSuccess SelectInfo (up to Globals.MaxCharacterCount, most recently played first,
//    selectScene.ts sortList) with Prguse HeroAvatar + 370 portraits, empty "+ Create a character" slots.
//  - Details: class, level and last played from SelectInfo; guild, location (map title), play time and account gold
//    from S.SelectDetails (accountDetails.ts; rows only once the server sent them).
//  - Preview: the ChrSel class display (16 frames at 250 ms) with the +560 layer drawn additively ('plus-lighter', as
//    the classic CharacterDisplay.AfterDraw DrawBlend). Bounds are measured once per display index.
//  - Backdrop: STATIC (the login door's last frame, ChrSel 18, under a theme shade): there is no world before
//    StartGame, and no backdrop-filter over anything (Performance.md T3-1).
//  - Create: C.NewCharacter; Assassin / Archer follow S.ServerRules AllowCreateAssassin / AllowCreateArcher (allowed
//    until the first snapshot: the server still answers Result 3); the name is checked against the server's
//    CharacterReg; S.NewCharacter 0-5 / S.NewCharacterSuccess (the new character goes first, as the classic).
//  - Delete: Yes / No, then type the name (popups), C.DeleteCharacter; S.DeleteCharacter 0 / 1 / Success.
//  - Start: C.StartGame with an entering guard; S.StartGame 0-4 (4 = setPlayingCharacter + onStart), StartGameBanned,
//    StartGameDelay (a live countdown that sends StartGame again when it runs out, like the classic).
//  - "Back to log in" and Exit: C.LogOut + reload (the server has no select-to-login transition). A footer link switches
//    to the classic interface.
// Stripped from the mock: the mock characters and the ME sync, the ALLOW constant, the local "taken" names, the random
// names, local create / delete, the fake start timers / load bar / fade, the class blurbs (the real class descriptions
// from accountCommon.ts are used), the live-world backdrop and the scene-on HUD rule.
// Keys: element-level listeners on the screen's own root (Up / Down pick, Enter starts, Esc leaves creation; repeats
// ignored); no document / window listener. Works without a game UiRuntime: inside an account theme root ('.hudm'
// ancestor) or, if there is none, under a small theme root of its own.

import type { ServerPacket, SelectInfo } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { MirClass, MirGender } from "../../../protocol/enums.generated.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { heroAvatar } from "../../heroPanels.ts";
import { Globals, Text, format, banText, chrSelIndex, wallMs, isMinValue, formatDateTime, type SceneHost } from "../../accountCommon.ts";
import { warmCharacter, setPlayingCharacter } from "../../../game/mapFiles.ts";
import { setSetting } from "../../../game/settings.ts";
import type { AccountDetailsStore } from "../../../game/accountDetails.ts";
import type { ServerRulesStore } from "../../../game/serverRules.ts";
import { h, injectCss, setText, setStyle, restartAnim, windowInterval, asButton, typingField, type Switch } from "../core/dom.ts";
import { installBaseStyles } from "../core/styles.ts";
import { applyTheme } from "../core/theme.ts";
import { accountThemeSettings } from "./accountRoot.ts";
import { Popups } from "../core/popups.ts";
import { fmt, fmtDuration, className, classTag, CLASSES } from "../core/format.ts";
import { svg } from "../core/icons.ts";

type P<T extends ServerPacket["type"]> = Extract<ServerPacket, { type: T }>;

/** Envir.CharacterReg (Server/MirEnvir/Envir.cs): 3-15 letters, digits, _ or CJK. */
const NAME_RX = new RegExp(`^[\\u4e00-\\u9fa5_A-Za-z0-9]{${Globals.MinCharacterNameLength},${Globals.MaxCharacterNameLength}}$`);
const NAME_HINT = `${Globals.MinCharacterNameLength}-${Globals.MaxCharacterNameLength} letters, digits, _ or Chinese characters`;
/** The login door's last frame (loginScene.ts plays ChrSel 0-18): the select backdrop. */
const BACKDROP_INDEX = 18;
const PREVIEW_FRAMES = 16, PREVIEW_MS = 250, GLOW_OFFSET = 560;

/** What the select screen reads besides SceneHost: the per-connection stores Screens feeds, and the interface switch. */
export interface SelectSceneDeps {
  /** S.SelectDetails (guild, map title, play time, account gold). Without it those rows stay hidden. */
  details?: AccountDetailsStore;
  /** S.ServerRules (AllowCreateAssassin / AllowCreateArcher). Without it every class is offered (the server decides). */
  rules?: ServerRulesStore;
  /** Footer "Classic interface": called after the setting is saved, so Screens can rebuild the classic select screen
   *  over the same connection. Without it the page logs out and reloads. */
  onClassic?: () => void;
}

/** The interface Screens uses for a select scene (the classic SelectScene has the same shape). */
export interface SelectUi { handle(p: ServerPacket): boolean; dispose(): void }

/** The class description the desktop NewCharacterDialog shows (ClientTextKeys, accountCommon.ts). */
function classDescription(cls: number): string {
  switch (cls) {
    case MirClass.Wizard: return Text.WizardsDescription;
    case MirClass.Taoist: return Text.TaoistsDescription;
    case MirClass.Assassin: return Text.AssassinsDescription;
    case MirClass.Archer: return Text.ArchersDescription;
    default: return Text.WarriorsDescription;
  }
}


const CSS = `
.hudm .acsel { position: absolute; left: 0; top: 0; width: 100%; height: 100%; z-index: 40; pointer-events: auto; overflow: hidden; outline: none; color: var(--tx); font-size: 13px; background: #000; }
.hudm .acsel .bg { position: absolute; inset: 0; background: #000 center / cover no-repeat; image-rendering: pixelated; opacity: .45; }
.hudm .acsel .shade { position: absolute; inset: 0; background: radial-gradient(ellipse 40% 60% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%),
  linear-gradient(90deg, rgba(0,0,0,.75), rgba(0,0,0,.2) 30%, rgba(0,0,0,.2) 70%, rgba(0,0,0,.75)),
  linear-gradient(180deg, color-mix(in srgb, var(--pb) 40%, transparent), transparent 40%, color-mix(in srgb, var(--pb) 60%, transparent)); }
/* Light rays: a static gradient whose opacity alone pulses (no filter, no transform animation: Performance.md). */
.hudm .acsel .rays { position: absolute; left: 50%; top: -10%; width: 560px; height: 90%; translate: -50% 0; opacity: .7;
  background: conic-gradient(from 180deg at 50% 0%, transparent 156deg, color-mix(in srgb, var(--ac) 12%, transparent) 168deg, color-mix(in srgb, var(--ac) 18%, transparent) 172deg,
    transparent 182deg, color-mix(in srgb, var(--ac) 10%, transparent) 190deg, transparent 204deg);
  animation: hudm-acrays 8s ease-in-out infinite alternate; }
@keyframes hudm-acrays { to { opacity: .35; } }
.hudm .acsel .glass { position: absolute; border-radius: calc(var(--rad) * 1.4); background: color-mix(in srgb, var(--pb) 88%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 20px 50px rgba(0,0,0,.6); }
.hudm .acsel .top { position: absolute; left: 0; right: 0; top: 0; height: 56px; display: flex; align-items: center; gap: 14px; padding: 0 28px; background: linear-gradient(180deg, rgba(0,0,0,.7), transparent); z-index: 3; }
.hudm .acsel .top b { font: 900 22px Georgia, serif; letter-spacing: .1em; color: transparent; background: linear-gradient(100deg, #b8902f, #ffe9a8 40%, #b8902f); -webkit-background-clip: text; background-clip: text; }
.hudm .acsel .top span { font-size: 11px; letter-spacing: .3em; color: var(--dim); } .hudm .acsel .top .sp { flex: 1; }
.hudm .acsel a { cursor: pointer; color: var(--tx); font-size: 12px; text-decoration: none; display: inline-flex; align-items: center; gap: 4px; } .hudm .acsel a:hover { color: var(--ac); }
.hudm .acsel a svg { width: 13px; height: 13px; }
.hudm .acsel .lb { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); } .hudm .acsel .lb b { letter-spacing: 0; color: var(--tx); }
.hudm .acsel .roster { left: 28px; top: 80px; width: 270px; padding: 14px; display: flex; flex-direction: column; gap: 8px; z-index: 2; }
.hudm .acsel .ch { position: relative; display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: rgba(3,5,10,.4); transition: transform .15s, border-color .15s; }
.hudm .acsel .ch:hover { transform: translateX(3px); border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .acsel .ch.on { border-color: var(--ac); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 22%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac), 0 0 14px color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .acsel .ch.lock { cursor: default; }
.hudm .acsel .pt { width: 46px; height: 46px; flex: none; border-radius: 50%; border: 2px solid var(--fr); background: #05070c center / cover no-repeat; image-rendering: pixelated; }
.hudm .acsel .ch.on .pt { border-color: var(--ac); box-shadow: 0 0 10px var(--glow); }
.hudm .acsel .ch .t { flex: 1; min-width: 0; } .hudm .acsel .ch .t b { display: block; font-size: 15px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .acsel .ch .t small { display: block; font-size: 11px; color: var(--dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .acsel .ch .lv { font-size: 20px; font-weight: 800; color: var(--ac); text-shadow: 0 0 8px var(--glow); } .hudm .acsel .ch .lv small { display: block; font-size: 8px; letter-spacing: .14em; color: var(--dim); text-align: center; }
.hudm .acsel .ch.empty { justify-content: center; border-style: dashed; color: var(--dim); min-height: 46px; } .hudm .acsel .ch.empty:hover { color: #fff; }
.hudm .acsel .ch.empty.dis { opacity: .45; cursor: not-allowed; transform: none; }
.hudm .acsel .stage { position: absolute; left: 50%; top: 70px; width: 420px; height: 520px; translate: -50% 0; z-index: 1; pointer-events: none; }
.hudm .acsel .stage::after { content: ""; position: absolute; left: 50%; bottom: 40px; width: 260px; height: 44px; translate: -50% 0; border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%); }
.hudm .acsel .spr { position: absolute; left: 50%; bottom: 52px; transform-origin: 50% 100%; z-index: 1; }
.hudm .acsel .spr img { position: absolute; image-rendering: pixelated; } .hudm .acsel .spr img.glow { mix-blend-mode: plus-lighter; }
.hudm .acsel .spr.in { animation: hudm-acin .5s ease-out; } @keyframes hudm-acin { from { opacity: 0; } }
.hudm .acsel .plate { position: absolute; left: 50%; bottom: 0; translate: -50% 0; z-index: 2; text-align: center; white-space: nowrap; }
.hudm .acsel .plate b { display: block; font-size: 30px; font-weight: 800; color: #fff; text-shadow: 0 0 16px var(--glow), 0 2px 4px #000; letter-spacing: .04em; }
.hudm .acsel .plate span { font-size: 12px; letter-spacing: .18em; text-transform: uppercase; color: var(--ac); }
.hudm .acsel .info { right: 28px; top: 80px; width: 270px; padding: 16px; display: flex; flex-direction: column; gap: 9px; z-index: 2; max-height: calc(100% - 180px); overflow-y: auto; }
.hudm .acsel .kv { display: flex; justify-content: space-between; gap: 8px; font-size: 12px; padding: 3px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .acsel .kv span { color: var(--dim); flex: none; } .hudm .acsel .kv b { color: #fff; text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .acsel .kv b.gold { color: var(--gold); }
.hudm .acsel .start { position: absolute; left: 50%; bottom: 46px; translate: -50% 0; width: 300px; height: 52px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: 26px; z-index: 3;
  font-size: 17px; font-weight: 900; letter-spacing: .2em; color: #fff; border: 1px solid var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 60%, var(--pb)), color-mix(in srgb, var(--ac) 25%, var(--pb)));
  box-shadow: 0 0 0 1px #000, 0 0 12px var(--glow); --halo: 0 0 30px var(--glow); --halo-t: 2.4s; }
.hudm .acsel .start:hover { border-color: #fff; }
.hudm .acsel .start.dis { opacity: .45; cursor: default; } .hudm .acsel .start.dis .halo { display: none; }
.hudm .acsel .start.wait { letter-spacing: .08em; font-size: 14px; }
.hudm .acsel .btns { display: flex; gap: 6px; }
.hudm .acsel .b2 { flex: 1; height: 32px; display: flex; align-items: center; justify-content: center; gap: 5px; cursor: pointer; border-radius: calc(var(--rad) * .9); font-size: 12px; font-weight: 600;
  border: 1px solid var(--fr); background: rgba(3,5,10,.5); user-select: none; } .hudm .acsel .b2 svg { width: 13px; height: 13px; }
.hudm .acsel .b2:hover { border-color: var(--ac); color: #fff; } .hudm .acsel .b2.red:hover { border-color: var(--bad); color: var(--bad-soft); }
.hudm .acsel .b2.dis { opacity: .4; cursor: not-allowed; } .hudm .acsel .b2.dis:hover { border-color: var(--fr); color: inherit; }
.hudm .acsel .b2.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
.hudm .acsel input.nmi { width: 100%; height: 36px; padding: 0 12px; border-radius: calc(var(--rad) * .9); font: inherit; font-size: 14px; color: #fff; outline: none; border: 1px solid var(--fr); background: rgba(3,5,10,.7); }
.hudm .acsel input.nmi:focus { border-color: var(--ac); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ac) 20%, transparent); }
.hudm .acsel .nm.bad input { border-color: var(--bad); } .hudm .acsel .nm.good input { border-color: var(--ok); } .hudm .acsel .nm small { display: block; min-height: 14px; font-size: 10px; margin-top: 3px; color: var(--dim); }
.hudm .acsel .nm.bad small { color: var(--bad-soft); } .hudm .acsel .nm.good small { color: var(--ok); }
.hudm .acsel .classes { left: 28px; top: 80px; width: 270px; padding: 14px; display: flex; flex-direction: column; gap: 7px; z-index: 2; }
.hudm .acsel .cc { display: flex; gap: 10px; align-items: center; padding: 6px 10px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: rgba(3,5,10,.4); }
.hudm .acsel .cc:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); } .hudm .acsel .cc.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .acsel .cc.off { opacity: .35; cursor: not-allowed; }
.hudm .acsel .cc i { width: 34px; height: 34px; flex: none; border-radius: 50%; border: 1px solid var(--fr); background: #05070c center / cover no-repeat; image-rendering: pixelated; }
.hudm .acsel .cc b { display: block; color: #fff; } .hudm .acsel .cc small { font-size: 10px; color: var(--dim); letter-spacing: .1em; text-transform: uppercase; }
.hudm .acsel .about { font-size: 11.5px; line-height: 1.5; color: var(--tx); max-height: 190px; overflow-y: auto; }
.hudm .acsel .gender { display: flex; gap: 6px; } .hudm .acsel .gender div { flex: 1; height: 32px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: calc(var(--rad) * .9); border: 1px solid var(--fr); }
.hudm .acsel .gender div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .acsel .msg { font-size: 12px; padding: 8px 10px; border-radius: var(--rad); line-height: 1.4; white-space: pre-line; }
.hudm .acsel .msg.err { color: #ffd0c8; border: 1px solid var(--bad); background: color-mix(in srgb, var(--bad) 22%, transparent); }
.hudm .acsel .msg.ok { color: #c8ffd4; border: 1px solid var(--ok); background: color-mix(in srgb, var(--ok) 18%, transparent); }
.hudm .acsel .msg.wait { color: #fff3cf; border: 1px solid var(--warn); background: color-mix(in srgb, var(--warn) 16%, transparent); }
.hudm .acsel .tip { position: absolute; left: 50%; bottom: 14px; translate: -50% 0; z-index: 3; font-size: 11px; color: var(--dim); white-space: nowrap; }
.hudm .acsel .foot { position: absolute; right: 28px; bottom: 14px; z-index: 3; font-size: 11px; }
.hudm .acsel .foot a { font-size: 11px; color: var(--dim); }
.hudm .acsel .pops { position: absolute; inset: 0; z-index: 20; pointer-events: none; }
`;

interface Pic { url: string; x: number; y: number; w: number; h: number }
/** One display index's frames, with the bounds of the base frames measured once (Performance.md T3-12). */
interface Frames { pics: Pic[]; glow: (Pic | null)[]; x0: number; y0: number; w: number; h: number; k: number }

const toPic = (p: { url: string; x: number; y: number; width: number; height: number } | null): Pic | null =>
  p ? { url: p.url, x: p.x, y: p.y, w: p.width, h: p.height } : null;

export class ModernSelectScene implements SelectUi {
  private readonly host: SceneHost;
  private readonly onStart: (c: SelectInfo) => void;
  private readonly deps: SelectSceneDeps;
  private readonly characters: SelectInfo[];
  private readonly scope = new AbortController();
  private readonly unsubs: (() => void)[] = [];
  /** Our own theme root when the parent has no '.hudm' ancestor (removed on dispose). */
  private readonly themeRoot: HTMLElement | null = null;
  private readonly root: HTMLElement;
  private readonly panels: HTMLElement;
  private readonly modeLabel: HTMLElement;
  private readonly spr: HTMLElement;
  private readonly base: HTMLImageElement;
  private readonly glow: HTMLImageElement;
  private readonly plateName: HTMLElement;
  private readonly plateLine: HTMLElement;
  private readonly popups: Popups;
  private readonly anim: Switch;
  private readonly delayTimer: Switch;

  private selected = 0;
  private mode: "select" | "create" = "select";
  private msg: [string, "ok" | "err" | ""] = ["", ""];
  /** C.StartGame was sent for this character; further starts wait for the answer. */
  private entering: SelectInfo | null = null;
  /** C.NewCharacter / C.DeleteCharacter sent: their buttons stay locked until the answer. */
  private creating = false;
  private deleting = false;
  /** S.StartGameDelay: performance.now() when StartGame is sent again (0 = no wait). */
  private delayUntil = 0;
  private cCls: number = MirClass.Warrior;
  private cGender: number = MirGender.Male;
  private cName = "";
  private cErr = "";
  private frames = new Map<number, Frames | null>();
  private showIdx = -1;
  private frame = 0;
  private startBtn: HTMLElement | null = null;
  private startLabel: HTMLElement | null = null;
  private waitMsg: HTMLElement | null = null;
  private nameInput: HTMLInputElement | null = null;
  /** The character whose map warmCharacter last fetched. */
  private warmed = "";

  constructor(parent: HTMLElement, host: SceneHost, characters: SelectInfo[], onStart: (c: SelectInfo) => void, deps: SelectSceneDeps = {}) {
    this.host = host;
    this.onStart = onStart;
    this.deps = deps;
    this.characters = characters;
    this.sortList();
    const sig = this.scope.signal;

    installBaseStyles();
    injectCss("screen-select", CSS);
    let mount = parent;
    if (!parent.closest(".hudm")) {
      // No account theme root around us: a small one of our own (the theme variables need a '.hudm' ancestor).
      const t = h("div", "hudm");
      t.style.width = "100%";
      t.style.height = "100%";
      applyTheme(t, accountThemeSettings());
      parent.append(t);
      this.themeRoot = t;
      mount = t;
    }

    const bg = h("div", "bg");
    void libPicture(Lib.ChrSel, BACKDROP_INDEX).then((p) => { if (p && !sig.aborted) bg.style.backgroundImage = `url(${p.url})`; });

    const back = asButton(h("a", "", svg("back"), "Back to log in"));
    back.addEventListener("click", () => this.logOut(), { signal: sig });
    const exit = asButton(h("a", "", svg("power"), "Exit"));
    exit.addEventListener("click", () => this.logOut(), { signal: sig });
    this.modeLabel = h("span", "");
    const top = h("div", "top", h("b", "", "LEGEND OF MIR"), this.modeLabel, h("span", "sp"), back, exit);

    this.base = h("img", "base");
    this.glow = h("img", "glow");
    this.base.alt = this.glow.alt = "";
    this.spr = h("div", "spr", this.base, this.glow);
    this.plateName = h("b", "");
    this.plateLine = h("span", "");
    const stage = h("div", "stage", this.spr, h("div", "plate", this.plateName, this.plateLine));

    const classic = asButton(h("a", "", "Switch to the classic interface"));
    classic.addEventListener("click", () => this.switchToClassic(), { signal: sig });

    this.panels = h("div", "");
    this.panels.style.display = "contents";
    const pops = h("div", "pops");
    this.root = h("div", "acsel", bg, h("div", "shade"), h("div", "rays"), top, stage, this.panels, h("div", "foot", classic), pops);
    this.root.tabIndex = -1;
    this.root.setAttribute("role", "region");
    this.root.setAttribute("aria-label", "Character select");
    mount.append(this.root);
    this.popups = new Popups(pops, sig);

    // Keys: on the screen's own root only (there is no game router before StartGame), gone with the element.
    this.root.addEventListener("keydown", (e) => this.key(e), { signal: sig });

    this.anim = windowInterval(() => this.tick(), PREVIEW_MS, sig);
    this.anim.on();
    this.delayTimer = windowInterval(() => this.delayTick(), 100, sig);

    if (deps.details) this.unsubs.push(deps.details.changed.on((what) => { if (what === "select") this.render(); }));
    if (deps.rules) this.unsubs.push(deps.rules.changed.on(() => { if (this.mode === "create") { this.fixClass(); this.render(); } }));

    this.mode = this.characters.length ? "select" : "create";
    this.render();
    this.root.focus({ preventScroll: true });
  }

  dispose(): void {
    this.scope.abort(); // intervals, listeners, popups
    for (const u of this.unsubs.splice(0)) u();
    this.root.remove();
    this.themeRoot?.remove();
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "NewCharacter": this.newCharacter(p); return true;
      case "NewCharacterSuccess": this.newCharacterSuccess(p); return true;
      case "DeleteCharacter": this.deleteCharacterResult(p); return true;
      case "DeleteCharacterSuccess": this.deleteCharacterSuccess(p); return true;
      case "StartGame": this.startGameResult(p); return true;
      case "StartGameBanned":
        this.entering = null;
        this.updateStart();
        void this.popups.confirm({ title: "Banned", text: banText(p.Reason, p.ExpiryDate), kind: "warning" });
        return true;
      case "StartGameDelay": this.startGameDelay(Number(p.Milliseconds)); return true;
      default: return false;
    }
  }

  // — State —

  /** SelectScene.SortList: most recently played first. */
  private sortList(): void {
    this.characters.sort((a, b) => wallMs(b.LastAccess) - wallMs(a.LastAccess));
  }

  private get current(): SelectInfo | undefined { return this.characters[this.selected]; }

  private allowed(cls: number): boolean {
    const r = this.deps.rules?.rules;
    if (!r) return true;
    if (cls === MirClass.Assassin) return r.AllowCreateAssassin;
    if (cls === MirClass.Archer) return r.AllowCreateArcher;
    return true;
  }

  private fixClass(): void {
    if (!this.allowed(this.cCls)) this.cCls = MirClass.Warrior;
  }

  private busy(): boolean { return !!this.entering || this.creating || this.deleting; }

  private pick(i: number): void {
    if (this.busy() || i === this.selected || !this.characters[i]) return;
    this.cancelDelay();
    this.selected = i;
    this.msg = ["", ""];
    this.render();
  }

  // — Rendering —

  private render(): void {
    const c = this.current;
    setText(this.modeLabel, this.mode === "create" ? "CREATE A CHARACTER" : "CHOOSE YOUR CHARACTER");
    if (this.mode === "create") {
      setText(this.plateName, this.cName || "New character");
      setText(this.plateLine, `${this.cGender === MirGender.Female ? "Female" : "Male"} ${className(this.cCls)}`);
      this.setPreview(chrSelIndex(this.cCls, this.cGender));
      this.panels.replaceChildren(this.classPanel(), this.createPanel());
      this.startBtn = this.startLabel = this.waitMsg = null;
    } else {
      if (c) {
        if (this.warmed !== c.Name) { this.warmed = c.Name; warmCharacter(c.Name); } // the map + tile libraries, once per pick
        const guild = this.deps.details?.character(c.Index)?.GuildName ?? "";
        setText(this.plateName, c.Name);
        setText(this.plateLine, `Level ${c.Level} ${className(c.Class)}${guild ? " · " + guild : ""}`);
        this.setPreview(chrSelIndex(c.Class, c.Gender));
      } else {
        setText(this.plateName, "");
        setText(this.plateLine, "");
        this.setPreview(-1);
      }
      this.nameInput = null;
      this.panels.replaceChildren(this.roster(), this.info(), this.startButton(), h("div", "tip", "↑ ↓ pick a character · Enter to start"));
      this.updateStart();
    }
    // A re-render can remove the focused element: keep the keys on this screen.
    const a = document.activeElement;
    if (!a || a === document.body || !this.root.contains(a)) this.root.focus({ preventScroll: true });
  }

  private portrait(el: HTMLElement, cls: number, gender: number): void {
    const sig = this.scope.signal;
    void libPicture(Lib.Prguse, heroAvatar(cls, gender) + 370).then((p) => { if (p && !sig.aborted) el.style.backgroundImage = `url(${p.url})`; });
  }

  private roster(): HTMLElement {
    const max = Globals.MaxCharacterCount;
    const r = h("div", "glass roster", h("div", "lb", "Characters", h("b", "", `${Math.min(this.characters.length, max)} / ${max}`)));
    this.characters.slice(0, max).forEach((c, i) => {
      const pt = h("div", "pt");
      this.portrait(pt, c.Class, c.Gender);
      const map = this.deps.details?.character(c.Index)?.MapTitle ?? "";
      const e = asButton(h("div", "ch" + (i === this.selected ? " on" : "") + (this.busy() ? " lock" : ""), pt,
        h("div", "t", h("b", "", c.Name), h("small", "", map ? `${className(c.Class)} · ${map}` : className(c.Class))),
        h("div", "lv", String(c.Level), h("small", "", "LEVEL"))), c.Name);
      e.addEventListener("click", () => this.pick(i));
      e.addEventListener("dblclick", () => { if (i === this.selected) this.startGame(); });
      r.append(e);
    });
    for (let i = this.characters.length; i < max; i++) {
      const e = asButton(h("div", "ch empty" + (this.busy() ? " dis" : ""), "+ Create a character"));
      e.addEventListener("click", () => this.newChar());
      r.append(e);
    }
    return r;
  }

  private info(): HTMLElement {
    const c = this.current;
    const p = h("div", "glass info", h("div", "lb", "Details"));
    const full = this.characters.length >= Globals.MaxCharacterCount;
    const create = asButton(h("div", "b2" + (full || this.busy() ? " dis" : ""), svg("plus"), "New character"));
    if (full) create.title = format(Text.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount);
    create.addEventListener("click", () => this.newChar());
    if (!c) {
      p.append(h("div", "about", "No characters yet. Create one to begin."), h("div", "btns", create));
    } else {
      const d = this.deps.details?.character(c.Index);
      const rows: [string, string, string?][] = [
        ["Class", `${c.Gender === MirGender.Female ? "Female" : "Male"} ${className(c.Class)}`],
        ["Level", String(c.Level)],
      ];
      if (d) rows.push(["Guild", d.GuildName || "—"], ["Location", d.MapTitle || "—"]);
      rows.push(["Last played", isMinValue(c.LastAccess) ? Text.Never : formatDateTime(wallMs(c.LastAccess))]);
      if (d) rows.push(["Played", fmtDuration(Number(d.PlayTime))]);
      if (this.deps.details?.characters.size) rows.push(["Account gold", fmt(this.deps.details.accountGold), "gold"]);
      for (const [k, v, cls] of rows) p.append(h("div", "kv", h("span", "", k), h("b", cls ?? "", v)));
      const del = asButton(h("div", "b2 red" + (this.busy() ? " dis" : ""), svg("trash"), this.deleting ? "Deleting…" : "Delete"));
      del.addEventListener("click", () => this.deleteCharacter());
      p.append(h("div", "btns", create, del));
    }
    this.waitMsg = h("div", "msg wait");
    this.waitMsg.hidden = true;
    p.append(this.waitMsg);
    if (this.msg[0]) p.append(h("div", "msg " + this.msg[1], this.msg[0]));
    return p;
  }

  private startButton(): HTMLElement {
    this.startLabel = h("span", "", "START GAME");
    const s = asButton(h("div", "start", h("div", "halo"), this.startLabel), "Start game");
    s.addEventListener("click", () => this.startGame());
    this.startBtn = s;
    return s;
  }

  /** The Start button's state (entering / delay countdown) without rebuilding the panels. */
  private updateStart(): void {
    const s = this.startBtn, l = this.startLabel;
    if (!s || !l) return;
    const left = this.delayUntil ? Math.max(0, Math.ceil((this.delayUntil - performance.now()) / 1000)) : 0;
    s.classList.toggle("dis", !this.current || this.busy() || !!this.delayUntil);
    s.classList.toggle("wait", !!this.delayUntil);
    setText(l, this.entering ? "ENTERING…" : this.delayUntil ? `WAIT ${left}s` : "START GAME");
    if (this.waitMsg) {
      this.waitMsg.hidden = !this.delayUntil;
      if (this.delayUntil) setText(this.waitMsg, format(Text.CannotLoginCharacterSeconds, left));
    }
  }

  // — Preview (ChrSel display + the +560 layer) —

  private setPreview(idx: number): void {
    if (idx === this.showIdx) return;
    this.showIdx = idx;
    this.frame = 0;
    if (idx < 0) { this.spr.hidden = true; return; }
    const f = this.frames.get(idx);
    if (f === undefined) { this.spr.hidden = true; void this.load(idx); return; }
    if (f === null) { this.spr.hidden = true; return; } // loading
    this.layout(f);
  }

  private async load(idx: number): Promise<void> {
    this.frames.set(idx, null);
    const pics = (await Promise.all(Array.from({ length: PREVIEW_FRAMES }, (_, i) => libPicture(Lib.ChrSel, idx + i)))).map(toPic);
    const glow = (await Promise.all(Array.from({ length: PREVIEW_FRAMES }, (_, i) => libPicture(Lib.ChrSel, idx + GLOW_OFFSET + i)))).map(toPic);
    if (this.scope.signal.aborted) return;
    const base = pics.filter((q): q is Pic => !!q);
    if (!base.length) { this.frames.delete(idx); return; }
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const q of base) { x0 = Math.min(x0, q.x); y0 = Math.min(y0, q.y); x1 = Math.max(x1, q.x + q.w); y1 = Math.max(y1, q.y + q.h); }
    const w = x1 - x0, hh = y1 - y0;
    const k = Math.min(1.7, 420 / Math.max(1, hh), 380 / Math.max(1, w));
    const f: Frames = { pics: base, glow: pics.map((q, i) => (q ? glow[i] ?? null : null)).filter((_, i) => !!pics[i]), x0, y0, w, h: hh, k };
    this.frames.set(idx, f);
    if (this.showIdx === idx) this.layout(f);
  }

  private layout(f: Frames): void {
    setStyle(this.spr, "width", f.w + "px");
    setStyle(this.spr, "height", f.h + "px");
    setStyle(this.spr, "margin-left", -f.w / 2 + "px");
    setStyle(this.spr, "transform", `scale(${f.k})`);
    this.spr.hidden = false;
    restartAnim(this.spr, "in");
    this.draw(f);
  }

  private draw(f: Frames): void {
    const i = this.frame % f.pics.length;
    const p = f.pics[i]!, g = f.glow[i] ?? null;
    if (this.base.src !== p.url) this.base.src = p.url;
    setStyle(this.base, "left", p.x - f.x0 + "px");
    setStyle(this.base, "top", p.y - f.y0 + "px");
    setStyle(this.base, "width", p.w + "px");
    setStyle(this.base, "height", p.h + "px");
    this.glow.hidden = !g;
    if (g) {
      if (this.glow.src !== g.url) this.glow.src = g.url;
      setStyle(this.glow, "left", g.x - f.x0 + "px");
      setStyle(this.glow, "top", g.y - f.y0 + "px");
      setStyle(this.glow, "width", g.w + "px");
      setStyle(this.glow, "height", g.h + "px");
    }
  }

  private tick(): void {
    const f = this.frames.get(this.showIdx);
    if (!f) return;
    this.frame = (this.frame + 1) % f.pics.length;
    this.draw(f);
  }

  // — Keys —

  private key(e: KeyboardEvent): void {
    if (this.popups.open) { this.popups.handleKey(e); e.stopPropagation(); return; }
    const t = e.target as HTMLElement | null;
    if (e.repeat || t?.closest?.("input, textarea, select")) return;
    if (this.mode === "select") {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        const n = this.characters.length;
        if (n) this.pick((this.selected + (e.key === "ArrowDown" ? 1 : n - 1)) % n);
      } else if (e.key === "Enter" && (t === this.root || !t?.closest?.("[role=button]"))) {
        e.preventDefault();
        this.startGame();
      }
    } else if (e.key === "Escape") {
      e.preventDefault();
      this.cancelCreate();
    }
  }

  // — Start (C.StartGame) —

  /** SelectScene.StartGame: C.StartGame for the selected character, once, until the server answers. */
  private startGame(): void {
    const c = this.current;
    if (!c || this.busy() || this.delayUntil || this.mode !== "select") return;
    this.entering = c;
    this.msg = ["", ""];
    this.render();
    this.host.send(ClientPackets.StartGame({ CharacterIndex: c.Index }));
  }

  private startGameResult(p: P<"StartGame">): void {
    const c = this.entering ?? this.current;
    this.entering = null;
    if (p.Result === 4) {
      if (c) {
        setPlayingCharacter(c.Name);
        this.onStart(c);
      }
      return;
    }
    this.render();
    const text = p.Result === 0 ? Text.StartingGameDisabled : p.Result === 1 ? Text.YouNotLoggedIn : p.Result === 2 ? Text.YourCharacterNotFound
      : p.Result === 3 ? Text.NoActiveMapOrStartPointFound : `Start failed (${p.Result}).`;
    void this.popups.confirm({ title: "Cannot start", text, kind: "warning" });
  }

  /** S.StartGameDelay: count down on the Start button; when it runs out, StartGame is sent again (the classic box). */
  private startGameDelay(ms: number): void {
    this.entering = null;
    this.delayUntil = performance.now() + Math.max(0, ms);
    this.host.track(() => this.cancelDelay());
    this.render();
    this.delayTimer.on();
  }

  private delayTick(): void {
    if (!this.delayUntil) { this.delayTimer.off(); return; }
    if (performance.now() <= this.delayUntil) { this.updateStart(); return; }
    this.cancelDelay();
    this.startGame();
  }

  private cancelDelay(): void {
    if (!this.delayUntil) return;
    this.delayUntil = 0;
    this.delayTimer.off();
    this.updateStart();
  }

  // — Log out / switch —

  /** SelectScene ExitGame / "Back to log in": the server has no select-to-login transition; log out and reconnect. */
  private logOut(): void {
    this.host.send(ClientPackets.LogOut());
    location.reload();
  }

  private switchToClassic(): void {
    if (this.entering) return;
    setSetting("UiStyle", "Classic");
    if (this.deps.onClassic) this.deps.onClassic();
    else this.logOut();
  }

  // — Delete (C.DeleteCharacter) —

  /** SelectScene.DeleteCharacter: Yes / No, then the character's name; a mismatch is "Incorrect Entry.". */
  private async deleteCharacter(): Promise<void> {
    const c = this.current;
    if (!c || this.busy()) return;
    this.cancelDelay();
    const yes = await this.popups.ask(format(Text.ConfirmDeleteCharacter, c.Name), { title: "Delete character", kind: "danger" });
    if (!yes || this.scope.signal.aborted) return;
    const name = await this.popups.input({
      title: "Delete character", text: Text.PleaseEnterCharacterName, placeholder: c.Name, max: Globals.MaxCharacterNameLength,
      check: (v) => (v && v !== c.Name ? "The name does not match" : ""),
    });
    if (name === null || this.scope.signal.aborted) return;
    if (name !== c.Name || this.busy() || !this.characters.includes(c)) { this.msg = [Text.IncorrectEntry, "err"]; this.render(); return; }
    this.deleting = true;
    this.msg = ["", ""];
    this.render();
    this.host.send(ClientPackets.DeleteCharacter({ CharacterIndex: c.Index }));
  }

  private deleteCharacterResult(p: P<"DeleteCharacter">): void {
    this.deleting = false;
    this.msg = [p.Result === 0 ? Text.DeletingCharactersDisabled : p.Result === 1 ? Text.SelectedCharacterNotExist : `Delete failed (${p.Result}).`, "err"];
    this.render();
  }

  private deleteCharacterSuccess(p: P<"DeleteCharacterSuccess">): void {
    this.deleting = false;
    const i = this.characters.findIndex((c) => c.Index === p.CharacterIndex);
    if (i >= 0) this.characters.splice(i, 1);
    if (this.selected >= this.characters.length) this.selected = 0;
    this.msg = [Text.YourCharacterDeletedSuccessfully, "ok"];
    this.render();
  }

  // — Create (NewCharacterDialog, C.NewCharacter) —

  private newChar(): void {
    if (this.busy()) return;
    if (this.characters.length >= Globals.MaxCharacterCount) {
      this.msg = [format(Text.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount), "err"];
      this.render();
      return;
    }
    this.cancelDelay();
    this.mode = "create";
    this.cName = "";
    this.cErr = "";
    this.cCls = MirClass.Warrior;
    this.cGender = MirGender.Male;
    this.msg = ["", ""];
    this.render();
  }

  private cancelCreate(): void {
    if (this.creating || !this.characters.length) return;
    this.mode = "select";
    this.render();
  }

  private classPanel(): HTMLElement {
    const p = h("div", "glass classes", h("div", "lb", "Class"));
    for (const cl of CLASSES) {
      const off = !this.allowed(cl);
      const icon = h("i");
      this.portrait(icon, cl, this.cGender);
      const e = asButton(h("div", "cc" + (this.cCls === cl ? " on" : "") + (off ? " off" : ""), icon, h("div", "", h("b", "", className(cl)), h("small", "", classTag(cl)))), className(cl));
      if (off) { e.title = "Disabled on this server"; e.setAttribute("aria-disabled", "true"); }
      e.addEventListener("click", () => { if (!off && !this.creating && this.cCls !== cl) { this.cCls = cl; this.cErr = ""; this.render(); } });
      p.append(e);
    }
    return p;
  }

  private createPanel(): HTMLElement {
    const p = h("div", "glass info", h("div", "lb", className(this.cCls)), h("div", "about", classDescription(this.cCls)), h("div", "lb", "Gender"));
    const g = h("div", "gender");
    for (const [label, v] of [["Male", MirGender.Male], ["Female", MirGender.Female]] as const) {
      const d = asButton(h("div", this.cGender === v ? "on" : "", label));
      d.addEventListener("click", () => { if (!this.creating && this.cGender !== v) { this.cGender = v; this.render(); } });
      g.append(d);
    }
    const inp = typingField(h("input", "nmi"));
    inp.type = "text";
    inp.maxLength = Globals.MaxCharacterNameLength;
    inp.placeholder = "Character name";
    inp.value = this.cName;
    inp.disabled = this.creating;
    const hint = h("small", "");
    const nm = h("div", "nm", inp, hint);
    const cr = asButton(h("div", "b2 main", this.creating ? "Creating…" : "Create"));
    const check = () => {
      const v = inp.value;
      const bad = this.cErr || (!v ? "" : !NAME_RX.test(v) ? NAME_HINT : "");
      nm.classList.toggle("bad", !!bad);
      nm.classList.toggle("good", !!v && !bad);
      setText(hint, bad || (v ? "✓ Looks good" : NAME_HINT));
      setText(this.plateName, v || "New character");
      cr.classList.toggle("dis", this.creating || !NAME_RX.test(v));
    };
    inp.addEventListener("input", () => { this.cName = inp.value; this.cErr = ""; check(); });
    inp.addEventListener("keydown", (e) => {
      if (e.repeat) return;
      if (e.key === "Enter") { e.preventDefault(); this.create(); }
      else if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); this.cancelCreate(); }
    });
    cr.addEventListener("click", () => this.create());
    const cancel = asButton(h("div", "b2" + (this.characters.length && !this.creating ? "" : " dis"), "Cancel"));
    cancel.addEventListener("click", () => this.cancelCreate());
    p.append(g, h("div", "lb", "Name"), nm, h("div", "btns", cancel, cr));
    if (this.msg[0]) p.append(h("div", "msg " + this.msg[1], this.msg[0]));
    check();
    this.nameInput = inp;
    if (!this.creating) queueMicrotask(() => { if (this.nameInput === inp && inp.isConnected) inp.focus({ preventScroll: true }); });
    return p;
  }

  /** NewCharacterDialog OK: C.NewCharacter (the server checks the name, class, gender and count). */
  private create(): void {
    const v = this.cName;
    if (this.creating || this.mode !== "create") return;
    if (!NAME_RX.test(v)) { this.cErr = Text.YourCharacterNameNotAcceptable; this.render(); return; }
    if (!this.allowed(this.cCls)) return;
    this.creating = true;
    this.cErr = "";
    this.msg = ["", ""];
    this.render();
    this.host.send(ClientPackets.NewCharacter({ Name: v, Class: this.cCls, Gender: this.cGender }));
  }

  private newCharacter(p: P<"NewCharacter">): void {
    this.creating = false;
    switch (p.Result) {
      case 0: // creation disabled / too many: the classic closes the dialog
      case 4:
        this.msg = [p.Result === 0 ? Text.CreatingNewCharactersDisabled : format(Text.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount), "err"];
        if (this.characters.length) this.mode = "select";
        break;
      case 1: this.cErr = Text.YourCharacterNameNotAcceptable; break;
      case 5: this.cErr = Text.CharacterNameExists; break;
      case 2: this.msg = [Text.GenderNotExistContactGM, "err"]; break;
      case 3: this.msg = [Text.ClassNotExistContactGM, "err"]; break;
      default: this.msg = [`Character creation failed (${p.Result}).`, "err"]; break;
    }
    this.render();
  }

  private newCharacterSuccess(p: P<"NewCharacterSuccess">): void {
    this.creating = false;
    this.characters.unshift(p.CharInfo);
    this.selected = 0;
    this.mode = "select";
    this.msg = [Text.YourCharacterCreatedSuccessfully, "ok"];
    this.render();
  }
}

/** Factory with the classic SelectScene's arguments (plus the optional stores): what Screens calls for the new UI. */
export function createSelectScene(parent: HTMLElement, host: SceneHost, characters: SelectInfo[], onStart: (c: SelectInfo) => void, deps: SelectSceneDeps = {}): SelectUi {
  return new ModernSelectScene(parent, host, characters, onStart, deps);
}
