// Ranking window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// RankingDialog (Overall and one board per class, ranked by level then experience): a podium for the top three, the
// board with movement since yesterday (▲ / ▼), online dots, guild tags and an EXP-to-next-level bar, your own row
// pinned at the bottom with "Find me", search, an online-only filter, a Guilds board, and a profile card for the picked
// player (class, level, guild, best rank, recent movement as a sparkline, Whisper / Inspect / Add friend). Mock data:
// made-up names, never real players.

import { canon } from "./hudPeople.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

type Board = "Overall" | "Warrior" | "Wizard" | "Taoist" | "Assassin" | "Archer" | "Guilds";
interface Player { name: string; cls: string; level: number; exp: number; guild: string; online: boolean; trend: number[]; best: number }
const CLASSES = ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"];
const CLS: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };
const NAMES = ["Valkyrion", "Moonshade", "Grimtooth", "Ashenveil", "Kaelith", "Stormcaller", "Ironbark", "Nightbloom", "Seren", "Thorne", "Brannoc", "Vexa", "Duskrunner", "Embergale",
  "Frostwhisper", "Hollowfang", "Lumen", "Mirelle", "Nox", "Oakenshield", "Pyralis", "Quillon", "Ravenna", "Silverbrook", "Talon", "Umbra", "Vesper", "Wren", "Xyra", "Yorick",
  "Zephyra", "Corvin", "Isolde", "Halvard", "Pip", "Nyra", "Ulric", "Talia", "Soren", "Rhea"];
const GUILDS = ["GameMaster", "Nightfall", "Lionheart", "Silver Dawn", "Red Lotus", "—"];
let r = 7;
const rnd = () => (r = (r * 9301 + 49297) % 233280) / 233280;
const PLAYERS: Player[] = NAMES.map((name, i) => {
  const level = Math.max(22, 61 - Math.floor(i * 0.9) - Math.floor(rnd() * 3));
  const trend = Array.from({ length: 7 }, () => Math.round((rnd() - 0.45) * 4));
  return { name, cls: CLASSES[i % 5]!, level, exp: rnd(), guild: GUILDS[Math.floor(rnd() * GUILDS.length)]!, online: rnd() > 0.45, trend, best: Math.max(1, i + 1 - Math.floor(rnd() * 5)) };
});
// You
PLAYERS.push({ name: "GMRyu", cls: "Warrior", level: 42, exp: 0.634, guild: "GameMaster", online: true, trend: [2, 1, 0, 3, -1, 2, 4], best: 31 });
const GUILD_BOARD: [string, number, number, string, number][] = [["Nightfall", 14, 58, "Garrick", 1], ["GameMaster", 12, 38, "GMRyu", 2], ["Lionheart", 10, 44, "Ironbark", -1], ["Silver Dawn", 8, 31, "Moonshade", 0],
  ["Red Lotus", 7, 25, "Ashenveil", 1], ["Iron Covenant", 6, 22, "Talon", -2], ["Eclipse", 5, 19, "Umbra", 0], ["Wanderers", 3, 12, "Pip", 3]];
const RESET_DAYS = 4;

