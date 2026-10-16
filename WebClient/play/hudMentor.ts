// Mentor window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// MentorDialog (mentor / mentee names and levels, online state, the mentee's banked EXP, Allow requests, Add, Remove with
// the cooldown warning) built round the server's rules (MentorSystem.ini defaults in Settings.cs, PlayerObject /
// HumanObject): the mentor must be at least LevelGap (10) levels above the mentee; a mentorship lasts MentorshipLength
// (7) days; the mentee graduates (the bond ends) once their level + LevelGap passes the mentor's; while the partner is
// online the mentor gets the Mentor buff (+MentorDamageBoost 10 % damage) and the mentee the Mentee buff, whose
// +MenteeExpBoost 10 % EXP only counts while the mentor is in the mentee's group, on the same map, in range and alive
// (PlayerObject.GainExp); PercentXPtoMentor (1 %) of the mentee's EXP is banked and paid to the mentor when the bond
// ends, if the partner is online then (MentorBreak); with
// MenteeSkillBoost the mentor trains skills twice as fast while grouped with the mentee nearby; cancelling starts a
// cooldown of MentorshipLength days. View it as the mentor (GMRyu teaching Wren) or as the mentee (GMRyu taught by
// Aldric); your level is the HUD's. Banner with both portraits joined by a live link, a term ring, buff chips, the
// graduation track, the EXP bank, mentor chat (the !# prefix), a find board that applies the level-gap rule, and
// a "How it works" panel. People, chat and the EXP simulation are mock.

import { ME } from "./hudPeople.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

const GAP = 10, LENGTH = 7, DMG = 10, EXPB = 10, BANK = 1;
type Role = "Mentor" | "Mentee";
interface Person { name: string; cls: string; level: number; online: boolean; where: string }
const CLASS_TAG: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };
const BOARD: Person[] = [
  { name: "Pip", cls: "Archer", level: 24, online: true, where: "Woomyon Woods" }, { name: "Tamsin", cls: "Warrior", level: 24, online: true, where: "Serpent Valley" },
  { name: "Odo", cls: "Taoist", level: 29, online: true, where: "Bichon Province" }, { name: "Brisk", cls: "Archer", level: 33, online: true, where: "Mongchon" },
  { name: "Lumen", cls: "Assassin", level: 38, online: false, where: "" }, { name: "Garrick", cls: "Warrior", level: 55, online: true, where: "Woomyon Woods" },
  { name: "Elowen", cls: "Taoist", level: 61, online: true, where: "Bichon Wall" }, { name: "Sable", cls: "Wizard", level: 68, online: false, where: "" },
];
const ICON = {
  link: "M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1", sword: "M5 19L19 5M14 5h5v5M5 14l5 5",
  star: "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z", book: "M4 5a2 2 0 012-2h14v16H6a2 2 0 00-2 2zM4 19V5", cap: "M2 9l10-5 10 5-10 5zM6 11v5c3 2 9 2 12 0v-5M22 9v6",
  bank: "M3 10l9-6 9 6M5 10v8M9 10v8M15 10v8M19 10v8M3 20h18", chat: "M4 5h16v11H9l-5 4z", info: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 11v5M12 8v.01",
};
const svg = (d: string) => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.innerHTML = `<path d="${d}"/>`; return s; };
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");

