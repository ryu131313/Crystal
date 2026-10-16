// Chat box mock for the scene lab's UI tester: a mostly transparent, AAA-style take on the desktop's ChatDialog +
// ChatOptionDialog + ChatNoticeDialog. Bottom left of the screen, no panel until you hover it or type (the background
// fades in to the opacity you set), lines with text shadows that fade away after a while when idle. Tabs (All, Local,
// Whispers, Group, Guild, System) with unread badges, a tab per whisper partner made on the first whisper, custom tabs
// (+, double-click to rename, × to close) and a per-tab channel filter. The input opens with Enter (Esc leaves): a channel
// pill (Say / Shout / Group / Guild / Whisper / Mentor / Lover), the server's prefixes typed then a space switch the pill
// (PlayerObject.Chat: "!" shout, "!!" group, "!~" guild, "!#" mentor, ":)" lover, "/name" whisper), @commands and names
// autocomplete (↑ ↓ Tab / Enter), ↑ ↓ history, item links from the bag ([ItemName] chips with a card), a character count.
// Messages: channel colours from the desktop's ChatType (Classic) or the theme accent (Theme), channel tags, timestamps,
// a name menu (Whisper, Group invite, Add friend, Inspect, Block, Copy), mentions of your name highlighted (and the tab
// flashes), repeated lines folded into ×N, a "new messages" pill when scrolled up, search, settings (opacity, fade,
// font size, timestamps, tags, colours, live traffic, lock), move (drag the tab bar) and resize (top-right grip). The
// announcement ticker (LineMessage / ChatNoticeDialog) runs across the top of the screen. All traffic is simulated.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { COMMANDS } from "./hudHelp.ts";
import { h, injectCss } from "./hudWindow.ts";

type Ch = "Normal" | "Shout" | "Group" | "Guild" | "Whisper" | "Mentor" | "Lover" | "System" | "Hint" | "Announcement" | "LevelUp";
const ALL: Ch[] = ["Normal", "Shout", "Group", "Guild", "Whisper", "Mentor", "Lover", "System", "Hint", "Announcement", "LevelUp"];
/** Channels you can talk in, with the server prefix (PlayerObject.Chat). */
const SPEAK: [Ch, string, string][] = [["Normal", "", "Say"], ["Shout", "!", "Shout"], ["Group", "!!", "Group"], ["Guild", "!~", "Guild"], ["Whisper", "/", "Whisper"], ["Mentor", "!#", "Mentor"], ["Lover", ":)", "Lover"]];
/** Classic colours, after the desktop ChatDialog's per-ChatType colours, brightened for a transparent box. */
const CLASSIC: Record<Ch, string> = {
  Normal: "#eef2fa", Shout: "#ffd84a", Group: "#8ad4ff", Guild: "#6fe08a", Whisper: "#d9a6ff", Mentor: "#c9a0ff", Lover: "#ff9cc4",
  System: "#ff7a6a", Hint: "#8fd8a8", Announcement: "#7fb0ff", LevelUp: "#f2c94c",
};
const TAG: Record<Ch, string> = { Normal: "", Shout: "Shout", Group: "Group", Guild: "Guild", Whisper: "", Mentor: "Mentor", Lover: "Lover", System: "System", Hint: "Hint", Announcement: "Notice", LevelUp: "Level" };
interface Line { id: number; t: number; ch: Ch; from?: string; to?: string; text: string; n: number; me?: boolean }
interface Tab { id: number; name: string; chans: Set<Ch>; whisper?: string; unread: number; ping: boolean; fixed: boolean }
const ME = "GMRyu";
const NAMES = ["Seren", "Wren", "Kaelith", "Nyra", "Aldric", "Mirelle", "Garrick", "Elowen", "Pip", "Tamsin", "Odo", "Brisk"];
const ONLINE = new Set(["Seren", "Wren", "Kaelith", "Aldric", "Mirelle", "Garrick", "Elowen", "Pip", "Tamsin", "Odo", "Brisk"]);
/** Item links: name → Items.Lib image (from the DB copy). */
const LINKABLE: [string, number][] = [["DragonSlayer", 57], ["DragonStaff", 58], ["TarragonNecklace", 2764], ["GoldDragonRing", 1001], ["SoulRing", 674],
  ["HeroNecklace", 682], ["BlackTigerArmour(M)", 1030], ["(HP)DrugXL", 813], ["BengalTiger", 2040], ["HealthStone(M)", 586]];
const TRAFFIC: [Ch, string | null, string][] = [
  ["Normal", "Pip", "anyone know where the Zuma temple entrance is?"], ["Normal", "Tamsin", "selling (HP)DrugXL, cheap"], ["Normal", "Odo", "lol that skeleton just one-shot me"],
  ["Shout", "Garrick", "LF2M Zuma run, need a Taoist! /Garrick"], ["Shout", "Brisk", "WTB [BlackTigerArmour(M)] paying well"], ["Shout", "Elowen", "Guild Nightfall recruiting, level 40+"],
  ["Group", "Seren", "pulling the next pack"], ["Group", "Wren", "MP low, give me a sec"], ["Group", "Kaelith", "nice one GMRyu!"],
  ["Guild", "Aldric", "guild war with Nightfall at 8pm, be online"], ["Guild", "Mirelle", "dropped a [GoldDragonRing] in storage for whoever needs it"], ["Guild", "Nyra", "gg everyone"],
  ["System", null, "You picked up [SoulRing]."], ["Hint", null, "Your armour is wearing down. Repair it at a merchant."], ["LevelUp", null, "Wren has reached level 29!"],
  ["Mentor", "Wren", "thanks for the help earlier!"], ["Lover", "Seren", "miss you ♥"], ["Whisper", "Garrick", "want to join our Zuma run?"], ["Whisper", "Seren", "come to the fountain?"],
];
const fmtTime = (t: number) => new Date(t).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });

const CSS = `
.hudm .hchat { position: absolute; pointer-events: auto; display: flex; flex-direction: column; color: var(--tx); border-radius: var(--rad); border: 1px solid transparent;
  --cbg: 0; background: color-mix(in srgb, var(--pb) calc(var(--cbg) * 100%), transparent); transition: background .3s, border-color .3s; }
.hudm .hchat.act { --cbg: var(--cop); border-color: color-mix(in srgb, var(--fr) 70%, transparent); box-shadow: 0 6px 24px rgba(0,0,0,.35); }
.hudm .hchat .ctop { display: flex; align-items: center; gap: 2px; padding: 3px 4px 0; opacity: .0; transition: opacity .3s; cursor: move; }
.hudm .hchat.act .ctop, .hudm .hchat .ctop.ping { opacity: 1; }
.hudm .hchat.lock .ctop { cursor: default; }
.hudm .hchat .tstrip { position: relative; flex: 1; min-width: 0; display: flex; gap: 2px; overflow-x: auto; scrollbar-width: none; }
.hudm .hchat .ct { position: relative; flex: none; display: flex; align-items: center; gap: 4px; height: 20px; padding: 0 7px; cursor: pointer; user-select: none; font-size: 11px; font-weight: 600; border-radius: calc(var(--rad) * .7) calc(var(--rad) * .7) 0 0;
  color: var(--dim); text-shadow: 0 1px 2px #000; }
.hudm .hchat .ct:hover { color: var(--tx); }
.hudm .hchat .ct.on { color: #fff; background: color-mix(in srgb, var(--ac) 22%, transparent); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .hchat .ct.ping { animation: hudm-ctping 1s ease-in-out infinite; } @keyframes hudm-ctping { 50% { color: #fff; background: color-mix(in srgb, var(--ac) 45%, transparent); } }
.hudm .hchat .ct b { min-width: 14px; height: 14px; padding: 0 3px; box-sizing: border-box; border-radius: 7px; font-size: 9px; display: flex; align-items: center; justify-content: center; color: #fff; background: var(--ac); }
.hudm .hchat .ct i { display: none; font-style: normal; color: var(--dim); margin-left: 2px; } .hudm .hchat .ct:hover i { display: inline; } .hudm .hchat .ct i:hover { color: #ff8a6a; }
.hudm .hchat .ct input { width: 70px; height: 16px; font-size: 11px; padding: 0 4px; border: 1px solid var(--ac); background: #05070c; color: #fff; border-radius: 3px; outline: none; }
.hudm .hchat .sp { flex: 1; align-self: stretch; }
.hudm .hchat .ib { width: 22px; height: 20px; display: flex; align-items: center; justify-content: center; cursor: pointer; color: var(--dim); border-radius: 4px; }
.hudm .hchat .ib:hover, .hudm .hchat .ib.on { color: #fff; background: color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .hchat svg { width: 13px; height: 13px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hchat .srch { display: none; padding: 3px 6px 0; } .hudm .hchat .srch.on { display: flex; gap: 6px; align-items: center; font-size: 10px; color: var(--dim); }
.hudm .hchat .srch input { flex: 1; height: 20px; padding: 0 7px; font-size: 11px; border-radius: 10px; border: 1px solid var(--fr); background: #05070cc0; color: var(--tx); outline: none; }
.hudm .hchat .log { position: relative; flex: 1; min-height: 0; overflow-y: auto; overflow-x: hidden; padding: 3px 8px 4px; scrollbar-width: none; mask-image: linear-gradient(180deg, transparent, #000 14px); }
.hudm .hchat.act .log { scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hchat .ln { position: relative; padding: 1px 4px; margin: 0 -4px; border-radius: 3px; line-height: 1.35; font-size: var(--cfs); text-shadow: 0 1px 2px #000, 0 0 3px #000; word-wrap: break-word; transition: opacity 1.2s; }
.hudm .hchat:not(.act) .ln.old { opacity: 0; }
.hudm .hchat .ln:hover { background: rgba(255,255,255,.05); }
.hudm .hchat .ln.mention { background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 2px 0 0 var(--ac); }
.hudm .hchat .ln.hit { background: rgba(242,201,76,.14); }
.hudm .hchat .ln .tm { font-size: .82em; color: var(--dim); margin-right: 4px; font-variant-numeric: tabular-nums; }
.hudm .hchat .ln .tg { font-size: .78em; font-weight: 700; margin-right: 4px; opacity: .9; }
.hudm .hchat .ln .nm { font-weight: 700; cursor: pointer; } .hudm .hchat .ln .nm:hover { text-decoration: underline; }
.hudm .hchat .ln .x { margin-left: 5px; font-size: .78em; font-weight: 800; padding: 0 4px; border-radius: 6px; color: #000; background: var(--dim); }
.hudm .hchat .ln mark { background: rgba(242,201,76,.5); color: #fff; border-radius: 2px; }
.hudm .hchat .lk { cursor: pointer; font-weight: 700; color: #f2c94c; } .hudm .hchat .lk:hover { text-decoration: underline; }
.hudm .hchat .ln.sys { font-style: italic; }
.hudm .hchat .newp { position: absolute; left: 50%; bottom: 40px; translate: -50% 0; z-index: 2; padding: 2px 10px; border-radius: 10px; cursor: pointer; font-size: 10px; font-weight: 700; color: #fff;
  background: var(--ac); box-shadow: 0 0 10px var(--glow); }
.hudm .hchat .cin { display: none; align-items: center; gap: 5px; padding: 4px; border-top: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .hchat.typing .cin { display: flex; }
.hudm .hchat .hint { padding: 2px 8px 5px; font-size: 10px; color: var(--dim); text-shadow: 0 1px 2px #000; opacity: 0; transition: opacity .3s; } .hudm .hchat.act:not(.typing) .hint { opacity: 1; }
.hudm .hchat.typing .hint { display: none; }
.hudm .hchat .pill { flex: none; height: 22px; padding: 0 8px; display: flex; align-items: center; gap: 4px; cursor: pointer; user-select: none; font-size: 11px; font-weight: 700; border-radius: 11px; border: 1px solid currentColor; background: #05070cd0; }
.hudm .hchat .cin input { flex: 1; min-width: 0; height: 24px; padding: 0 8px; font-size: 12px; border-radius: calc(var(--rad) * .7); border: 1px solid var(--fr); background: #05070cd8; color: #fff; outline: none; }
.hudm .hchat .cin input:focus { border-color: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .hchat .cnt { font-size: 9px; color: var(--dim); width: 34px; text-align: right; font-variant-numeric: tabular-nums; } .hudm .hchat .cnt.hi { color: #ff8a6a; }
.hudm .hchat .pop { position: absolute; z-index: 5; min-width: 150px; padding: 4px; border-radius: var(--rad); background: color-mix(in srgb, var(--pb) 96%, #000); border: 1px solid var(--fr); box-shadow: 0 8px 24px rgba(0,0,0,.7); }
.hudm .hchat .pop .it { display: flex; align-items: center; gap: 8px; padding: 4px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; white-space: nowrap; }
.hudm .hchat .pop .it:hover, .hudm .hchat .pop .it.on { background: color-mix(in srgb, var(--ac) 25%, transparent); color: #fff; }
.hudm .hchat .pop .it small { color: var(--dim); font-size: 10px; margin-left: auto; } .hudm .hchat .pop .it code { font: 600 11px ui-monospace, Consolas, monospace; color: var(--ac); }
.hudm .hchat .pop .it .ic { width: 28px; height: 28px; flex: none; border-radius: 4px; border: 1px solid var(--fr); background: #000 center / auto no-repeat; }
.hudm .hchat .pop .hd { padding: 3px 8px; font-size: 9px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hchat .pop .sep { height: 1px; margin: 3px 4px; background: var(--fr); }
.hudm .hchat .card { position: absolute; z-index: 6; display: flex; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--pb) 96%, #000); border: 1px solid #f2c94c;
  box-shadow: 0 8px 24px rgba(0,0,0,.7); font-size: 12px; }
.hudm .hchat .card .ic { width: 40px; height: 40px; border-radius: 4px; border: 1px solid var(--fr); background: #000 center / auto no-repeat; }
.hudm .hchat .card b { display: block; color: #f2c94c; } .hudm .hchat .card small { color: var(--dim); font-size: 10px; }
.hudm .hchat .set { width: 240px; padding: 8px 10px; display: flex; flex-direction: column; gap: 7px; font-size: 11px; }
.hudm .hchat .set .r { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.hudm .hchat .set input[type=range] { width: 110px; accent-color: var(--ac); }
.hudm .hchat .seg { display: flex; border-radius: 9px; overflow: hidden; border: 1px solid var(--fr); }
.hudm .hchat .seg div { padding: 1px 7px; cursor: pointer; font-size: 10px; color: var(--dim); } .hudm .hchat .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
.hudm .hchat .sw { position: relative; width: 26px; height: 14px; flex: none; cursor: pointer; border-radius: 7px; background: var(--pb); border: 1px solid var(--fr); }
.hudm .hchat .sw::after { content: ""; position: absolute; left: 2px; top: 2px; width: 8px; height: 8px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .hchat .sw.on { background: color-mix(in srgb, var(--ac) 45%, var(--pb)); border-color: var(--ac); } .hudm .hchat .sw.on::after { left: 14px; background: #fff; }
.hudm .hchat .chs { display: grid; grid-template-columns: 1fr 1fr; gap: 3px; }
.hudm .hchat .chs label { display: flex; align-items: center; gap: 5px; cursor: pointer; } .hudm .hchat .chs input { accent-color: var(--ac); margin: 0; }
.hudm .hchat .grip { position: absolute; right: -1px; top: -1px; width: 14px; height: 14px; cursor: nesw-resize; opacity: 0; border-top: 2px solid var(--ac); border-right: 2px solid var(--ac); border-radius: 0 var(--rad) 0 0; }
.hudm .hchat.act:not(.lock) .grip { opacity: .8; }
/* Announcement ticker (LineMessage / ChatNoticeDialog) */
.hudm .cnotice { position: absolute; left: 50%; top: 10px; translate: -50% 0; width: 560px; height: 26px; overflow: hidden; pointer-events: none; border-radius: 13px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--pb) 85%, transparent) 15%, color-mix(in srgb, var(--pb) 85%, transparent) 85%, transparent);
  mask-image: linear-gradient(90deg, transparent, #000 12%, #000 88%, transparent); }
.hudm .cnotice span { position: absolute; top: 4px; white-space: nowrap; font-size: 13px; font-weight: 700; color: #fff; text-shadow: 0 0 8px var(--glow), 0 1px 2px #000; animation: hudm-tick var(--dur) linear forwards; }
.hudm .cnotice span b { color: var(--ac); margin-right: 8px; }
@keyframes hudm-tick { from { left: 100%; } to { left: 0; transform: translateX(-100%); } }
`;
/** A chat popup row: plain text, or nodes ("-" = a separator). */
type PopLabel = string | (Node | string)[];
const coloured = (text: string, colour: string) => { const s = h("span", "", text); s.style.color = colour; return s; };
const svg = (d: string) => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.innerHTML = `<path d="${d}"/>`; return s; };
const I = { gear: "M12 9a3 3 0 110 6 3 3 0 010-6zM12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2", search: "M11 4a7 7 0 110 14 7 7 0 010-14zM20 20l-4-4",
  lock: "M6 11h12v9H6zM8 11V8a4 4 0 018 0v3", unlock: "M6 11h12v9H6zM8 11V8a4 4 0 017.5-2", link: "M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1",
  send: "M4 12l16-8-6 16-2-7z", plus: "M12 5v14M5 12h14" };