const CSS = `
.hudm .rwin { --winh: 600px; width: 900px; }
.hudm .rwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .rwin .tabs { display: flex; gap: 2px; padding: 8px 14px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); align-items: flex-end; }
.hudm .rwin .tabs div { padding: 7px 14px; cursor: pointer; font-size: 12px; font-weight: 600; color: var(--dim); border-radius: var(--rad) var(--rad) 0 0; border: 1px solid transparent; border-bottom: 0; }
.hudm .rwin .tabs div:hover { color: var(--tx); }
.hudm .rwin .tabs div.on { color: #fff; background: color-mix(in srgb, var(--ac) 16%, transparent); border-color: color-mix(in srgb, var(--ac) 50%, transparent); box-shadow: inset 0 2px 0 var(--ac); }
.hudm .rwin .tabs .season { margin-left: auto; padding: 0 0 8px; font-size: 11px; color: var(--dim); cursor: default; border: 0; background: none; box-shadow: none; }
.hudm .rwin .tabs .season b { color: var(--ac); }
.hudm .rwin .rbody { flex: 1; min-height: 0; display: flex; }
.hudm .rwin .board { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 10px 12px; gap: 8px; min-height: 0; }
/* Podium */
.hudm .rwin .podium { display: grid; grid-template-columns: 1fr 1.15fr 1fr; gap: 10px; align-items: end; flex: none; }
.hudm .rwin .pod { position: relative; text-align: center; cursor: pointer; padding: 10px 8px 8px; border-radius: var(--rad) var(--rad) 0 0;
  background: linear-gradient(180deg, color-mix(in srgb, var(--m) 22%, transparent), color-mix(in srgb, var(--pb) 70%, transparent)); border: 1px solid color-mix(in srgb, var(--m) 60%, transparent); border-bottom-width: 3px; }
.hudm .rwin .pod:hover { box-shadow: 0 0 16px color-mix(in srgb, var(--m) 50%, transparent); }
.hudm .rwin .pod.p1 { --m: #f2c94c; padding-top: 18px; } .hudm .rwin .pod.p2 { --m: #c9d3e0; } .hudm .rwin .pod.p3 { --m: #d08a4a; }
.hudm .rwin .pod .medal { position: absolute; top: -13px; left: 50%; transform: translateX(-50%); width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  font-size: 12px; font-weight: 900; color: #1a1a1a; background: radial-gradient(circle at 35% 35%, #fff, var(--m)); box-shadow: 0 0 10px var(--m), 0 0 0 2px #000; }
.hudm .rwin .pod .badge { width: 46px; height: 46px; margin: 6px auto 6px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--m); box-shadow: 0 0 12px color-mix(in srgb, var(--m) 50%, transparent); }
.hudm .rwin .pod.p1 .badge { width: 56px; height: 56px; }
.hudm .rwin .pod b { display: block; font-size: 14px; color: #fff; } .hudm .rwin .pod span { font-size: 11px; color: var(--dim); }
.hudm .rwin .pod .lv { margin-top: 3px; font: 700 18px Georgia, serif; color: var(--m); text-shadow: 0 0 8px color-mix(in srgb, var(--m) 50%, transparent); }
/* Table */
.hudm .rwin .tools { display: flex; gap: 8px; align-items: center; }
.hudm .rwin input.in { height: 26px; width: 200px; box-sizing: border-box; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .rwin input.in:focus { border-color: var(--ac); } .hudm .rwin input.in::placeholder { color: var(--dim); }
.hudm .rwin .opt { display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--dim); }
.hudm .rwin .tog { width: 32px; height: 18px; border-radius: 9px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); }
.hudm .rwin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 12px; height: 12px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .rwin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); } .hudm .rwin .tog.on::after { left: 16px; background: #fff; }
.hudm .rwin .tools .btn { margin-left: auto; height: 26px; }
.hudm .rwin .rows { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .rwin .rrow { display: grid; grid-template-columns: 44px 30px 1fr 110px 60px 120px 46px; align-items: center; gap: 8px; height: 34px; padding: 0 8px; cursor: pointer; border-radius: var(--rad); font-size: 12px; }
.hudm .rwin .rrow:nth-child(odd) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rwin .rrow:hover { background: color-mix(in srgb, var(--ac) 9%, transparent); }
.hudm .rwin .rrow.sel { background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .rwin .rrow.me { background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .rwin .rrow.flash { animation: hudm-rflash 1.2s ease-out; }
@keyframes hudm-rflash { 0%, 40% { background: color-mix(in srgb, var(--ac) 45%, transparent); } }
.hudm .rwin .rk { font: 700 14px Georgia, serif; color: var(--tx); text-align: right; }
.hudm .rwin .rrow.top .rk { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .rwin .cb { width: 26px; height: 26px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 8px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); position: relative; }
.hudm .rwin .cb.on::after { content: ""; position: absolute; right: -2px; bottom: -2px; width: 8px; height: 8px; border-radius: 50%; background: #4fe07a; border: 2px solid #000; }
.hudm .rwin .nm { min-width: 0; } .hudm .rwin .nm b { color: #fff; font-weight: 600; } .hudm .rwin .nm small { margin-left: 6px; color: var(--dim); font-size: 10px; }
.hudm .rwin .gtag { font-size: 10px; color: var(--acd); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .rwin .lvl { font-weight: 700; color: var(--tx); text-align: center; font-variant-numeric: tabular-nums; }
.hudm .rwin .xp { height: 5px; border-radius: 3px; background: #04060a; overflow: hidden; } .hudm .rwin .xp i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .rwin .mv { font-size: 11px; font-weight: 700; text-align: right; font-variant-numeric: tabular-nums; }
.hudm .rwin .mv.up { color: #4fe07a; } .hudm .rwin .mv.down { color: #ff6a5a; } .hudm .rwin .mv.same { color: var(--dim); }
.hudm .rwin .pin { flex: none; border-top: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); padding-top: 6px; }
.hudm .rwin .head { display: grid; grid-template-columns: 44px 30px 1fr 110px 60px 120px 46px; gap: 8px; padding: 0 8px; font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .rwin .head span:first-child { text-align: right; } .hudm .rwin .head span:nth-child(5) { text-align: center; } .hudm .rwin .head span:last-child { text-align: right; }
/* Profile */
.hudm .rwin .side { width: 250px; flex: none; padding: 12px; display: flex; flex-direction: column; gap: 10px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rwin .prof { text-align: center; }
.hudm .rwin .prof .cb { width: 64px; height: 64px; margin: 4px auto 8px; font-size: 14px; border: 2px solid var(--ac); box-shadow: 0 0 calc(12px * var(--gl)) var(--glow); }
.hudm .rwin .prof .cb.on::after { width: 12px; height: 12px; }
.hudm .rwin .prof h3 { margin: 0; font-size: 18px; color: #fff; } .hudm .rwin .prof .sub { font-size: 11px; color: var(--dim); }
.hudm .rwin .big { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .rwin .big div { padding: 8px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--frl) 6%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .rwin .big b { display: block; font: 700 20px Georgia, serif; color: #fff; } .hudm .rwin .big span { font-size: 10px; color: var(--dim); }
.hudm .rwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .rwin .spark { width: 100%; height: 50px; }
.hudm .rwin .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.hudm .rwin .acts .btn { height: 26px; justify-content: center; }
.hudm .rwin .note { font-size: 11px; color: var(--ac); min-height: 14px; text-align: center; }
/* Guild board */
.hudm .rwin .grow { display: grid; grid-template-columns: 44px 34px 1fr 70px 70px 120px 46px; align-items: center; gap: 8px; height: 40px; padding: 0 8px; border-radius: var(--rad); font-size: 12px; }
.hudm .rwin .grow:nth-child(odd) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .rwin .grow.me { box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 12%, transparent); }
.hudm .rwin .grow .sh { width: 28px; height: 32px; }
`;