const CSS = `
.hudm .mnwin { width: 920px; }
.hudm .mnwin .panel { height: 600px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .mnwin svg { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .mnwin .seg { position: absolute; left: 10px; top: 4px; z-index: 2; display: flex; padding: 2px; gap: 2px; border-radius: 11px; background: #05070c; border: 1px solid var(--fr); }
.hudm .mnwin .seg div { padding: 2px 10px; font-size: 10px; font-weight: 700; letter-spacing: .06em; cursor: pointer; border-radius: 9px; color: var(--dim); }
.hudm .mnwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
/* Banner */
.hudm .mnwin .ban { position: relative; display: flex; align-items: center; gap: 18px; padding: 16px 22px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 50% 90% at 50% 50%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 70%); }
.hudm .mnwin .who { flex: 1; display: flex; align-items: center; gap: 12px; }
.hudm .mnwin .who.r { flex-direction: row-reverse; text-align: right; }
.hudm .mnwin .med { position: relative; width: 64px; height: 64px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 800; letter-spacing: .04em; color: #fff;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 2px solid var(--ac); box-shadow: 0 0 0 3px #000, 0 0 calc(14px * var(--gl)) var(--glow); }
.hudm .mnwin .med.off { border-color: var(--fr); box-shadow: 0 0 0 3px #000; color: var(--dim); }
.hudm .mnwin .med u { position: absolute; right: 2px; bottom: 2px; width: 12px; height: 12px; border-radius: 50%; border: 2px solid #000; background: #4fd36a; text-decoration: none; }
.hudm .mnwin .med.off u { background: #555; }
.hudm .mnwin .med em { position: absolute; top: -9px; left: 50%; translate: -50% 0; font-style: normal; font-size: 8px; letter-spacing: .1em; padding: 1px 6px; border-radius: 6px; color: #fff; background: var(--ac); }
.hudm .mnwin .who b { display: block; font-size: 17px; color: #fff; } .hudm .mnwin .who small { display: block; font-size: 11px; color: var(--dim); }
.hudm .mnwin .who .you { color: var(--ac); font-size: 10px; font-weight: 700; letter-spacing: .1em; }
.hudm .mnwin .mcen { width: 230px; flex: none; position: relative; display: flex; flex-direction: column; align-items: center; gap: 6px; }
.hudm .mnwin .beam { position: absolute; left: -30px; right: -30px; top: 44px; height: 2px; background: linear-gradient(90deg, transparent, var(--ac) 20%, var(--ac) 80%, transparent); filter: drop-shadow(0 0 4px var(--ac)); }
.hudm .mnwin .beam::after { content: ""; position: absolute; top: -2px; width: 40px; height: 6px; border-radius: 3px; background: radial-gradient(ellipse, #fff, transparent 70%); animation: hudm-beam 1.8s linear infinite; }
@keyframes hudm-beam { from { left: 0; } to { left: calc(100% - 40px); } }
.hudm .mnwin .beam.off { background: repeating-linear-gradient(90deg, var(--fr) 0 6px, transparent 6px 12px); filter: none; } .hudm .mnwin .beam.off::after { display: none; }
.hudm .mnwin .ring { position: relative; z-index: 1; width: 88px; height: 88px; }
.hudm .mnwin .ring svg { width: 88px; height: 88px; transform: rotate(-90deg); }
.hudm .mnwin .ring circle { fill: color-mix(in srgb, var(--pb) 90%, #000); stroke-width: 6; } .hudm .mnwin .ring .bg { stroke: #05070c; } .hudm .mnwin .ring .fg { fill: none; stroke: var(--ac); stroke-linecap: round; filter: drop-shadow(0 0 3px var(--glow)); }
.hudm .mnwin .ring div { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; }
.hudm .mnwin .ring b { font-size: 18px; color: #fff; line-height: 1; } .hudm .mnwin .ring small { font-size: 9px; color: var(--dim); letter-spacing: .08em; text-transform: uppercase; }
.hudm .mnwin .mbuffs { display: flex; gap: 6px; }
.hudm .mnwin .bf { display: flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 10px; font-size: 10px; font-weight: 700; border: 1px solid var(--fr); color: var(--dim); }
.hudm .mnwin .bf.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 28%, var(--pb)); box-shadow: 0 0 6px var(--glow); }
.hudm .mnwin .bf svg { width: 12px; height: 12px; }
/* Body */
.hudm .mnwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .mnwin .col { padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mnwin .col > * { flex-shrink: 0; }
.hudm .mnwin .col.a { width: 330px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mnwin .col.b { flex: 1; min-width: 0; }
.hudm .mnwin .lbl { display: flex; align-items: center; gap: 6px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .mnwin .lbl svg { color: var(--ac); width: 14px; height: 14px; } .hudm .mnwin .lbl b { margin-left: auto; letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .mnwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .mnwin .track { position: relative; height: 34px; margin: 4px 6px 0; }
.hudm .mnwin .track .rail { position: absolute; left: 0; right: 0; top: 10px; height: 6px; border-radius: 3px; background: #05070c; border: 1px solid var(--fr); }
.hudm .mnwin .track .fill { position: absolute; left: 0; top: 10px; height: 8px; border-radius: 4px; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, #000), var(--ac)); box-shadow: 0 0 8px var(--glow); transition: width .6s; }
.hudm .mnwin .track .nd { position: absolute; top: 6px; width: 14px; height: 14px; margin-left: -7px; border-radius: 50%; background: var(--pb); border: 2px solid var(--fr); }
.hudm .mnwin .track .nd.on { background: var(--ac); border-color: #fff; box-shadow: 0 0 6px var(--glow); }
.hudm .mnwin .track .nd span { position: absolute; top: 16px; left: 50%; translate: -50% 0; font-size: 9px; color: var(--dim); white-space: nowrap; }
.hudm .mnwin .track .nd.cap { width: 20px; height: 20px; top: 3px; margin-left: -10px; display: flex; align-items: center; justify-content: center; color: var(--ac); }
.hudm .mnwin .track .nd.cap svg { width: 12px; height: 12px; }
.hudm .mnwin .grad { font-size: 12px; margin-top: 10px; } .hudm .mnwin .grad b { color: var(--ac); }
.hudm .mnwin .xp { height: 6px; margin-top: 6px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .mnwin .xp i { display: block; height: 100%; background: var(--ac); transition: width .5s; }
.hudm .mnwin .bank { display: flex; align-items: center; gap: 12px; }
.hudm .mnwin .bank .big { font-size: 22px; font-weight: 800; color: #f2c94c; text-shadow: 0 0 10px rgba(242,201,76,.4); font-variant-numeric: tabular-nums; }
.hudm .mnwin .bank .big.pop { animation: hudm-mnpop .5s ease-out; } @keyframes hudm-mnpop { 30% { transform: scale(1.12); color: #fff; } }
.hudm .mnwin .bank small { display: block; font-size: 10px; color: var(--dim); line-height: 1.4; }
.hudm .mnwin .perk { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 4px 0; }
.hudm .mnwin .perk u { width: 8px; height: 8px; border-radius: 50%; flex: none; text-decoration: none; background: var(--fr); } .hudm .mnwin .perk.on u { background: #4fd36a; box-shadow: 0 0 6px #4fd36a; }
.hudm .mnwin .perk span { flex: 1; } .hudm .mnwin .perk em { font-style: normal; font-size: 10px; color: var(--dim); }
.hudm .mnwin .row { display: flex; gap: 6px; } .hudm .mnwin .row .btn { flex: 1; }
.hudm .mnwin .btn.mmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .mnwin .btn.dng:hover { border-color: #e05555; box-shadow: 0 0 0 1px #000, 0 0 8px rgba(224,85,85,.5); }
.hudm .mnwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .mnwin .warn { font-size: 11px; line-height: 1.4; padding: 8px 10px; border-radius: var(--rad); border: 1px solid #c45; background: rgba(200,60,80,.12); }
.hudm .mnwin .warn .row { margin-top: 6px; }
.hudm .mnwin .chat { display: flex; flex-direction: column; gap: 3px; height: 128px; overflow-y: auto; padding: 8px; border-radius: var(--rad); background: #05070cb0; border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; scrollbar-width: thin; }
.hudm .mnwin .chat div b { color: #c9a0ff; } .hudm .mnwin .chat div.me b { color: var(--ac); } .hudm .mnwin .chat div.sys { color: var(--dim); font-style: italic; }
.hudm .mnwin .send { display: flex; gap: 6px; } .hudm .mnwin .send span { align-self: center; font: 600 11px ui-monospace, Consolas, monospace; color: #c9a0ff; }
.hudm .mnwin input[type=text] { flex: 1; height: 26px; box-sizing: border-box; padding: 0 9px; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #05070c; color: var(--tx); font-size: 12px; outline: none; }
.hudm .mnwin input[type=text]:focus { border-color: var(--ac); }
.hudm .mnwin .bd { display: flex; align-items: center; gap: 10px; padding: 6px 8px; border-radius: var(--rad); border: 1px solid transparent; font-size: 12px; }
.hudm .mnwin .bd:nth-child(even) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .mnwin .bd .tg { width: 34px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 9px; font-weight: 800; border-radius: 6px; border: 1px solid var(--fr); background: var(--pb); }
.hudm .mnwin .bd .n { flex: 1; min-width: 0; } .hudm .mnwin .bd .n b { color: #fff; } .hudm .mnwin .bd .n small { display: block; font-size: 10px; color: var(--dim); }
.hudm .mnwin .bd .ok { font-size: 10px; color: #7fdc8a; } .hudm .mnwin .bd .no { font-size: 10px; color: #ff8a6a; }
.hudm .mnwin .bd .dot { width: 7px; height: 7px; border-radius: 50%; flex: none; background: #4fd36a; } .hudm .mnwin .bd .dot.off { background: #555; }
.hudm .mnwin .tog { display: flex; align-items: center; gap: 8px; font-size: 12px; cursor: pointer; }
.hudm .mnwin .sw { position: relative; width: 30px; height: 16px; flex: none; border-radius: 8px; background: var(--pb); border: 1px solid var(--fr); }
.hudm .mnwin .sw::after { content: ""; position: absolute; left: 2px; top: 2px; width: 10px; height: 10px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .mnwin .sw.on { background: color-mix(in srgb, var(--ac) 45%, var(--pb)); border-color: var(--ac); } .hudm .mnwin .sw.on::after { left: 16px; background: #fff; }
.hudm .mnwin .rules { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .mnwin .rules div { padding: 7px 9px; border-radius: var(--rad); font-size: 11px; line-height: 1.35; color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .mnwin .rules b { display: block; font-size: 14px; color: var(--ac); }
.hudm .mnwin .empty { text-align: center; padding: 24px 10px; color: var(--dim); font-size: 12px; }
.hudm .mnwin .empty b { display: block; font-size: 16px; color: #fff; margin-bottom: 4px; }
.hudm .mnwin .cf { position: absolute; z-index: 9; width: 7px; height: 11px; border-radius: 2px; pointer-events: none; animation: hudm-mncf 1.5s cubic-bezier(.2,.7,.4,1) forwards; }
@keyframes hudm-mncf { to { transform: translate(var(--dx), var(--dy)) rotate(var(--rot)); opacity: 0; } }
.hudm .mnwin .toast { position: absolute; left: 50%; top: 40%; z-index: 10; translate: -50% 0; padding: 12px 22px; border-radius: var(--rad); font-size: 15px; font-weight: 700; color: #fff; text-align: center;
  background: color-mix(in srgb, var(--ac) 40%, var(--pb)); border: 1px solid var(--ac); box-shadow: 0 0 24px var(--glow); animation: hudm-mntoast 3s ease-out forwards; }
.hudm .mnwin .toast small { display: block; font-size: 11px; font-weight: 400; color: var(--tx); }
@keyframes hudm-mntoast { 0% { opacity: 0; scale: .8; } 10%, 80% { opacity: 1; scale: 1; } 100% { opacity: 0; } }
`;

