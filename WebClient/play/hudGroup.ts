// Group window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// GroupDialog around Crystal's group rules (Globals.MaxGroup = 15, the leader invites / kicks / passes leadership,
// AllowGroup decides whether anyone may invite you, members' maps and locations come from S.GroupMembersMap /
// S.SendMemberLocation): a header (member count, Allow group, loot rule, invite box), member cards (class badge, level,
// leader crown, live HP / MP bars, buffs, map and distance / direction when on your map, status), a side panel (the
// picked member's actions, pending invites, the group log), on-screen party frames (left of the screen) and member
// positions for the minimap. Mock data; HP / MP drift and members take hits now and then.

import { canon } from "./hudPeople.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { BuffType } from "../src/protocol/enums.generated.ts";
import { buffIcon } from "../src/game/buffs.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

const MAX_GROUP = 15;
interface Member {
  name: string; cls: string; level: number; hp: number; mp: number; hpMax: number; mpMax: number;
  map: string; dx: number; dy: number; dead: boolean; buffs: number[]; you?: boolean;
}
const CLS: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };
const MEMBERS: Member[] = [
  { name: "GMRyu", cls: "Warrior", level: 42, hp: 3470, mp: 968, hpMax: 4820, mpMax: 2150, map: "", dx: 0, dy: 0, dead: false, buffs: [BuffType.Haste, BuffType.Exp], you: true },
  { name: "Seren", cls: "Archer", level: 31, hp: 1850, mp: 900, hpMax: 2300, mpMax: 1400, map: "", dx: 6, dy: -3, dead: false, buffs: [BuffType.SoulShield, BuffType.BlessedArmour] },
  { name: "Wren", cls: "Taoist", level: 29, hp: 1150, mp: 1500, hpMax: 1500, mpMax: 1900, map: "", dx: -4, dy: 5, dead: false, buffs: [BuffType.SoulShield] },
  { name: "Kaelith", cls: "Assassin", level: 42, hp: 2900, mp: 700, hpMax: 3400, mpMax: 1200, map: "Gi-Ryoong", dx: 0, dy: 0, dead: false, buffs: [BuffType.Haste] },
  { name: "Nyra", cls: "Taoist", level: 24, hp: 0, mp: 300, hpMax: 1100, mpMax: 1300, map: "", dx: 18, dy: 11, dead: true, buffs: [] },
];
const LOG: [string, string][] = [["GMRyu formed the group.", "40 min"], ["Seren joined.", "38 min"], ["Wren joined.", "31 min"], ["Kaelith joined.", "20 min"],
  ["Kaelith looted WhiteGoldRing.", "12 min"], ["Nyra joined.", "6 min"], ["Nyra died.", "1 min"]];
const INVITES: [string, string, number][] = [["Thorne", "Warrior", 38], ["Pip", "Archer", 24]];
const LOOT = ["Free for all", "Round robin", "Leader decides"];
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

const svg = (d: string, cls = "gp-ic") => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.setAttribute("class", cls); const p = document.createElementNS("http://www.w3.org/2000/svg", "path"); p.setAttribute("d", d); s.append(p); return s; };
const CROWN = "M3 8l4 4 5-7 5 7 4-4-2 11H5z";
const ARROW = "M12 3l6 16-6-4-6 4z";