export class ChatMock {
  readonly el: HTMLElement;
  readonly notice: HTMLElement;
  private lines: Line[] = [];
  private nextId = 1;
  private tabs: Tab[] = [];
  private tab = 0;
  private ch: Ch = "Normal";
  private target = "";
  private hist: string[] = [];
  private histAt = -1;
  private blocked = new Set<string>();
  private query = "";
  private opt = { op: 0.55, fade: 20, fs: 12, time: false, tags: true, theme: false, live: true, lock: false };
  private pos = { left: 8, bottom: 186, w: 440, h: 196 };
  private stick = true;
  private unseen = 0;
  private hover = false;
  private allowTrade = true;
  private log!: HTMLElement;
  private tabBar!: HTMLElement;
  private input!: HTMLInputElement;
  private pill!: HTMLElement;
  private cnt!: HTMLElement;
  private srch!: HTMLElement;
  private pop: HTMLElement | null = null;
  private popSel = 0;
  private popItems: [PopLabel, () => void][] = [];

  constructor() {
    injectCss("hudm-chat-css", CSS);
    this.el = h("div", "hchat");
    this.notice = h("div", "cnotice");
    this.notice.hidden = true;
    const mk = (name: string, chans: Ch[], fixed = true): Tab => ({ id: this.nextId++, name, chans: new Set(chans), unread: 0, ping: false, fixed });
    this.tabs = [mk("All", ALL), mk("Local", ["Normal", "Shout", "System", "Hint", "Announcement", "LevelUp"]), mk("Whispers", ["Whisper", "Lover", "Mentor"]),
      mk("Group", ["Group", "System"]), mk("Guild", ["Guild", "Announcement"]), mk("System", ["System", "Hint", "Announcement", "LevelUp"])];
    this.build();
    this.place();
    const seed: [Ch, string | null, string][] = [["Announcement", null, "Welcome to Crystal! Double EXP this weekend."], ["Normal", "Pip", "hi all"], ["Group", "Seren", "ready when you are"],
      ["Guild", "Aldric", "GMRyu, can you check the guild storage?"], ["System", null, "You picked up [TarragonNecklace]."], ["Shout", "Garrick", "LF2M Zuma run! /Garrick"], ["Shout", "Garrick", "LF2M Zuma run! /Garrick"]];
    for (const [ch, from, text] of seed) this.add(ch, text, from ?? undefined, undefined, false);
    for (const l of this.lines) l.t -= 4000;
    window.setInterval(() => this.ageLines(), 1000);
    const sim = () => { if (this.opt.live) this.simulate(); window.setTimeout(sim, 2500 + Math.random() * 4500); };
    window.setTimeout(sim, 3000);
    document.addEventListener("mousedown", (e) => { if (this.pop && !this.pop.contains(e.target as Node)) this.closePop(); });
  }