interface Bond { partner: Person; startLevel: number; day: number; hoursLeft: number; bank: number; expPct: number }

export class MentorMock extends HudWindow {
  private role: Role = "Mentor";
  private myLevel = 42;
  private bonds: Record<Role, Bond | null> = {
    Mentor: { partner: { name: "Wren", cls: "Taoist", level: 29, online: true, where: "Bichon Province" }, startLevel: 24, day: 3, hoursLeft: 14, bank: 18640, expPct: 0.42 },
    Mentee: { partner: { name: "Aldric", cls: "Warrior", level: 60, online: true, where: "Woomyon Woods" }, startLevel: 36, day: 5, hoursLeft: 6, bank: 9120, expPct: 0.7 },
  };
  private cooldown: Record<Role, number> = { Mentor: 0, Mentee: 0 };
  private allow = true;
  private confirmEnd = false;
  private pending: string | null = null;
  private chat: [string, string][] = [["Wren", "Thanks for the help in the Bichon caves!"], ["GMRyu", "Any time. Stick close and the buff stays up."], ["", "Mentor buff active: +10% damage."]];
  private chatMentee: [string, string][] = [["Aldric", "Remember: Halfmoon in crowds, Thrusting in corridors."], ["GMRyu", "Got it, heading to Woomyon."]];
  private draft = "";
  private bankEl: HTMLElement | null = null;