export class RankingMock extends HudWindow {
  private board: Board = "Overall";
  private query = "";
  private onlineOnly = false;
  private picked = "GMRyu";
  private flash = "";
  private note = "";

  constructor() {
    super("rwin", 62, 30);
    injectCss("hudm-ranking-css", CSS);
  }

  protected override shown(): void { this.render(); }

  /** The board's full order (level, then experience), then the filters for display. */
  private ranked(): Player[] {
    for (const p of PLAYERS) canon(p); // known names and the player: hudPeople.ts
    return PLAYERS.filter((p) => this.board === "Overall" || p.cls === this.board).sort((a, b) => b.level - a.level || b.exp - a.exp);
  }

  private render(): void {
    const tabs = h("div", "tabs");
    for (const b of ["Overall", ...CLASSES, "Guilds"] as Board[]) {
      const t = h("div", b === this.board ? "on" : "", b);
      t.addEventListener("click", () => { this.board = b; this.query = ""; this.render(); });
      tabs.append(t);
    }
    tabs.append(h("div", "season", "Season resets in ", h("b", "", `${RESET_DAYS} days`)));
    const body = h("div", "rbody", this.board === "Guilds" ? this.guilds() : this.players(), this.side());
    this.el.replaceChildren(h("div", "panel", this.titleBar("RANKING", "Close (K)"), tabs, body));
    if (this.flash) {
      const row = this.el.querySelector<HTMLElement>(`.rows .rrow[data-n="${this.flash}"]`);
      row?.scrollIntoView({ block: "center" });
      row?.classList.add("flash");
      this.flash = "";
    }
  }