  /** Ctrl+click on an item anywhere (hudTooltip.ts): add an [Item] link to the input and open it. */
  insertLink(name: string): void {
    if (!LINKABLE.some(([n]) => n === name)) LINKABLE.push([name, -1]);
    const v = this.input.value;
    this.input.value = (v && !v.endsWith(" ") ? v + " " : v) + `[${name}] `;
    this.focus();
  }

  /** Enter (from hudMock.ts) opens the input. */
  focus(): void { this.el.classList.add("typing", "act"); this.input.focus(); this.updatePill(); }

  private build(): void {
    this.tabBar = h("div", "ctop");
    this.drag(this.tabBar);
    this.srch = h("div", "srch");
    const sIn = h("input"); sIn.placeholder = "Search chat…";
    sIn.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Escape") { this.query = ""; sIn.value = ""; this.srch.classList.remove("on"); this.renderLog(); this.renderTabs(); } });
    sIn.addEventListener("input", () => { this.query = sIn.value.trim().toLowerCase(); this.renderLog(); });
    this.srch.append(sIn);
    this.log = h("div", "log");
    this.log.addEventListener("scroll", () => {
      const atEnd = this.log.scrollHeight - this.log.scrollTop - this.log.clientHeight < 6;
      if (atEnd && !this.stick) { this.stick = true; this.unseen = 0; this.renderPill(); } else if (!atEnd) this.stick = false;
    });
    this.pill = h("div", "pill");
    this.pill.addEventListener("click", (e) => { e.stopPropagation(); this.channelMenu(); });
    this.input = h("input");
    this.input.maxLength = 200;
    this.input.addEventListener("keydown", (e) => this.onKey(e));
    this.input.addEventListener("input", () => this.onInput());
    this.input.addEventListener("blur", () => window.setTimeout(() => { if (document.activeElement !== this.input && !this.pop && !this.input.value) this.el.classList.remove("typing"); this.refreshAct(); }, 150));
    const link = h("div", "ib", svg(I.link)); link.title = "Link an item";
    link.addEventListener("mousedown", (e) => e.preventDefault());
    link.addEventListener("click", (e) => { e.stopPropagation(); this.itemMenu(link); });
    const send = h("div", "ib", svg(I.send)); send.title = "Send (Enter)";
    send.addEventListener("mousedown", (e) => e.preventDefault());
    send.addEventListener("click", () => this.send());
    this.cnt = h("div", "cnt");
    const grip = h("div", "grip"); grip.title = "Resize";
    this.resize(grip);
    this.el.append(this.tabBar, this.srch, this.log, h("div", "cin", this.pill, this.input, link, this.cnt, send), h("div", "hint", "Press Enter to chat · right-click a name for options"), grip);
    this.el.addEventListener("mouseenter", () => { this.hover = true; this.refreshAct(); });
    this.el.addEventListener("mouseleave", () => { this.hover = false; this.refreshAct(); });
    this.renderTabs(); this.renderLog(); this.applyOpts();
  }

  private refreshAct(): void {
    const on = this.hover || document.activeElement === this.input || !!this.pop || this.srch.classList.contains("on");
    this.el.classList.toggle("act", on);
  }
  private applyOpts(): void {
    this.el.style.setProperty("--cop", String(this.opt.op));
    this.el.style.setProperty("--cfs", this.opt.fs + "px");
    this.el.classList.toggle("lock", this.opt.lock);
  }
  private place(): void {
    Object.assign(this.el.style, { left: this.pos.left + "px", bottom: this.pos.bottom + "px", width: this.pos.w + "px", height: this.pos.h + "px" });
  }
  private scale(): number { return this.el.getBoundingClientRect().width / this.el.offsetWidth || 1; }
  private drag(handle: HTMLElement): void {
    handle.addEventListener("mousedown", (e) => {
      if (this.opt.lock || (e.target as HTMLElement).closest(".ct, .ib, input")) return;
      e.preventDefault();
      const k = this.scale(), sx = e.clientX, sy = e.clientY, ol = this.pos.left, ob = this.pos.bottom;
      const mv = (m: MouseEvent) => { this.pos.left = Math.max(0, ol + (m.clientX - sx) / k); this.pos.bottom = Math.max(0, ob - (m.clientY - sy) / k); this.place(); };
      const up = () => { window.removeEventListener("mousemove", mv); window.removeEventListener("mouseup", up); };
      window.addEventListener("mousemove", mv); window.addEventListener("mouseup", up);
    });
  }
  private resize(grip: HTMLElement): void {
    grip.addEventListener("mousedown", (e) => {
      if (this.opt.lock) return;
      e.preventDefault(); e.stopPropagation();
      const k = this.scale(), sx = e.clientX, sy = e.clientY, ow = this.pos.w, oh = this.pos.h;
      const mv = (m: MouseEvent) => { this.pos.w = Math.min(760, Math.max(260, ow + (m.clientX - sx) / k)); this.pos.h = Math.min(520, Math.max(110, oh - (m.clientY - sy) / k)); this.place(); };
      const up = () => { window.removeEventListener("mousemove", mv); window.removeEventListener("mouseup", up); };
      window.addEventListener("mousemove", mv); window.addEventListener("mouseup", up);
    });
  }