  constructor() {
    super("mnwin", 60, 30);
    injectCss("hudm-mentor-css", CSS);
    window.setInterval(() => this.tick(), 2000);
  }
  protected override shown(): void { this.render(); }
  /** Your level from the HUD mock. */
  setLevel(level: number): void {
    this.myLevel = level;
    // As the mentee, your own level-up can end the bond (PlayerObject.LevelUp).
    const b = this.bonds.Mentee;
    if (b && this.role === "Mentee" && level + GAP > b.partner.level) { this.graduate(); return; }
    if (this.open) this.render();
  }

  private me(): Person { return { name: ME.name, cls: ME.cls, level: this.myLevel, online: true, where: "Bichon Province" }; }
  /** Mentor and mentee of the shown bond. */
  private pair(b: Bond): [Person, Person] { return this.role === "Mentor" ? [this.me(), b.partner] : [b.partner, this.me()]; }
  /** PlayerObject.LevelUp: the bond ends once mentee level + LevelGap > mentor level, so the last level kept is mentor - gap. */
  private gradLevel(b: Bond): number { return this.pair(b)[0].level - GAP + 1; }

  /** Whether mentor and mentee are grouped and near each other (mock: Wren is in your group; you and Aldric are). */
  private grouped(b: Bond): boolean { return b.partner.online && (this.role === "Mentor" ? b.partner.name === "Wren" : true); }