const CSS = `
.hudm .pwin { --winh: 520px; width: 800px; }
.hudm .pwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .gp-ic { width: 13px; height: 13px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .pwin .head { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .pwin .head .count b { font-size: 18px; color: #fff; } .hudm .pwin .head .count span { font-size: 11px; color: var(--dim); }
.hudm .pwin .head .cap { width: 120px; height: 5px; border-radius: 3px; background: #04060a; overflow: hidden; margin-top: 4px; }
.hudm .pwin .head .cap i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .pwin .opt { display: flex; align-items: center; gap: 7px; font-size: 11px; color: var(--dim); }
.hudm .pwin .tog { width: 34px; height: 18px; border-radius: 9px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); }
.hudm .pwin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 12px; height: 12px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .pwin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); }
.hudm .pwin .tog.on::after { left: 18px; background: #fff; }
.hudm .pwin select, .hudm .pwin input.in { height: 26px; box-sizing: border-box; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .pwin input.in:focus, .hudm .pwin select:focus { border-color: var(--ac); }
.hudm .pwin input.in::placeholder { color: var(--dim); }
.hudm .pwin .inv { margin-left: auto; display: flex; gap: 6px; }
.hudm .pwin .inv .in { width: 150px; }
.hudm .pwin .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .pwin .btn.warn { border-color: #b33; color: #fbb; }
.hudm .pwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .pwin .gbody { flex: 1; min-height: 0; display: flex; }
.hudm .pwin .cards { flex: 1; min-width: 0; padding: 12px; overflow-y: auto; display: grid; grid-template-columns: 1fr 1fr; gap: 8px; align-content: start; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .pwin .card { position: relative; display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .pwin .card:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .pwin .card.sel { border-color: var(--ac); box-shadow: inset 0 0 14px color-mix(in srgb, var(--ac) 15%, transparent); }
.hudm .pwin .card.dead { opacity: .6; } .hudm .pwin .card.dead .bars { filter: grayscale(1); }
.hudm .pwin .card.empty { cursor: default; justify-content: center; align-items: center; color: var(--dim); font-size: 11px; border-style: dashed; min-height: 74px; }
.hudm .pwin .badge { position: relative; width: 40px; height: 40px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .pwin .card.you .badge { border-color: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .pwin .crown { position: absolute; top: -9px; left: 50%; transform: translateX(-50%); color: #ffd75a; filter: drop-shadow(0 0 3px rgba(255, 210, 90, .8)); }
.hudm .pwin .crown svg { width: 15px; height: 15px; fill: currentColor; stroke: #000; stroke-width: 1; }
.hudm .pwin .info { flex: 1; min-width: 0; }
.hudm .pwin .nm { display: flex; align-items: center; gap: 6px; font-size: 13px; font-weight: 600; color: #fff; }
.hudm .pwin .nm small { font-size: 10px; font-weight: 500; color: var(--dim); }
.hudm .pwin .nm .st { margin-left: auto; font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .pwin .nm .st.dead { color: #ff7a6a; border-color: rgba(255, 110, 90, .5); }
.hudm .pwin .bars { display: flex; flex-direction: column; gap: 3px; margin: 6px 0 5px; }
.hudm .pwin .b { position: relative; height: 9px; border-radius: 2px; background: #04060a; overflow: hidden; border: 1px solid rgba(0,0,0,.8); }
.hudm .pwin .b i { position: absolute; left: 0; top: 0; bottom: 0; transition: width .4s; }
.hudm .pwin .b.hp i { background: linear-gradient(90deg, #6a0c14, #c8303a); } .hudm .pwin .b.mp i { background: linear-gradient(90deg, #12306e, #3a7ee0); }
.hudm .pwin .b span { position: absolute; inset: 0; font-size: 8px; line-height: 9px; text-align: center; color: #fff; text-shadow: 0 0 2px #000; font-variant-numeric: tabular-nums; }
.hudm .pwin .loc { display: flex; align-items: center; gap: 6px; font-size: 10px; color: var(--dim); }
.hudm .pwin .loc .gp-ic { color: var(--ac); width: 12px; height: 12px; }
.hudm .pwin .loc .far { color: #ffb08a; }
.hudm .pwin .bf { display: flex; gap: 2px; margin-left: auto; }
.hudm .pwin .bf i { width: 15px; height: 15px; border-radius: 3px; background: center / contain no-repeat #000; image-rendering: pixelated; border: 1px solid var(--fr); }
/* Side */
.hudm .pwin .side { width: 240px; flex: none; display: flex; flex-direction: column; gap: 10px; padding: 12px; min-height: 0; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .pwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; display: flex; justify-content: space-between; }
.hudm .pwin .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.hudm .pwin .acts .btn { height: 26px; padding: 0 6px; white-space: nowrap; justify-content: center; }
.hudm .pwin .who { font-size: 13px; font-weight: 700; color: #fff; margin-bottom: 6px; }
.hudm .pwin .ir { display: flex; align-items: center; gap: 8px; padding: 5px 0; font-size: 12px; }
.hudm .pwin .ir span { flex: 1; } .hudm .pwin .ir small { color: var(--dim); font-size: 10px; }
.hudm .pwin .ir .btn { height: 22px; padding: 0 8px; font-size: 10px; }
.hudm .pwin .log { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .pwin .log div { display: flex; gap: 8px; padding: 4px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .pwin .log div span { margin-left: auto; color: var(--dim); white-space: nowrap; }
.hudm .pwin .note { font-size: 11px; color: var(--ac); min-height: 14px; }
/* Party frames (left of the screen) */
.hudm .party { position: absolute; left: 12px; top: 12px; display: flex; flex-direction: column; gap: 5px; pointer-events: auto; }
.hudm .pf { position: relative; display: flex; align-items: center; gap: 7px; width: 176px; padding: 4px 7px 4px 4px; cursor: pointer; border-radius: var(--rad);
  background: linear-gradient(90deg, color-mix(in srgb, var(--pb) 85%, transparent), color-mix(in srgb, var(--pb) 40%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6); }
.hudm .pf:hover, .hudm .pf.sel { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .pf.dead { opacity: .55; }
.hudm .pf .badge { width: 26px; height: 26px; font-size: 8px; position: relative; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .pf .crown { position: absolute; top: -8px; left: 50%; transform: translateX(-50%); color: #ffd75a; }
.hudm .pf .crown svg { width: 12px; height: 12px; fill: currentColor; stroke: #000; stroke-width: 1; }
.hudm .pf .in2 { flex: 1; min-width: 0; }
.hudm .pf .n { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; color: #fff; text-shadow: 0 1px 1px #000; }
.hudm .pf .n small { font-weight: 600; color: var(--dim); }
.hudm .pf .n small.far { color: #ffb08a; }
.hudm .pf .b { position: relative; height: 5px; margin-top: 2px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .pf .b i { position: absolute; left: 0; top: 0; bottom: 0; transition: width .4s; }
.hudm .pf .b.hp i { background: linear-gradient(90deg, #6a0c14, #d0343e); } .hudm .pf .b.mp i { background: linear-gradient(90deg, #12306e, #3a7ee0); }
.hudm .pf.low .b.hp i { animation: hudm-pulse 1s ease-in-out infinite; }
`;