  // — Messages —
  private colour(l: Line): string {
    if (!this.opt.theme) return CLASSIC[l.ch];
    return l.ch === "System" ? "#ff8a7a" : l.ch === "Normal" ? "var(--tx)" : l.ch === "Hint" || l.ch === "LevelUp" || l.ch === "Announcement" ? "var(--dim)" : "color-mix(in srgb, var(--ac) 55%, #fff)";
  }
  private shows(tab: Tab, l: Line): boolean {
    if (l.from && this.blocked.has(l.from)) return false;
    if (tab.whisper) return l.ch === "Whisper" && (l.from === tab.whisper || l.to === tab.whisper);
    return tab.chans.has(l.ch);
  }
  private add(ch: Ch, text: string, from?: string, to?: string, live = true): void {
    const last = this.lines[this.lines.length - 1];
    if (last && last.ch === ch && last.from === from && last.to === to && last.text === text) { last.n++; last.t = Date.now(); }
    else this.lines.push({ id: this.nextId++, t: Date.now(), ch, from, to, text, n: 1, me: from === ME });
    if (this.lines.length > 250) this.lines.shift();
    if (ch === "Whisper") { const who = from === ME ? to! : from!; if (!this.tabs.some((t) => t.whisper === who)) this.tabs.push({ id: this.nextId++, name: who, chans: new Set(["Whisper"]), whisper: who, unread: 0, ping: false, fixed: false }); }
    const l = this.lines[this.lines.length - 1]!;
    const mention = !!from && from !== ME && new RegExp(`\\b${ME}\\b`, "i").test(text);
    if (live) this.tabs.forEach((t, i) => {
      if (i === this.tab || !this.shows(t, l) || from === ME) return;
      t.unread++;
      if (mention || (ch === "Whisper" && t.whisper)) t.ping = true;
    });
    if (ch === "Announcement" && live) this.ticker(text);
    if (!this.stick && this.shows(this.tabs[this.tab]!, l) && live) this.unseen++;
    this.renderTabs(); this.renderLog();
    if (mention && live) { this.tabBar.classList.add("ping"); window.setTimeout(() => this.tabBar.classList.remove("ping"), 4000); }
  }
  private sys(text: string, ch: Ch = "System"): void { this.add(ch, text); }
  /** Name menu > Inspect (hudMock.ts wires the inspect window); false = not someone you can inspect. */
  onInspect: (name: string) => boolean = () => false;
  /** Starts a whisper to a player (inspect window). */
  whisperTo(name: string): void { this.ch = "Whisper"; this.target = name; this.focus(); }
  /** A system line from another mock (NPC services, teleports). */
  system(text: string): void { this.add("System", text); }

  private ageLines(): void {
    if (!this.opt.fade) return;
    const cut = Date.now() - this.opt.fade * 1000;
    for (const e of this.log.querySelectorAll<HTMLElement>(".ln")) e.classList.toggle("old", +(e.dataset.t ?? 0) < cut);
  }

  private renderLog(): void {
    const tab = this.tabs[this.tab]!;
    const cut = Date.now() - this.opt.fade * 1000;
    const nodes: HTMLElement[] = [];
    for (const l of this.lines) {
      if (!this.shows(tab, l)) continue;
      const plain = (l.from ?? "") + " " + l.text;
      if (this.query && !plain.toLowerCase().includes(this.query)) continue;
      const e = h("div", "ln" + (l.from || l.ch === "Whisper" ? "" : " sys"));
      e.dataset.t = String(l.t);
      if (this.opt.fade && l.t < cut) e.classList.add("old");
      if (l.from && l.from !== ME && new RegExp(`\\b${ME}\\b`, "i").test(l.text)) e.classList.add("mention");
      if (this.query) e.classList.add("hit");
      const c = this.colour(l);
      e.style.color = c;
      if (this.opt.time) e.append(h("span", "tm", fmtTime(l.t)));
      if (l.ch === "Whisper") e.append(h("span", "tg", l.from === ME ? "To" : "From"), this.name(l.from === ME ? l.to! : l.from!, c), ": ");
      else {
        if (this.opt.tags && TAG[l.ch]) e.append(h("span", "tg", `[${TAG[l.ch]}]`));
        if (l.from) e.append(this.name(l.from, c), ": ");
      }
      e.append(...this.rich(l.text));
      if (l.n > 1) e.append(h("span", "x", "×" + l.n));
      nodes.push(e);
    }
    const newp = h("div", "newp", `${this.unseen} new message${this.unseen === 1 ? "" : "s"} ↓`);
    newp.addEventListener("click", () => { this.stick = true; this.unseen = 0; this.log.scrollTop = this.log.scrollHeight; this.renderPill(); });
    newp.hidden = !this.unseen || this.stick;
    const top = this.log.scrollTop;
    this.log.replaceChildren(...nodes);
    this.el.querySelector(".newp")?.remove();
    this.el.append(newp);
    if (this.stick) this.log.scrollTop = this.log.scrollHeight; else this.log.scrollTop = top;
  }
  private renderPill(): void { const p = this.el.querySelector<HTMLElement>(".newp"); if (p) { p.hidden = !this.unseen || this.stick; p.textContent = `${this.unseen} new message${this.unseen === 1 ? "" : "s"} ↓`; } }