  /** The EXP simulation, while the window is open: the mentee earns EXP (+10 % when grouped with the mentor nearby);
   *  1 % is banked for the mentor; the partner's level-ups; graduation. Your own level is the HUD's (setLevel). */
  private tick(): void {
    const b = this.bonds[this.role];
    if (!b || !this.open) return;
    const [, mentee] = this.pair(b);
    const gain = 600 + Math.random() * 1400;
    b.bank += gain * (this.grouped(b) ? 1 + EXPB / 100 : 1) * BANK / 100 * 30; // ×30: a few kills per tick
    let levelled = false;
    if (this.role === "Mentor") {
      b.expPct += 0.04 + Math.random() * 0.06;
      if (b.expPct >= 1) {
        b.expPct -= 1; levelled = true;
        b.partner.level++;
        this.log(b, "", `${mentee.name} reached level ${mentee.level + 1}!`);
      }
    }
    const [mentorNow, menteeNow] = this.pair(b);
    if (menteeNow.level + GAP > mentorNow.level) { this.graduate(); return; }
    if (this.open) {
      if (levelled) this.render();
      else {
        if (this.bankEl) { this.bankEl.textContent = fmt(b.bank); this.bankEl.classList.remove("pop"); void this.bankEl.offsetWidth; this.bankEl.classList.add("pop"); }
        const xp = this.el.querySelector<HTMLElement>(".xp i"); if (xp) xp.style.width = b.expPct * 100 + "%";
      }
    }
  }
  private log(_b: Bond, who: string, text: string): void { (this.role === "Mentor" ? this.chat : this.chatMentee).push([who, text]); }

  private graduate(): void {
    const b = this.bonds[this.role]!;
    const [mentor, mentee] = this.pair(b);
    this.bonds[this.role] = null;
    this.confirmEnd = false;
    this.render();
    this.celebrate(`🎓 ${mentee.name} graduated!`, `${mentor.name} receives ${fmt(b.bank)} banked EXP`);
  }

  private end(): void {
    const b = this.bonds[this.role]!;
    const [mentor] = this.pair(b);
    this.bonds[this.role] = null;
    this.cooldown[this.role] = LENGTH;
    this.confirmEnd = false;
    this.log(b, "", `The mentorship was cancelled. ${b.partner.online ? `${mentor.name} receives ${fmt(b.bank)} banked EXP.` : `${b.partner.name} is offline, so the ${fmt(b.bank)} banked EXP is lost.`} Cooldown: ${LENGTH} days.`);
    this.render();
  }