export class GroupMock extends HudWindow {
  readonly frames: HTMLElement;
  private members: Member[] = MEMBERS.map((m) => ({ ...m, buffs: [...m.buffs] }));
  private invites = INVITES.map((i) => [...i] as [string, string, number]);
  private log = LOG.map((l) => [...l] as [string, string]);
  private allow = true;
  private loot = 1;
  private picked = "Seren";
  private confirmKick = "";
  private note = "";
  private showFrames = true;
  private cards!: HTMLElement; private side!: HTMLElement; private head!: HTMLElement;

  constructor() {
    super("pwin", 204, 50); // right of the party frames
    injectCss("hudm-group-css", CSS);
    this.frames = h("div", "party");
    this.head = h("div", "head");
    this.cards = h("div", "cards");
    this.side = h("div", "side");
    this.el.append(h("div", "panel", this.titleBar("GROUP", "Close (P)"), this.head, h("div", "gbody", this.cards, this.side)));
    // Members' HP / MP drift; now and then someone takes a hit or a heal.
    window.setInterval(() => this.sim(), 1200);
    this.renderFrames();
  }

  protected override shown(): void { this.render(); }

  /** Members on your map, as positions relative to `cam` (for the minimap). */
  positions(cam: { x: number; y: number }): { x: number; y: number; name: string }[] {
    return this.members.filter((m) => !m.you && !m.map).map((m) => ({ x: Math.round(cam.x) + m.dx, y: Math.round(cam.y) + m.dy, name: `${m.name} (group)` }));
  }

  private sim(): void {
    for (const m of this.members) {
      if (m.dead || m.you) continue;
      const r = Math.random();
      if (r < 0.12) m.hp = Math.max(1, m.hp - Math.round(m.hpMax * (0.1 + Math.random() * 0.25)));
      else m.hp = Math.min(m.hpMax, m.hp + Math.round(m.hpMax * 0.04));
      m.mp = Math.min(m.mpMax, Math.max(0, m.mp + Math.round(m.mpMax * (Math.random() < 0.3 ? -0.12 : 0.03))));
      if (Math.random() < 0.2) { m.dx += Math.round(Math.random() * 2 - 1); m.dy += Math.round(Math.random() * 2 - 1); }
    }
    this.renderFrames();
    if (this.open) { this.renderCards(); }
  }

  private you(): Member { return this.members.find((m) => m.you)!; }
  private leader(): Member { return this.members[0]!; }
  private iAmLeader(): boolean { return this.leader().you === true; }

  private where(m: Member): { text: string; far: boolean; ang: number } {
    if (m.you) return { text: "You", far: false, ang: 0 };
    if (m.map) return { text: m.map, far: true, ang: 0 };
    const d = Math.round(Math.hypot(m.dx, m.dy));
    const ang = Math.atan2(m.dx, -m.dy);
    const dir = DIRS[(Math.round(ang / (Math.PI / 4)) + 8) % 8]!;
    return { text: `${d} cells ${dir}`, far: false, ang };
  }