  /** Text with [Item] links and the search match marked. */
  private rich(text: string): (Node | string)[] {
    const out: (Node | string)[] = [];
    for (const part of text.split(/(\[[^\]]+\])/)) {
      if (!part) continue;
      const m = /^\[([^\]]+)\]$/.exec(part);
      if (m && LINKABLE.some(([n]) => n === m[1])) {
        const a = h("span", "lk", part);
        a.dataset.item = m[1]!; // hover: the item tooltip (hudTooltip.ts)
        a.addEventListener("click", (e) => { e.stopPropagation(); this.itemCard(m[1]!, a); });
        out.push(a);
      } else if (this.query && part.toLowerCase().includes(this.query)) {
        const i = part.toLowerCase().indexOf(this.query);
        out.push(part.slice(0, i), h("mark", "", part.slice(i, i + this.query.length)), part.slice(i + this.query.length));
      } else out.push(part);
    }
    return out;
  }
  private name(n: string, c: string): HTMLElement {
    const e = h("span", "nm", n);
    e.style.color = c;
    const open = (ev: MouseEvent) => { ev.preventDefault(); ev.stopPropagation(); this.nameMenu(n, e); };
    e.addEventListener("click", open);
    e.addEventListener("contextmenu", open);
    return e;
  }

  // — Tabs —
  private renderTabs(): void {
    const nodes: HTMLElement[] = [];
    this.tabs.forEach((t, i) => {
      const e = h("div", "ct" + (i === this.tab ? " on" : "") + (t.ping && i !== this.tab ? " ping" : ""), t.whisper ? "✉ " + t.name : t.name);
      if (t.unread && i !== this.tab) e.append(h("b", "", t.unread > 99 ? "99+" : String(t.unread)));
      if (!t.fixed) { const x = h("i", "", "×"); x.title = "Close tab"; x.addEventListener("click", (ev) => { ev.stopPropagation(); this.tabs.splice(i, 1); if (this.tab >= i) this.tab = Math.max(0, this.tab - 1); this.renderTabs(); this.renderLog(); }); e.append(x); }
      e.addEventListener("click", () => {
        this.tab = i; t.unread = 0; t.ping = false; this.stick = true; this.unseen = 0;
        if (t.whisper) { this.ch = "Whisper"; this.target = t.whisper; this.updatePill(); }
        this.renderTabs(); this.renderLog();
      });
      if (!t.fixed && !t.whisper) e.addEventListener("dblclick", () => {
        const inp = h("input"); inp.value = t.name;
        inp.addEventListener("keydown", (ev) => { ev.stopPropagation(); if (ev.key === "Enter" || ev.key === "Escape") inp.blur(); });
        inp.addEventListener("blur", () => { t.name = inp.value.trim() || t.name; this.renderTabs(); });
        e.replaceChildren(inp); inp.focus(); inp.select();
      });
      nodes.push(e);
    });
    const plus = h("div", "ib", svg(I.plus)); plus.title = "New tab (pick its channels in settings)";
    plus.addEventListener("click", () => {
      this.tabs.push({ id: this.nextId++, name: `Tab ${this.tabs.filter((t) => !t.fixed && !t.whisper).length + 1}`, chans: new Set(["Normal", "Group", "Guild"]), unread: 0, ping: false, fixed: false });
      this.tab = this.tabs.length - 1; this.renderTabs(); this.renderLog(); this.settings();
    });
    const search = h("div", "ib" + (this.srch.classList.contains("on") ? " on" : ""), svg(I.search)); search.title = "Search";
    search.addEventListener("click", () => { const on = this.srch.classList.toggle("on"); if (on) this.srch.querySelector("input")!.focus(); else { this.query = ""; this.renderLog(); } this.renderTabs(); this.refreshAct(); });
    const lock = h("div", "ib" + (this.opt.lock ? " on" : ""), svg(this.opt.lock ? I.lock : I.unlock)); lock.title = this.opt.lock ? "Unlock position" : "Lock position";
    lock.addEventListener("click", () => { this.opt.lock = !this.opt.lock; this.applyOpts(); this.renderTabs(); });
    const gear = h("div", "ib", svg(I.gear)); gear.title = "Chat settings";
    gear.addEventListener("click", (e) => { e.stopPropagation(); this.settings(); });
    const strip = h("div", "tstrip", ...nodes, plus);
    strip.addEventListener("wheel", (e) => { strip.scrollLeft += e.deltaY; e.preventDefault(); }, { passive: false });
    this.tabBar.replaceChildren(strip, search, lock, gear);
    const on = strip.querySelector<HTMLElement>(".ct.on");
    if (on && (on.offsetLeft < strip.scrollLeft || on.offsetLeft + on.offsetWidth > strip.scrollLeft + strip.clientWidth)) strip.scrollLeft = on.offsetLeft - 4;
  }

  // — Input —
  private updatePill(): void {
    const sp = SPEAK.find((s) => s[0] === this.ch) ?? SPEAK[0]!;
    this.pill.textContent = this.ch === "Whisper" ? `To ${this.target || "…"}` : sp[2];
    this.pill.style.color = CLASSIC[this.ch];
    this.input.placeholder = this.ch === "Whisper" ? `Whisper to ${this.target || "(type /name then space)"}` : `Message ${sp[2].toLowerCase() === "say" ? "nearby players" : sp[2].toLowerCase()}…  (@ for commands)`;
    this.cnt.textContent = `${this.input.value.length}/200`;
    this.cnt.classList.toggle("hi", this.input.value.length > 180);
  }
  private onInput(): void {
    const v = this.input.value;
    // Prefix then space switches the channel pill (the server's prefixes, PlayerObject.Chat).
    const w = /^\/(\S+) $/.exec(v);
    if (w) { this.ch = "Whisper"; this.target = w[1]!; this.input.value = ""; }
    else for (const [ch, pre] of SPEAK) if (pre && pre !== "/" && v === pre + " ") { this.ch = ch; this.input.value = ""; break; }
    this.updatePill();
    this.suggest();
  }
  private suggest(): void {
    const v = this.input.value;
    const items: [PopLabel, () => void][] = [];
    let head = "";
    if (/^@\S*$/.test(v)) {
      head = "Commands";
      for (const [c, d] of COMMANDS) if (c.startsWith(v.toLowerCase())) items.push([[h("code", "", c), h("small", "", d)], () => { this.input.value = c + " "; this.closePop(); this.updatePill(); }]);
    } else if (/^\/\S*$/.test(v)) {
      head = "Whisper to";
      const q = v.slice(1).toLowerCase();
      for (const n of NAMES) if (n.toLowerCase().startsWith(q)) items.push([[n, h("small", "", ONLINE.has(n) ? "online" : "offline")], () => { this.ch = "Whisper"; this.target = n; this.input.value = ""; this.closePop(); this.updatePill(); }]);
    }
    if (!items.length) { if (this.pop?.dataset.kind === "ac") this.closePop(); return; }
    this.openPop(head, items.slice(0, 8), this.input, "ac");
  }
  private onKey(e: KeyboardEvent): void {
    e.stopPropagation();
    if (this.pop && this.popItems.length && ["ArrowUp", "ArrowDown", "Tab"].includes(e.key) || (this.pop && e.key === "Enter" && this.pop.dataset.kind === "ac")) {
      e.preventDefault();
      if (e.key === "ArrowUp") this.popSel = (this.popSel + this.popItems.length - 1) % this.popItems.length;
      else if (e.key === "ArrowDown") this.popSel = (this.popSel + 1) % this.popItems.length;
      else { this.popItems[this.popSel]![1](); return; }
      this.pop.querySelectorAll(".it").forEach((x, i) => x.classList.toggle("on", i === this.popSel));
      return;
    }
    if (e.key === "Escape") { e.preventDefault(); if (this.pop) { this.closePop(); return; } this.input.value = ""; this.input.blur(); this.el.classList.remove("typing"); this.refreshAct(); return; }
    if (e.key === "Enter") { e.preventDefault(); this.send(); return; }
    if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      if (!this.hist.length) return;
      e.preventDefault();
      this.histAt = e.key === "ArrowUp" ? (this.histAt < 0 ? this.hist.length - 1 : Math.max(0, this.histAt - 1)) : this.histAt < 0 ? -1 : this.histAt + 1;
      if (this.histAt >= this.hist.length) this.histAt = -1;
      this.input.value = this.histAt < 0 ? "" : this.hist[this.histAt]!;
      this.updatePill();
    }
    if (e.key === "Backspace" && !this.input.value && this.ch !== "Normal") { this.ch = "Normal"; this.target = ""; this.updatePill(); }
  }

  private send(): void {
    let text = this.input.value.trim();
    if (!text) { this.input.blur(); this.el.classList.remove("typing"); this.refreshAct(); return; }
    this.hist.push(text); if (this.hist.length > 30) this.hist.shift(); this.histAt = -1;
    this.input.value = ""; this.closePop();
    let ch = this.ch, to = this.target;
    // Prefixes typed without the space still count (PlayerObject.Chat order: /, !!, !~, !#, !, :), @).
    const w = /^\/(\S+)\s+(.+)$/.exec(text);
    if (w) { ch = "Whisper"; to = w[1]!; text = w[2]!; }
    else if (text.startsWith("@")) { this.command(text); this.updatePill(); return; }
    else for (const [c, pre] of [["Group", "!!"], ["Guild", "!~"], ["Mentor", "!#"], ["Shout", "!"], ["Lover", ":)"]] as [Ch, string][]) if (text.startsWith(pre)) { ch = c; text = text.slice(pre.length).trim(); break; }
    if (!text) return;
    if (ch === "Whisper") {
      if (!to) { this.sys("Type /name and a space to pick who to whisper."); return; }
      if (!ONLINE.has(to)) { this.add("Whisper", text, ME, to); this.sys(`${to} is not online.`); return; }
      this.add("Whisper", text, ME, to);
      window.setTimeout(() => this.add("Whisper", ["sure!", "on my way", "haha yes", "give me 5 min", "thanks!"][Math.floor(Math.random() * 5)]!, to), 1500 + Math.random() * 2000);
    } else {
      this.add(ch, text, ME);
      const replyFrom = ch === "Group" ? "Seren" : ch === "Guild" ? "Aldric" : ch === "Mentor" ? "Wren" : ch === "Lover" ? "Seren" : null;
      if (replyFrom && Math.random() < .7) window.setTimeout(() => this.add(ch, ["ok!", "got it", "nice", "on it", "♥"][Math.floor(Math.random() * 5)]!, replyFrom), 1600);
    }
    this.stick = true; this.unseen = 0; this.renderLog(); this.updatePill();
  }

  /** Player @commands (PlayerObject.Chat), answered with mock system lines. */
  private command(text: string): void {
    const [c, ...args] = text.slice(1).split(/\s+/);
    switch ((c ?? "").toLowerCase()) {
      case "allowtrade": this.allowTrade = !this.allowTrade; this.sys(this.allowTrade ? "You are now allowing trade requests." : "You are now refusing trade requests."); break;
      case "allowguild": this.sys("You are now allowing guild invites."); break;
      case "allowobserve": this.sys("Other players can now observe you."); break;
      case "time": this.sys(`The time is ${new Date().toLocaleTimeString("en-GB")}.`); break;
      case "roll": this.add("Normal", `rolls ${1 + Math.floor(Math.random() * 6)} (1-6)`, ME); break;
      case "map": this.sys("You are on Bichon Province (0)."); break;
      case "ride": this.sys("You mount your WhiteTiger."); break;
      case "recalllover": this.sys("Seren was recalled to your side."); break;
      case "grouprecall": this.sys("Your group was recalled."); break;
      case "enablegrouprecall": this.sys("Group recall is now allowed."); break;
      case "toggletransform": this.sys("Your transform is now hidden."); break;
      case "leaveguild": this.sys("You can't leave the guild while you are its leader."); break;
      default: this.sys(`Unknown command @${c}${args.length ? " " + args.join(" ") : ""}.`); break;
    }
  }

  // — Popups —
  private openPop(head: string, items: [PopLabel, () => void][], anchor: HTMLElement, kind: string): void {
    this.closePop();
    this.popItems = items; this.popSel = 0;
    const p = h("div", "pop");
    p.dataset.kind = kind;
    if (head) p.append(h("div", "hd", head));
    items.forEach(([label, fn], i) => {
      if (label === "-") { p.append(h("div", "sep")); return; }
      // Labels are text or nodes, never HTML: names and item names come from typed input and data.
      const it = h("div", "it" + (i === 0 && kind === "ac" ? " on" : ""), ...(typeof label === "string" ? [label] : label));
      it.addEventListener("mousedown", (e) => e.preventDefault());
      it.addEventListener("click", () => { fn(); });
      p.append(it);
    });
    this.el.append(p);
    this.pop = p;
    const er = this.el.getBoundingClientRect(), ar = anchor.getBoundingClientRect(), k = this.scale();
    p.style.left = Math.max(0, (ar.left - er.left) / k) + "px";
    p.style.bottom = (er.bottom - ar.top) / k + 4 + "px";
    this.refreshAct();
  }
  private closePop(): void { this.pop?.remove(); this.pop = null; this.popItems = []; this.el.querySelector(".card")?.remove(); this.refreshAct(); }

  private channelMenu(): void {
    this.openPop("Talk in", SPEAK.map(([ch, pre, label]): [PopLabel, () => void] => [[coloured("●", CLASSIC[ch]), ` ${label}`, h("small", "", pre ? pre + (ch === "Whisper" ? "name" : "") : "no prefix")], () => {
      this.ch = ch; if (ch !== "Whisper") this.target = ""; this.closePop(); this.updatePill(); this.input.focus();
      if (ch === "Whisper" && !this.target) { this.input.value = "/"; this.suggest(); }
    }]), this.pill, "menu");
  }
  private nameMenu(n: string, anchor: HTMLElement): void {
    const act = (text: string) => () => { this.closePop(); this.sys(text); };
    const items: [PopLabel, () => void][] = n === ME ? [["That's you!", () => this.closePop()]] : [
      ["Whisper", () => { this.closePop(); this.ch = "Whisper"; this.target = n; this.focus(); }],
      ["Invite to group", act(`Group invite sent to ${n}.`)], ["Add friend", act(`${n} was added to your friends.`)], ["Inspect", () => { this.closePop(); if (!this.onInspect(n)) this.sys(`${n} is too far away to inspect.`); }],
      ["Copy name", () => { void navigator.clipboard?.writeText(n).catch(() => {}); act(`Copied "${n}".`)(); }], ["-", () => {}],
      [[coloured(`Block ${n}`, "#ff8a6a")], () => { this.blocked.add(n); this.closePop(); this.sys(`You blocked ${n}. Their messages are hidden.`); this.renderLog(); }],
    ];
    if (this.blocked.has(n)) items.push([`Unblock ${n}`, () => { this.blocked.delete(n); this.closePop(); this.renderLog(); }]);
    this.openPop(n, items, anchor, "menu");
  }
  private itemMenu(anchor: HTMLElement): void {
    this.openPop("Link an item from your bag", LINKABLE.map(([n, img]): [PopLabel, () => void] => {
      const ic = h("span", "ic");
      void libPicture(Lib.Items, img).then((p) => { if (p) ic.style.backgroundImage = `url(${p.url})`; });
      return [[ic, n], () => { const v = this.input.value; this.input.value = (v && !v.endsWith(" ") ? v + " " : v) + `[${n}] `; this.closePop(); this.input.focus(); this.updatePill(); }];
    }), anchor, "menu");
  }
  private itemCard(n: string, anchor: HTMLElement): void {
    this.closePop();
    const img = LINKABLE.find((x) => x[0] === n)?.[1] ?? -1;
    const ic = h("div", "ic");
    void libPicture(Lib.Items, img).then((p) => { if (p) ic.style.backgroundImage = `url(${p.url})`; });
    const c = h("div", "card", ic, h("div", "", h("b", "", n), h("small", "", "Linked item · full tooltip comes with the item tooltip mock")));
    this.el.append(c);
    const er = this.el.getBoundingClientRect(), ar = anchor.getBoundingClientRect(), k = this.scale();
    c.style.left = Math.max(0, (ar.left - er.left) / k) + "px";
    c.style.bottom = (er.bottom - ar.top) / k + 4 + "px";
    window.setTimeout(() => c.remove(), 4000);
  }

  private settings(): void {
    this.closePop();
    const p = h("div", "pop set");
    p.dataset.kind = "set";
    const sw = (on: boolean, fn: () => void) => { const s = h("div", "sw" + (on ? " on" : "")); s.addEventListener("click", () => { fn(); this.applyOpts(); this.renderLog(); this.settings(); }); return s; };
    const seg = <T>(vals: [T, string][], cur: T, fn: (v: T) => void) => {
      const s = h("div", "seg");
      for (const [v, l] of vals) { const d = h("div", v === cur ? "on" : "", l); d.addEventListener("click", () => { fn(v); this.applyOpts(); this.renderLog(); this.settings(); }); s.append(d); }
      return s;
    };
    const op = h("input"); op.type = "range"; op.min = "0"; op.max = "0.9"; op.step = "0.05"; op.value = String(this.opt.op);
    op.addEventListener("input", () => { this.opt.op = +op.value; this.applyOpts(); });
    const tab = this.tabs[this.tab]!;
    const chs = h("div", "chs");
    for (const c of ALL) {
      const cb = h("input"); cb.type = "checkbox"; cb.checked = tab.chans.has(c); cb.disabled = !!tab.whisper;
      cb.addEventListener("change", () => { if (cb.checked) tab.chans.add(c); else tab.chans.delete(c); this.renderLog(); });
      const lb = h("label", "", cb, c === "Normal" ? "Say" : c === "LevelUp" ? "Level up" : c); lb.style.color = CLASSIC[c];
      chs.append(lb);
    }
    p.append(
      h("div", "r", "Background", op),
      h("div", "r", "Fade lines after", seg<number>([[10, "10s"], [20, "20s"], [60, "1m"], [0, "Never"]], this.opt.fade, (v) => { this.opt.fade = v; })),
      h("div", "r", "Text size", seg<number>([[11, "S"], [12, "M"], [14, "L"]], this.opt.fs, (v) => { this.opt.fs = v; })),
      h("div", "r", "Channel colours", seg<boolean>([[false, "Classic"], [true, "Theme"]], this.opt.theme, (v) => { this.opt.theme = v; })),
      h("div", "r", "Timestamps", sw(this.opt.time, () => { this.opt.time = !this.opt.time; })),
      h("div", "r", "Channel tags", sw(this.opt.tags, () => { this.opt.tags = !this.opt.tags; })),
      h("div", "r", "Live traffic (mock)", sw(this.opt.live, () => { this.opt.live = !this.opt.live; })),
      h("div", "r", "Lock position", sw(this.opt.lock, () => { this.opt.lock = !this.opt.lock; this.renderTabs(); })),
      h("div", "hd", `“${tab.name}” shows`), tab.whisper ? h("div", "", `Whispers with ${tab.whisper}`) : chs,
    );
    if (this.blocked.size) {
      const ub = h("div", "it", `Unblock all (${this.blocked.size})`);
      ub.addEventListener("click", () => { this.blocked.clear(); this.renderLog(); this.settings(); });
      p.append(ub);
    }
    p.addEventListener("mousedown", (e) => e.stopPropagation());
    this.el.append(p);
    this.pop = p;
    p.style.right = "0"; p.style.bottom = "100%";
    this.refreshAct();
  }

  // — Simulation and ticker —
  private simulate(): void {
    const [ch, from, text] = TRAFFIC[Math.floor(Math.random() * TRAFFIC.length)]!;
    if (Math.random() < 0.08) { this.add("Announcement", ["Guild war: Nightfall vs GameMaster starts at 20:00!", "Server maintenance in 30 minutes.", "The Zuma Temple gates are open!"][Math.floor(Math.random() * 3)]!); return; }
    if (Math.random() < 0.1) { this.add(ch === "Group" ? "Group" : "Normal", `GMRyu, ${["can you help?", "nice drop!", "where are you?"][Math.floor(Math.random() * 3)]}`, NAMES[Math.floor(Math.random() * 6)]); return; }
    this.add(ch, text, from ?? undefined, ch === "Whisper" ? undefined : undefined);
  }
  private ticker(text: string): void {
    const s = h("span", "", h("b", "", "NOTICE"), text);
    const dur = 6 + text.length * 0.12;
    s.style.setProperty("--dur", dur + "s");
    this.notice.hidden = false;
    this.notice.replaceChildren(s);
    s.addEventListener("animationend", () => { if (s.isConnected) this.notice.hidden = true; });
  }
}