  private celebrate(title: string, sub: string): void {
    const panel = this.el.querySelector<HTMLElement>(".panel");
    if (!panel) return;
    panel.append(h("div", "toast", title, h("small", "", sub)));
    const w = panel.clientWidth, ht = panel.clientHeight, cols = ["var(--ac)", "#fff", "#f2c94c", "#c9a0ff"];
    for (let i = 0; i < 70; i++) {
      const c = h("div", "cf");
      const a = Math.random() * Math.PI * 2, d = 120 + Math.random() * 300;
      Object.assign(c.style, { left: w / 2 + "px", top: ht / 2 + "px", background: cols[i % cols.length]! });
      c.style.setProperty("--dx", Math.cos(a) * d + "px"); c.style.setProperty("--dy", Math.sin(a) * d + 140 + "px"); c.style.setProperty("--rot", Math.random() * 720 - 360 + "deg");
      panel.append(c);
    }
    window.setTimeout(() => { for (const c of panel.querySelectorAll(".cf, .toast")) c.remove(); }, 3000);
  }

  private render(): void {
    const b = this.bonds[this.role];
    const seg = h("div", "seg");
    for (const r of ["Mentor", "Mentee"] as Role[]) {
      const d = h("div", this.role === r ? "on" : "", r === "Mentor" ? "AS MENTOR" : "AS MENTEE");
      d.addEventListener("click", () => { this.role = r; this.confirmEnd = false; this.pending = null; this.render(); });
      seg.append(d);
    }
    const title = this.titleBar("MENTORSHIP", "Close (X)");
    title.append(seg);
    seg.addEventListener("mousedown", (e) => e.stopPropagation());
    this.bankEl = null;
    this.el.replaceChildren(h("div", "panel", title, this.banner(b), h("div", "body", this.leftCol(b), this.rightCol(b))));
  }

  private medal(p: Person, tag: string, you: boolean): HTMLElement {
    const m = h("div", "med" + (p.online ? "" : " off"), CLASS_TAG[p.cls] ?? "?", h("u"), h("em", "", tag));
    return h("div", "who" + (tag === "MENTEE" ? " r" : ""), m, h("div", "", ...(you ? [h("span", "you", "YOU")] : []), h("b", "", p.name),
      h("small", "", `Lv ${p.level} ${p.cls}`), h("small", "", p.online ? p.where : "Offline")));
  }

  private banner(b: Bond | null): HTMLElement {
    if (!b) {
      const me = this.me();
      return h("div", "ban", this.medal(me, this.role.toUpperCase(), true),
        h("div", "mcen", h("div", "beam off"), h("div", "ring", h("div", "", h("b", "", "—"), h("small", "", this.cooldown[this.role] ? `${this.cooldown[this.role]}d cooldown` : "No bond")))),
        h("div", "who r", h("div", "med off", "?", h("em", "", this.role === "Mentor" ? "MENTEE" : "MENTOR")), h("div", "", h("b", "", "Nobody yet"), h("small", "", this.role === "Mentor" ? "Find someone to teach below" : "Find a mentor below"))));
    }
    const [mentor, mentee] = this.pair(b);
    const c = 2 * Math.PI * 36, left = (LENGTH - b.day) + b.hoursLeft / 24;
    const ring = h("div", "ring");
    ring.innerHTML = `<svg viewBox="0 0 88 88"><circle class="bg" cx="44" cy="44" r="36"/><circle class="fg" cx="44" cy="44" r="36" stroke-dasharray="${c}" stroke-dashoffset="${c * (1 - b.day / LENGTH)}"/></svg>`;
    ring.append(h("div", "", h("b", "", `Day ${b.day}`), h("small", "", `of ${LENGTH}`)));
    ring.title = `${Math.floor(left)} days ${b.hoursLeft} h left in this mentorship`;
    const on = b.partner.online;
    const buffs = h("div", "mbuffs", h("div", "bf" + (on ? " on" : ""), svg(ICON.sword), `+${DMG}% damage`), h("div", "bf" + (this.grouped(b) ? " on" : ""), svg(ICON.star), `+${EXPB}% EXP`));
    buffs.title = on ? "The damage buff is on while you are both online; the EXP bonus only while grouped with the mentor nearby" : "Buffs need both of you online";
    return h("div", "ban", this.medal(mentor, "MENTOR", this.role === "Mentor"), h("div", "mcen", h("div", "beam" + (on ? "" : " off")), ring, buffs), this.medal(mentee, "MENTEE", this.role === "Mentee"));
  }