  private players(): HTMLElement {
    const all = this.ranked();
    const rankOf = (p: Player) => all.indexOf(p) + 1;
    // Podium
    const pod = h("div", "podium");
    for (const [pos, cls] of [[2, "p2"], [1, "p1"], [3, "p3"]] as [number, string][]) {
      const p = all[pos - 1];
      if (!p) { pod.append(h("div", "")); continue; }
      const d = h("div", "pod " + cls, h("div", "medal", String(pos)), h("div", "badge", CLS[p.cls] ?? ""), h("b", "", p.name), h("span", "", `${p.cls} · ${p.guild}`), h("div", "lv", `Lv ${p.level}`));
      d.addEventListener("click", () => { this.picked = p.name; this.render(); });
      pod.append(d);
    }
    const search = h("input", "in");
    search.placeholder = "Find a player…"; search.value = this.query;
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim().toLowerCase(); fill(); });
    const tog = h("div", "tog" + (this.onlineOnly ? " on" : ""));
    tog.addEventListener("click", () => { this.onlineOnly = !this.onlineOnly; this.render(); });
    const me = PLAYERS.find((p) => p.name === "GMRyu")!;
    const find = h("div", "btn", "Find me");
    find.addEventListener("click", () => { this.query = ""; this.onlineOnly = false; this.picked = me.name; this.flash = me.name; this.render(); });
    const rows = h("div", "rows");
    const row = (p: Player) => {
      const move = p.trend[p.trend.length - 1]!;
      const xp = h("div", "xp", h("i")); (xp.firstChild as HTMLElement).style.width = (p.exp * 100).toFixed(0) + "%";
      const rk = rankOf(p);
      const el = h("div", "rrow" + (rk <= 3 ? " top" : "") + (p.name === "GMRyu" ? " me" : "") + (p.name === this.picked ? " sel" : ""),
        h("span", "rk", `#${rk}`), h("div", "cb" + (p.online ? " on" : ""), CLS[p.cls] ?? ""), h("div", "nm", h("b", "", p.name), h("small", "", p.cls)), h("span", "gtag", p.guild),
        h("span", "lvl", String(p.level)), xp, h("span", "mv " + (move > 0 ? "up" : move < 0 ? "down" : "same"), move > 0 ? `▲${move}` : move < 0 ? `▼${-move}` : "–"));
      el.dataset.n = p.name;
      el.addEventListener("click", () => { this.picked = p.name; this.render(); });
      return el;
    };
    const fill = () => {
      rows.replaceChildren();
      for (const p of all) {
        if (this.onlineOnly && !p.online) continue;
        if (this.query && !p.name.toLowerCase().includes(this.query) && !p.guild.toLowerCase().includes(this.query)) continue;
        rows.append(row(p));
      }
      if (!rows.childElementCount) rows.append(h("div", "rrow", h("span", ""), h("span", ""), h("span", "gtag", "Nobody matches.")));
    };
    fill();
    const parts: HTMLElement[] = [pod, h("div", "tools", search, h("div", "opt", tog, "Online only"), find),
      h("div", "head", h("span", "", "Rank"), h("span", ""), h("span", "", "Player"), h("span", "", "Guild"), h("span", "", "Level"), h("span", "", "EXP"), h("span", "", "24 h")), rows];
    if (all.includes(me)) parts.push(h("div", "pin", row(me)));
    return h("div", "board", ...parts);
  }

  private guilds(): HTMLElement {
    const rows = h("div", "rows");
    GUILD_BOARD.forEach(([name, lv, members, leader, move], i) => {
      const shield = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      shield.setAttribute("viewBox", "0 0 28 32"); shield.setAttribute("class", "sh");
      shield.innerHTML = `<path d="M14 1l12 4v10c0 8-5 13-12 16C7 28 2 23 2 15V5z" style="fill: color-mix(in srgb, var(--ac) ${30 - i * 3}%, var(--pb)); stroke: var(--ac)" stroke-width="1.5"/><text x="14" y="20" text-anchor="middle" font-size="11" font-weight="700" fill="#fff"></text>`;
      shield.querySelector("text")!.textContent = name[0] ?? ""; // a guild name is player data: never markup
      rows.append(h("div", "grow" + (name === "GameMaster" ? " me" : ""), h("span", "rk", `#${i + 1}`), shield, h("div", "nm", h("b", "", name), h("small", "", `led by ${leader}`)),
        h("span", "lvl", `Lv ${lv}`), h("span", "gtag", `${members} members`), h("div", "xp", Object.assign(h("i"), { style: `width: ${30 + ((i * 37) % 60)}%` })),
        h("span", "mv " + (move > 0 ? "up" : move < 0 ? "down" : "same"), move > 0 ? `▲${move}` : move < 0 ? `▼${-move}` : "–")));
    });
    return h("div", "board", h("div", "head", h("span", "", "Rank"), h("span", ""), h("span", "", "Guild"), h("span", "", "Level"), h("span", "", "Size"), h("span", "", "EXP"), h("span", "", "Week")), rows);
  }

  private side(): HTMLElement {
    if (this.board === "Guilds") {
      return h("div", "side", h("div", "prof", h("div", "cb", "GM"), h("h3", "", "GameMaster"), h("div", "sub", "Your guild · rank #2")),
        h("div", "big", h("div", "", h("b", "", "#2"), h("span", "", "guild rank")), h("div", "", h("b", "", "12"), h("span", "", "guild level"))),
        h("div", "note", "Guild ranks are by guild level, then guild EXP (mock)."));
    }
    const all = this.ranked();
    // The picked player when they are on this board, else its leader (a Warrior picked on Overall is not on Wizard).
    const p = all.find((x) => x.name === this.picked) ?? all[0]!;
    const rank = all.indexOf(p) + 1;
    // Sparkline of the last 7 days' rank change (cumulative).
    const w = 226, hgt = 50;
    let acc = 0;
    const pts = p.trend.map((d) => (acc += d));
    const min = Math.min(0, ...pts), max = Math.max(1, ...pts);
    const xy = pts.map((v, i) => [i / (pts.length - 1) * (w - 8) + 4, hgt - 6 - (v - min) / (max - min || 1) * (hgt - 12)] as [number, number]);
    const spark = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    spark.setAttribute("viewBox", `0 0 ${w} ${hgt}`); spark.setAttribute("class", "spark");
    spark.innerHTML = `<path d="M${xy.map((q) => q.join(" ")).join("L")}L${w - 4} ${hgt}L4 ${hgt}Z" style="fill: color-mix(in srgb, var(--ac) 18%, transparent)"/>`
      + `<path d="M${xy.map((q) => q.join(" ")).join("L")}" fill="none" style="stroke: var(--ac)" stroke-width="2" stroke-linejoin="round"/>`
      + xy.map(([x, y], i) => `<circle cx="${x}" cy="${y}" r="${i === xy.length - 1 ? 3.5 : 2}" style="fill: ${i === xy.length - 1 ? "#fff" : "var(--ac)"}"/>`).join("");
    const act = (label: string, msg: string) => { const b = h("div", "btn", label); b.addEventListener("click", () => { this.note = msg; this.render(); }); return b; };
    const isMe = p.name === "GMRyu";
    return h("div", "side",
      h("div", "prof", h("div", "cb" + (p.online ? " on" : ""), CLS[p.cls] ?? ""), h("h3", "", p.name), h("div", "sub", `${p.cls} · ${p.guild} · ${p.online ? "online" : "offline"}`)),
      h("div", "big", h("div", "", h("b", "", `#${rank}`), h("span", "", `${this.board.toLowerCase()} rank`)), h("div", "", h("b", "", String(p.level)), h("span", "", `level · ${Math.round(p.exp * 100)}%`)),
        h("div", "", h("b", "", `#${p.best}`), h("span", "", "best this season")), h("div", "", h("b", "", `${pts[pts.length - 1]! >= 0 ? "▲" : "▼"}${Math.abs(pts[pts.length - 1]!)}`), h("span", "", "this week"))),
      h("div", "", h("div", "lbl", "Last 7 days"), spark),
      isMe ? h("div", "note", "That's you! Keep climbing.") : h("div", "acts", act("Whisper", `/w ${p.name} `), act("Inspect", `Inspecting ${p.name}… (mock)`), act("Add friend", `Friend request sent to ${p.name}.`), act("Group invite", `Group invite sent to ${p.name}.`)),
      h("div", "note", this.note));
  }
}