  private render(): void {
    for (const m of this.members) canon(m); // the player follows the HUD knobs; everyone else the roster (hudPeople.ts)
    this.renderHead(); this.renderCards(); this.renderSide(); this.renderFrames(); }

  private renderHead(): void {
    const n = this.members.length;
    const cap = h("div", "cap", h("i"));
    (cap.firstChild as HTMLElement).style.width = (n / MAX_GROUP * 100) + "%";
    const allow = h("div", "tog" + (this.allow ? " on" : ""));
    allow.addEventListener("click", () => { this.allow = !this.allow; this.note = this.allow ? "Others can invite you to groups." : "Group invites are now refused."; this.render(); });
    const loot = h("select");
    LOOT.forEach((l, i) => loot.append(Object.assign(h("option", "", l), { value: String(i) })));
    loot.value = String(this.loot);
    loot.disabled = !this.iAmLeader();
    loot.addEventListener("change", () => { this.loot = Number(loot.value); this.log.push([`Loot rule: ${LOOT[this.loot]}.`, "now"]); this.renderSide(); });
    const frames = h("div", "tog" + (this.showFrames ? " on" : ""));
    frames.addEventListener("click", () => { this.showFrames = !this.showFrames; this.render(); });
    const inp = h("input", "in");
    inp.placeholder = "Invite by name";
    inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") invite.click(); });
    const invite = h("div", "btn main" + (this.iAmLeader() && n < MAX_GROUP ? "" : " dis"), "Invite");
    invite.addEventListener("click", () => {
      const name = inp.value.trim();
      if (!name) return;
      this.note = this.members.some((m) => m.name.toLowerCase() === name.toLowerCase()) ? `${name} is already in the group.` : `Invite sent to ${name}.`;
      if (!this.note.includes("already")) this.log.push([`Invited ${name}.`, "now"]);
      inp.value = ""; this.renderSide();
    });
    this.head.replaceChildren(h("div", "count", h("b", "", `${n} / ${MAX_GROUP}`), h("span", "", " members"), cap),
      h("div", "opt", allow, "Allow group"), h("div", "opt", "Loot", loot), h("div", "opt", frames, "Party frames"), h("div", "inv", inp, invite));
  }

  private bars(m: Member, labels: boolean): HTMLElement {
    const bar = (cls: string, v: number, max: number) => { const b = h("div", "b " + cls, h("i"), labels ? h("span", "", `${v} / ${max}`) : ""); (b.firstChild as HTMLElement).style.width = (v / max * 100).toFixed(1) + "%"; return b; };
    return h("div", "bars", bar("hp", m.hp, m.hpMax), bar("mp", m.mp, m.mpMax));
  }