  private leftCol(b: Bond | null): HTMLElement {
    const col = h("div", "col a");
    if (b) {
      const [mentor, mentee] = this.pair(b);
      const grad = this.gradLevel(b), span = Math.max(1, grad - b.startLevel);
      const track = h("div", "track", h("div", "rail"));
      const fill = h("div", "fill"); fill.style.width = Math.min(100, (mentee.level - b.startLevel) / span * 100) + "%"; track.append(fill);
      const steps = Math.min(span, 8);
      for (let i = 0; i <= steps; i++) {
        const lv = Math.round(b.startLevel + span * i / steps), last = i === steps;
        const nd = h("div", "nd" + (mentee.level >= lv ? " on" : "") + (last ? " cap" : ""), ...(last ? [svg(ICON.cap)] : []), h("span", "", String(lv)));
        nd.style.left = i / steps * 100 + "%";
        nd.title = last ? `Graduates on reaching level ${grad}` : `Level ${lv}`;
        track.append(nd);
      }
      const xp = h("div", "xp", h("i")); (xp.firstChild as HTMLElement).style.width = b.expPct * 100 + "%";
      col.append(h("div", "lbl", svg(ICON.cap), "Graduation", h("b", "", `${mentee.level - b.startLevel} / ${span} levels`)),
        h("div", "box", track, h("div", "grad", `${mentee.name} graduates at level `, h("b", "", String(grad)), ` (${mentor.name} is ${mentor.level}; the gap must stay ${GAP}+).`), xp));
      this.bankEl = h("div", "big", fmt(b.bank));
      col.append(h("div", "lbl", svg(ICON.bank), "EXP bank"), h("div", "box bank", this.bankEl,
        h("small", "", `${BANK}% of ${mentee.name}'s EXP is saved for ${mentor.name}, paid when the mentorship ends (only if the other one is online then).`)));
      const grouped = this.grouped(b);
      const perks: [boolean, string, string][] = [
        [b.partner.online, `Mentor buff: +${DMG}% damage`, mentor.name], [grouped, `Mentee buff: +${EXPB}% EXP`, `${mentee.name} · grouped and nearby`],
        [grouped, "Skill training ×2", "grouped and nearby"], [true, `Banking ${BANK}% EXP`, "always"],
      ];
      const pk = h("div", "box");
      for (const [on, t, s] of perks) pk.append(h("div", "perk" + (on ? " on" : ""), h("u"), h("span", "", t), h("em", "", s)));
      col.append(h("div", "lbl", svg(ICON.star), "Perks"), pk);
      if (this.confirmEnd) {
        const yes = h("div", "btn dng", "End mentorship"); yes.addEventListener("click", () => this.end());
        const no = h("div", "btn", "Keep it"); no.addEventListener("click", () => { this.confirmEnd = false; this.render(); });
        col.append(h("div", "warn", `Ending it early starts a ${LENGTH}-day cooldown before you can take part in another mentorship. The banked EXP is still paid if ${b.partner.name} is online.`, h("div", "row", yes, no)));
      } else {
        const inv = h("div", "btn mmain", "Group invite"); inv.addEventListener("click", () => { this.log(b, "", `Group invite sent to ${b.partner.name}.`); this.render(); });
        const endB = h("div", "btn dng", "End mentorship"); endB.addEventListener("click", () => { this.confirmEnd = true; this.render(); });
        col.append(h("div", "row", inv, endB));
      }
    } else {
      col.append(h("div", "empty", h("b", "", this.role === "Mentor" ? "You are not mentoring anyone" : "You have no mentor"),
        this.cooldown[this.role] ? `Cooldown: ${this.cooldown[this.role]} days before a new mentorship.` : "Pick someone on the board to send a request."));
      if (this.cooldown[this.role]) { const skip = h("div", "btn", "Skip cooldown (mock)"); skip.addEventListener("click", () => { this.cooldown[this.role] = 0; this.render(); }); col.append(skip); }
    }
    const rules = h("div", "rules");
    for (const [v, t] of [[`${GAP}+`, "levels between mentor and mentee"], [`${LENGTH} days`, "per mentorship"], [`+${DMG}%`, "damage for the mentor"], [`+${EXPB}%`, "EXP for the mentee, grouped with the mentor nearby"],
      [`${BANK}%`, "of the mentee's EXP banked for the mentor"], ["×2", "mentor skill training when grouped"]] as const) rules.append(h("div", "", h("b", "", v), t));
    col.append(h("div", "lbl", svg(ICON.info), "How it works"), rules);
    return col;
  }