  private renderCards(): void {
    this.cards.replaceChildren();
    for (const m of this.members) {
      const w = this.where(m);
      const badge = h("div", "badge", CLS[m.cls] ?? "");
      if (m === this.leader()) { const c = h("div", "crown", svg(CROWN, "")); c.title = "Leader"; badge.append(c); }
      const loc = h("div", "loc");
      if (!m.you && !w.far) { const a = svg(ARROW); a.style.transform = `rotate(${w.ang}rad)`; loc.append(a); }
      loc.append(h("span", w.far ? "far" : "", w.text));
      const bf = h("div", "bf");
      for (const t of m.buffs) { const i = h("i"); const [lib, idx] = buffIcon(t); void libPicture(lib, idx).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; }); bf.append(i); }
      loc.append(bf);
      const card = h("div", "card" + (m.name === this.picked ? " sel" : "") + (m.dead ? " dead" : "") + (m.you ? " you" : ""), badge,
        h("div", "info", h("div", "nm", m.name, h("small", "", `${m.cls} ${m.level}`), h("span", "st" + (m.dead ? " dead" : ""), m.dead ? "Dead" : m.you ? "You" : w.far ? "Away" : "Nearby")), this.bars(m, true), loc));
      card.addEventListener("click", () => { this.picked = m.name; this.confirmKick = ""; this.render(); });
      this.cards.append(card);
    }
    const free = MAX_GROUP - this.members.length;
    if (free > 0) this.cards.append(h("div", "card empty", `${free} open slot${free === 1 ? "" : "s"} · invite friends or guildmates`));
  }

  private renderSide(): void {
    const m = this.members.find((x) => x.name === this.picked) ?? this.you();
    const lead = this.iAmLeader();
    const act = (label: string, fn: () => void, cls = "", ok = true) => { const b = h("div", "btn " + cls + (ok ? "" : " dis"), label); b.addEventListener("click", fn); return b; };
    const acts = h("div", "acts");
    if (m.you) {
      acts.append(act("Leave group", () => { this.note = "You left the group (mock: nothing changes)."; this.renderSide(); }, "warn"),
        act("Disband", () => { this.note = "Group disbanded (mock)."; this.renderSide(); }, "warn", lead));
    } else {
      acts.append(act("Whisper", () => { this.note = `/w ${m.name} `; this.renderSide(); }), act("Trade", () => { this.note = `Trade request sent to ${m.name}.`; this.renderSide(); }),
        act("Make leader", () => {
          this.members.splice(this.members.indexOf(m), 1); this.members.unshift(m);
          this.log.push([`${m.name} is now the leader.`, "now"]); this.render();
        }, "", lead),
        act(this.confirmKick === m.name ? "Click again" : "Kick", () => {
          if (this.confirmKick !== m.name) { this.confirmKick = m.name; this.renderSide(); return; }
          this.members = this.members.filter((x) => x !== m); this.log.push([`${m.name} was removed from the group.`, "now"]); this.picked = this.you().name; this.confirmKick = ""; this.render();
        }, this.confirmKick === m.name ? "warn" : "", lead));
      if (m.dead) acts.append(act("Resurrect", () => { m.dead = false; m.hp = Math.round(m.hpMax * 0.3); this.log.push([`${m.name} was resurrected.`, "now"]); this.render(); }, "main"));
    }
    const invites = h("div", "");
    invites.append(h("div", "lbl", "Asking to join", h("span", "", String(this.invites.length))));
    for (const inv of this.invites) {
      const [name, cls, lv] = inv;
      const ok = h("div", "btn" + (lead && this.members.length < MAX_GROUP ? "" : " dis"), "Accept"), no = h("div", "btn", "×");
      ok.addEventListener("click", () => {
        this.invites = this.invites.filter((x) => x !== inv);
        this.members.push({ name, cls, level: lv, hp: 2000, mp: 900, hpMax: 2400, mpMax: 1200, map: "", dx: 3, dy: 4, dead: false, buffs: [] });
        this.log.push([`${name} joined.`, "now"]); this.render();
      });
      no.addEventListener("click", () => { this.invites = this.invites.filter((x) => x !== inv); this.renderSide(); });
      invites.append(h("div", "ir", h("span", "", name, " ", h("small", "", `${cls} ${lv}`)), ok, no));
    }
    if (!this.invites.length) invites.append(h("div", "ir", h("small", "", "No requests.")));
    const log = h("div", "log");
    for (const [t, when] of [...this.log].reverse()) log.append(h("div", "", t, h("span", "", when)));
    this.side.replaceChildren(h("div", "", h("div", "lbl", m.you ? "You" : "Member"), h("div", "who", m.name), acts), h("div", "note", this.note), invites,
      h("div", "lbl", "Group log", h("span", "", "")), log);
  }

  private renderFrames(): void {
    this.frames.hidden = !this.showFrames || this.members.length < 2;
    this.frames.replaceChildren();
    for (const m of this.members) {
      if (m.you) continue;
      const w = this.where(m);
      const badge = h("div", "badge", CLS[m.cls] ?? "");
      if (m === this.leader()) badge.append(h("div", "crown", svg(CROWN, "")));
      const bar = (cls: string, v: number, max: number) => { const b = h("div", "b " + cls, h("i")); (b.firstChild as HTMLElement).style.width = (v / max * 100).toFixed(1) + "%"; return b; };
      const f = h("div", "pf" + (m.dead ? " dead" : "") + (m.hp / m.hpMax < 0.3 && !m.dead ? " low" : "") + (m.name === this.picked ? " sel" : ""), badge,
        h("div", "in2", h("div", "n", m.name, h("small", w.far ? "far" : "", m.dead ? "Dead" : w.far ? w.text : `${m.level}`)), bar("hp", m.hp, m.hpMax), bar("mp", m.mp, m.mpMax)));
      f.title = `${m.name} · ${m.cls} ${m.level} · ${w.text}\nClick to open the group`;
      f.addEventListener("click", () => { this.picked = m.name; this.toggle(true); });
      this.frames.append(f);
    }
  }
}