  private rightCol(b: Bond | null): HTMLElement {
    const col = h("div", "col b");
    if (b) {
      const lines = this.role === "Mentor" ? this.chat : this.chatMentee;
      const chat = h("div", "chat");
      for (const [who, text] of lines.slice(-30)) chat.append(h("div", who === "" ? "sys" : who === "GMRyu" ? "me" : "", ...(who ? [h("b", "", who + ": ")] : []), text));
      const inp = h("input"); inp.type = "text"; inp.placeholder = `Message ${b.partner.name}…`; inp.value = this.draft;
      inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") send(); });
      inp.addEventListener("input", () => { this.draft = inp.value; });
      const send = () => {
        const t = this.draft.trim(); if (!t) return;
        lines.push(["GMRyu", t]); this.draft = "";
        if (b.partner.online) window.setTimeout(() => { lines.push([b.partner.name, ["Sounds good!", "On my way.", "Thanks!", "Haha, will do."][Math.floor(Math.random() * 4)]!]); if (this.open) this.render(); }, 1200);
        this.render();
        this.el.querySelector<HTMLInputElement>(".send input")?.focus();
      };
      const sb = h("div", "btn", "Send"); sb.addEventListener("click", send);
      col.append(h("div", "lbl", svg(ICON.chat), "Mentor chat", h("b", "", "!# in the chat box")), chat, h("div", "send", h("span", "", "!#"), inp, sb));
      window.setTimeout(() => { chat.scrollTop = chat.scrollHeight; });
    }
    // Find board: the level-gap rule from PlayerObject.AddMentor (C.AddMentor).
    const board = h("div", "");
    const me = this.myLevel;
    for (const p of BOARD) {
      const asMentee = this.role === "Mentor"; // you look for mentees
      const fits = asMentee ? me - GAP >= p.level : p.level - GAP >= me;
      const why = !p.online ? "Offline" : fits ? "Eligible" : asMentee ? `Needs level ${me - GAP} or lower` : `Needs level ${me + GAP} or higher`;
      const can = fits && p.online && !b && !this.cooldown[this.role] && !this.pending;
      const btn = h("div", "btn" + (can ? " mmain" : " dis"), this.pending === p.name ? "Sent…" : "Request");
      btn.addEventListener("click", () => {
        if (!can) return;
        this.pending = p.name; this.render();
        window.setTimeout(() => {
          this.pending = null;
          this.bonds[this.role] = { partner: { ...p }, startLevel: asMentee ? p.level : me, day: 1, hoursLeft: 23, bank: 0, expPct: 0.05 };
          (this.role === "Mentor" ? this.chat : this.chatMentee).push(["", `${p.name} accepted. The mentorship lasts ${LENGTH} days.`]);
          this.render();
          this.celebrate("🤝 Mentorship started", `${asMentee ? "You are mentoring" : "You are mentored by"} ${p.name}`);
        }, 1500);
      });
      board.append(h("div", "bd", h("span", "dot" + (p.online ? "" : " off")), h("div", "tg", CLASS_TAG[p.cls] ?? "?"),
        h("div", "n", h("b", "", p.name), h("small", "", `Lv ${p.level} ${p.cls}${p.where ? " · " + p.where : ""}`)), h("span", fits && p.online ? "ok" : "no", why), btn));
    }
    const allow = h("div", "tog", h("div", "sw" + (this.allow ? " on" : "")), "Allow mentor requests");
    allow.addEventListener("click", () => { this.allow = !this.allow; this.render(); });
    col.append(h("div", "lbl", svg(ICON.book), this.role === "Mentor" ? "Find a mentee" : "Find a mentor", h("b", "", b ? "one mentorship at a time" : `${BOARD.filter((p) => p.online).length} online`)), board, allow);
    return col;
  }
}
