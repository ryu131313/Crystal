// Relationship (marriage) window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the
// desktop's RelationshipDialog (lover name, location or offline, married date and length, Allow marriage, Request
// marriage, Request divorce, Mail lover, Whisper lover) on the server's rules (MarriageSystem.ini defaults in Settings.cs;
// PlayerObject.MarriageRequest / "RECALLLOVER" / ReplaceWeddingRing, HumanObject's Lover buff): both partners level
// MinimumLevel (10)+, standing next to each other and facing each other, neither married nor within MarriageCooldown (7)
// days of a divorce; while both are online each gets the Lover buff (+EXPBonus 5 % EXP); @recalllover needs
// AllowLoverRecall, both wearing the wedding ring on the left ring slot and a map that allows recall; a replacement ring
// costs the ring's RequiredAmount x 10 x ReplaceRingCost (125) gold; a divorce starts the cooldown. Lover chat is the ":)"
// prefix. Banner with both partners and a beating heart (floating hearts while both are online), days together and the
// next milestone, wedding rings with left-hand checks, recall with its checklist, memories timeline, love letters and
// chat; divorce, then a proposal with the requirements checklist and a wedding. Seren, dates and replies are mock.

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

const MIN_LEVEL = 10, COOLDOWN = 7, EXP_BONUS = 5, RING_COST = 125;
interface Lover { name: string; cls: string; level: number; online: boolean; where: string; ringOnLeft: boolean }
const CLASS_TAG: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };
const CANDIDATES: Lover[] = [
  { name: "Seren", cls: "Archer", level: 31, online: true, where: "Stone Temple", ringOnLeft: true },
  { name: "Mirelle", cls: "Wizard", level: 44, online: true, where: "Bichon Province", ringOnLeft: false },
  { name: "Bram", cls: "Wizard", level: 8, online: true, where: "Bichon Province", ringOnLeft: false },
  { name: "Talia", cls: "Taoist", level: 51, online: false, where: "", ringOnLeft: false },
];
/** The wedding ring: GoldRing (Items.Lib 148, from the DB copy). RequiredAmount (level) is mock. */
const RING: [string, number, number] = ["GoldRing", 148, 22];
const ICON = {
  heart: "M12 20s-7-4.5-7-10a4 4 0 017-2.5A4 4 0 0119 10c0 5.5-7 10-7 10z", ring: "M12 8a6 6 0 110 12 6 6 0 010-12zM9 4h6l-1.5 3h-3z",
  mail: "M3 6h18v12H3zM3 7l9 6 9-6", chat: "M4 5h16v11H9l-5 4z", recall: "M12 3v4M12 17v4M3 12h4M17 12h4M12 8a4 4 0 110 8 4 4 0 010-8z",
  cal: "M4 6h16v14H4zM4 10h16M8 3v4M16 3v4", star: "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z", info: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 11v5M12 8v.01",
};
const svg = (d: string) => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.innerHTML = `<path d="${d}"/>`; return s; };
const MILESTONES = [7, 30, 50, 100, 365];
const dateBack = (days: number) => { const d = new Date(Date.now() - days * 864e5); return d.toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric" }); };

const CSS = `
.hudm .rlwin { width: 920px; }
.hudm .rlwin .panel { height: 600px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .rlwin svg { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .rlwin .ban { position: relative; display: flex; align-items: center; gap: 18px; padding: 16px 24px; overflow: hidden; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 100% at 50% 50%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .rlwin .who { flex: 1; display: flex; align-items: center; gap: 12px; } .hudm .rlwin .who.r { flex-direction: row-reverse; text-align: right; }
.hudm .rlwin .med { position: relative; width: 64px; height: 64px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 800; color: #fff;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 2px solid var(--ac); box-shadow: 0 0 0 3px #000, 0 0 calc(14px * var(--gl)) var(--glow); }
.hudm .rlwin .med.off { border-color: var(--fr); box-shadow: 0 0 0 3px #000; color: var(--dim); }
.hudm .rlwin .med u { position: absolute; right: 2px; bottom: 2px; width: 12px; height: 12px; border-radius: 50%; border: 2px solid #000; background: #4fd36a; text-decoration: none; } .hudm .rlwin .med.off u { background: #555; }
.hudm .rlwin .who b { display: block; font-size: 17px; color: #fff; } .hudm .rlwin .who small { display: block; font-size: 11px; color: var(--dim); }
.hudm .rlwin .who .you { color: var(--ac); font-size: 10px; font-weight: 700; letter-spacing: .1em; }
.hudm .rlwin .hc { position: relative; width: 240px; flex: none; display: flex; flex-direction: column; align-items: center; gap: 4px; }
.hudm .rlwin .hc .ln { position: absolute; left: -40px; right: -40px; top: 34px; height: 2px; background: linear-gradient(90deg, transparent, var(--ac) 25%, var(--ac) 75%, transparent); opacity: .7; }
.hudm .rlwin .hc .ln.off { background: repeating-linear-gradient(90deg, var(--fr) 0 6px, transparent 6px 12px); }
.hudm .rlwin .big { position: relative; z-index: 1; width: 70px; height: 70px; color: var(--ac); filter: drop-shadow(0 0 10px color-mix(in srgb, var(--ac) 70%, transparent)); }
.hudm .rlwin .big svg { width: 70px; height: 70px; fill: color-mix(in srgb, var(--ac) 75%, #000); stroke: #fff; stroke-width: 1; }
.hudm .rlwin .big.beat { animation: hudm-beat 1.2s ease-in-out infinite; } @keyframes hudm-beat { 0%, 40%, 100% { transform: scale(1); } 15% { transform: scale(1.14); } 25% { transform: scale(.98); } }
.hudm .rlwin .big.off svg { fill: color-mix(in srgb, var(--pb) 80%, #000); stroke: var(--fr); } .hudm .rlwin .big.off { filter: none; }
.hudm .rlwin .big.broken svg { fill: color-mix(in srgb, var(--pb) 60%, #000); stroke: var(--fr); stroke-dasharray: 3 3; }
.hudm .rlwin .big b { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding-bottom: 6px; font-size: 16px; font-weight: 800; color: #fff; text-shadow: 0 1px 3px #000; }
.hudm .rlwin .hc .t { font-size: 11px; color: var(--dim); text-align: center; } .hudm .rlwin .hc .t b { color: #fff; }
.hudm .rlwin .chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 9px; border-radius: 10px; font-size: 10px; font-weight: 700; border: 1px solid var(--fr); color: var(--dim); }
.hudm .rlwin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 25%, transparent); box-shadow: 0 0 8px color-mix(in srgb, var(--ac) 45%, transparent); } .hudm .rlwin .chip svg { width: 12px; height: 12px; }
.hudm .rlwin .fh { position: absolute; bottom: -10px; z-index: 0; color: var(--ac); opacity: 0; pointer-events: none; animation: hudm-fh 4s ease-in infinite; }
.hudm .rlwin .fh svg { width: 12px; height: 12px; fill: currentColor; stroke: none; }
@keyframes hudm-fh { 0% { opacity: 0; transform: translateY(0) scale(.6); } 15% { opacity: .8; } 100% { opacity: 0; transform: translateY(-120px) translateX(var(--sx)) scale(1.1); } }
.hudm .rlwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .rlwin .col { padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .rlwin .col > * { flex-shrink: 0; }
.hudm .rlwin .col.a { width: 340px; flex: none; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); } .hudm .rlwin .col.b { flex: 1; min-width: 0; }
.hudm .rlwin .lbl { display: flex; align-items: center; gap: 6px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .rlwin .lbl svg { color: var(--ac); width: 14px; height: 14px; } .hudm .rlwin .lbl b { margin-left: auto; letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .rlwin .box { padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .rlwin .rings { display: flex; gap: 10px; }
.hudm .rlwin .rg { flex: 1; display: flex; align-items: center; gap: 8px; font-size: 11px; } .hudm .rlwin .rg b { display: block; color: #fff; font-size: 12px; }
.hudm .rlwin .ok { color: #7fdc8a; } .hudm .rlwin .no { color: #ff8a6a; }
.hudm .rlwin .ck { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 3px 0; }
.hudm .rlwin .ck u { width: 16px; height: 16px; flex: none; border-radius: 50%; text-decoration: none; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800; }
.hudm .rlwin .ck.y u { color: #fff; background: #3a9a55; } .hudm .rlwin .ck.n u { color: #fff; background: #a33; } .hudm .rlwin .ck.n { color: var(--dim); }
.hudm .rlwin .ck a { margin-left: auto; font-size: 10px; color: var(--ac); cursor: pointer; text-decoration: underline; }
.hudm .rlwin .row { display: flex; gap: 6px; } .hudm .rlwin .row .btn { flex: 1; }
.hudm .rlwin .btn.lmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .rlwin .btn.dng:hover { border-color: #e05555; } .hudm .rlwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .rlwin .note { font-size: 11px; color: var(--dim); line-height: 1.4; } .hudm .rlwin .note b { color: var(--tx); }
.hudm .rlwin .warn { font-size: 11px; line-height: 1.4; padding: 8px 10px; border-radius: var(--rad); border: 1px solid #c45; background: rgba(200,60,80,.12); } .hudm .rlwin .warn .row { margin-top: 6px; }
.hudm .rlwin .tl { position: relative; padding-left: 18px; display: flex; flex-direction: column; gap: 8px; }
.hudm .rlwin .tl::before { content: ""; position: absolute; left: 5px; top: 4px; bottom: 4px; width: 2px; background: linear-gradient(180deg, var(--ac), var(--fr)); }
.hudm .rlwin .tl div { position: relative; font-size: 12px; } .hudm .rlwin .tl div::before { content: ""; position: absolute; left: -17px; top: 3px; width: 8px; height: 8px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 6px color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .rlwin .tl div.fut { color: var(--dim); } .hudm .rlwin .tl div.fut::before { background: var(--pb); border: 1px solid var(--fr); box-shadow: none; }
.hudm .rlwin .tl small { display: block; font-size: 10px; color: var(--dim); }
.hudm .rlwin .tabs { display: flex; gap: 4px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .rlwin .tabs div { padding: 6px 12px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .rlwin .tabs div.on { color: var(--ac); border-bottom-color: var(--ac); }
.hudm .rlwin .chat { display: flex; flex-direction: column; gap: 3px; height: 190px; overflow-y: auto; padding: 8px; border-radius: var(--rad); background: #05070cb0; border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font-size: 12px; scrollbar-width: thin; }
.hudm .rlwin .chat div b { color: var(--ac); } .hudm .rlwin .chat div.me b { color: var(--ac); } .hudm .rlwin .chat div.sys { color: var(--dim); font-style: italic; }
.hudm .rlwin .send { display: flex; gap: 6px; } .hudm .rlwin .send span { align-self: center; font: 600 11px ui-monospace, Consolas, monospace; color: var(--ac); }
.hudm .rlwin input[type=text], .hudm .rlwin textarea { flex: 1; box-sizing: border-box; padding: 5px 9px; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #05070c; color: var(--tx); font: 12px inherit; outline: none; resize: none; }
.hudm .rlwin textarea { height: 110px; } /* was inherited from lab.html's page-wide textarea rule */
.hudm .rlwin input[type=text] { height: 26px; } .hudm .rlwin input:focus, .hudm .rlwin textarea:focus { border-color: var(--ac); }
.hudm .rlwin .letter { padding: 10px 12px; border-radius: var(--rad); font-size: 12px; line-height: 1.45; background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 8%, transparent), transparent); border: 1px solid rgba(255,106,149,.35); }
.hudm .rlwin .letter small { display: block; font-size: 10px; color: var(--dim); margin-bottom: 4px; }
.hudm .rlwin .tog { display: flex; align-items: center; gap: 8px; font-size: 12px; cursor: pointer; }
.hudm .rlwin .sw { position: relative; width: 30px; height: 16px; flex: none; border-radius: 8px; background: var(--pb); border: 1px solid var(--fr); }
.hudm .rlwin .sw::after { content: ""; position: absolute; left: 2px; top: 2px; width: 10px; height: 10px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .rlwin .sw.on { background: color-mix(in srgb, var(--ac) 45%, transparent); border-color: var(--ac); } .hudm .rlwin .sw.on::after { left: 16px; background: #fff; }
.hudm .rlwin .cand { display: flex; align-items: center; gap: 10px; padding: 6px 8px; border-radius: var(--rad); cursor: pointer; font-size: 12px; border: 1px solid transparent; }
.hudm .rlwin .cand:hover { border-color: var(--fr); } .hudm .rlwin .cand.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .rlwin .cand .tg { width: 34px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 9px; font-weight: 800; border-radius: 6px; border: 1px solid var(--fr); background: var(--pb); }
.hudm .rlwin .cand .n { flex: 1; } .hudm .rlwin .cand .n b { color: #fff; } .hudm .rlwin .cand .n small { display: block; font-size: 10px; color: var(--dim); }
.hudm .rlwin .stats { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; }
.hudm .rlwin .stats div { text-align: center; padding: 8px 4px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); font-size: 10px; color: var(--dim); }
.hudm .rlwin .stats b { display: block; font-size: 16px; color: #fff; }
.hudm .rlwin .cf { position: absolute; z-index: 9; pointer-events: none; color: var(--ac); animation: hudm-rlcf 1.8s cubic-bezier(.2,.7,.4,1) forwards; }
.hudm .rlwin .cf svg { width: 14px; height: 14px; fill: currentColor; stroke: none; }
@keyframes hudm-rlcf { to { transform: translate(var(--dx), var(--dy)) rotate(var(--rot)); opacity: 0; } }
.hudm .rlwin .toast { position: absolute; left: 50%; top: 40%; z-index: 10; translate: -50% 0; padding: 12px 22px; border-radius: var(--rad); font-size: 15px; font-weight: 700; color: #fff; text-align: center;
  background: color-mix(in srgb, var(--ac) 40%, var(--pb)); border: 1px solid var(--ac); box-shadow: 0 0 24px color-mix(in srgb, var(--ac) 60%, transparent); animation: hudm-rltoast 3s ease-out forwards; }
.hudm .rlwin .toast small { display: block; font-size: 11px; font-weight: 400; color: var(--tx); }
@keyframes hudm-rltoast { 0% { opacity: 0; scale: .8; } 10%, 80% { opacity: 1; scale: 1; } 100% { opacity: 0; } }
`;

export class RelationshipMock extends HudWindow {
  private lover: Lover | null = { ...CANDIDATES[0]! };
  private days = 41;
  private myRingLeft = true;
  private allowRecall = true;
  private mapRecall = true;
  private allow = true;
  private cooldown = 0;
  private confirmDivorce = false;
  private tab: "Chat" | "Letters" = "Chat";
  private expTogether = 1_284_500;
  private recalls = 12;
  private recallNote = "";
  private recallCd = 0;
  private pick = 1;
  private facing = false;
  private draft = "";
  private letter = "";
  private chat: [string, string][] = [["Seren", "Meet me at the Bichon fountain? ♥"], ["GMRyu", "On my way!"], ["", "Lover buff active: +5% EXP."]];
  private letters: [string, string, string][] = [["Seren", "2 days ago", "Found a TarragonNecklace in the caves, saving it for you."], ["GMRyu", "1 week ago", "Happy one month! Here's to many more hunts together."]];
  private level = 42;

  constructor() {
    super("rlwin", 60, 30);
    injectCss("hudm-relationship-css", CSS);
    window.setInterval(() => {
      if (this.recallCd > 0) {
        this.recallCd--;
        // Only the recall button changes: a full render would drop a toast or the chat input's focus.
        const b = this.el.querySelector<HTMLElement>(".recall-btn");
        if (b && this.lover) { b.textContent = this.recallCd ? `Recall ${this.lover.name} (${this.recallCd}s)` : `Recall ${this.lover.name}`; if (!this.recallCd) b.classList.remove("dis"); }
      }
      if (this.lover?.online) { this.expTogether += 900 + Math.floor(Math.random() * 2400); const e = this.el.querySelector(".stats b"); if (e) e.textContent = this.expTogether.toLocaleString("en-GB"); }
    }, 1000);
  }
  protected override shown(): void { this.render(); }
  setLevel(level: number): void { this.level = level; if (this.open) this.render(); }

  private render(): void {
    const scroll = [...this.el.querySelectorAll<HTMLElement>(".col")].map((c) => c.scrollTop);
    this.el.replaceChildren(h("div", "panel", this.titleBar("RELATIONSHIP", "Close (L)"), this.banner(), h("div", "body", this.leftCol(), this.rightCol())));
    this.el.querySelectorAll<HTMLElement>(".col").forEach((c, i) => { c.scrollTop = scroll[i] ?? 0; });
  }

  private medal(name: string, cls: string, level: number, online: boolean, where: string, right: boolean, you: boolean): HTMLElement {
    return h("div", "who" + (right ? " r" : ""), h("div", "med" + (online ? "" : " off"), CLASS_TAG[cls] ?? "?", h("u")),
      h("div", "", ...(you ? [h("span", "you", "YOU")] : []), h("b", "", name), h("small", "", `Lv ${level} ${cls}`), h("small", "", online ? where : "Offline")));
  }

  private banner(): HTMLElement {
    const me = this.medal("GMRyu", "Warrior", this.level, true, "Bichon Province", false, true);
    const l = this.lover;
    const heart = h("div", "big" + (l ? (l.online ? " beat" : " off") : " broken"), svg(ICON.heart), h("b", "", l ? String(this.days) : ""));
    heart.title = l ? `${this.days} days together` : "Not married";
    const ban = h("div", "ban");
    if (l) {
      const next = MILESTONES.find((m) => m > this.days) ?? this.days + 100;
      const centre = h("div", "hc", h("div", "ln" + (l.online ? "" : " off")), heart, h("div", "t", h("b", "", `${this.days} days`), ` together · ${next - this.days} days to ${next}`),
        h("span", "chip" + (l.online ? " on" : ""), svg(ICON.star), `Lover buff +${EXP_BONUS}% EXP`));
      ban.append(me, centre, this.medal(l.name, l.cls, l.level, l.online, l.where, true, false));
      if (l.online) for (let i = 0; i < 9; i++) {
        const f = h("div", "fh", svg(ICON.heart));
        Object.assign(f.style, { left: 35 + Math.random() * 30 + "%", animationDelay: (i * 0.45).toFixed(2) + "s" });
        f.style.setProperty("--sx", (Math.random() * 60 - 30).toFixed(0) + "px");
        ban.append(f);
      }
    } else {
      ban.append(me, h("div", "hc", heart, h("div", "t", this.cooldown ? h("b", "", `${this.cooldown} days`) : h("b", "", "Single"), this.cooldown ? " before you can marry again" : " · open to proposals")),
        h("div", "who r", h("div", "med off", "?"), h("div", "", h("b", "", "Nobody yet"), h("small", "", "Propose below"))));
    }
    return ban;
  }

  private check(ok: boolean, text: string, fix?: [string, () => void]): HTMLElement {
    const c = h("div", "ck " + (ok ? "y" : "n"), h("u", "", ok ? "✓" : "✕"), text);
    if (!ok && fix) { const a = h("a", "", fix[0]); a.addEventListener("click", () => { fix[1](); this.render(); }); c.append(a); }
    return c;
  }

  private leftCol(): HTMLElement {
    const col = h("div", "col a");
    const l = this.lover;
    if (l) {
      col.append(h("div", "stats", h("div", "", h("b", "", this.expTogether.toLocaleString("en-GB")), "EXP with the buff"), h("div", "", h("b", "", String(this.recalls)), "recalls"),
        h("div", "", h("b", "", dateBack(this.days).split(" ").slice(0, 2).join(" ")), "wedding day")));
      // Rings
      const ring = (who: string, onLeft: boolean, toggle?: () => void) => {
        const r = h("div", "rg", itemCell(this.el, `${RING[0]} (wedding ring)`, RING[1]), h("div", "", h("b", "", who), h("span", onLeft ? "ok" : "no", onLeft ? "Left hand ✓" : "Not on the left hand")));
        if (toggle) { r.style.cursor = "pointer"; r.title = "Toggle (mock)"; r.addEventListener("click", () => { toggle(); this.render(); }); }
        return r;
      };
      const cost = RING[2] * 10 * RING_COST;
      const replace = h("div", "btn", `Replace ring · ${cost.toLocaleString("en-GB")} Gold`);
      replace.title = `RequiredAmount ${RING[2]} × 10 × ReplaceRingCost ${RING_COST}`;
      replace.addEventListener("click", () => { this.myRingLeft = true; this.recallNote = `A new wedding ring was made for ${cost.toLocaleString("en-GB")} Gold.`; this.render(); });
      col.append(h("div", "lbl", svg(ICON.ring), "Wedding rings"), h("div", "box", h("div", "rings", ring("Yours", this.myRingLeft, () => { this.myRingLeft = !this.myRingLeft; }), ring(l.name + "'s", l.ringOnLeft, () => { l.ringOnLeft = !l.ringOnLeft; })),
        h("div", "row", replace)));
      // Recall
      const ok = this.allowRecall && this.myRingLeft && l.ringOnLeft && l.online && this.mapRecall;
      const rb = h("div", "btn lmain recall-btn" + (ok && !this.recallCd ? "" : " dis"), this.recallCd ? `Recall ${l.name} (${this.recallCd}s)` : `Recall ${l.name}`);
      rb.addEventListener("click", () => {
        if (this.recallCd || !(this.allowRecall && this.myRingLeft && l.ringOnLeft && l.online && this.mapRecall)) return; this.recalls++; this.recallCd = 10; l.where = "Bichon Province"; this.recallNote = `${l.name} was recalled to you.`; this.chat.push(["", `${l.name} was recalled to your side.`]); this.render(); });
      col.append(h("div", "lbl", svg(ICON.recall), "Recall lover", h("b", "", "@recalllover")), h("div", "box",
        this.check(this.allowRecall, "Lover recall allowed on this server", ["allow (mock)", () => { this.allowRecall = true; }]),
        this.check(this.myRingLeft, "You wear the wedding ring on the left", ["put it on", () => { this.myRingLeft = true; }]),
        this.check(l.ringOnLeft, `${l.name} wears the wedding ring on the left`, ["ask (mock)", () => { l.ringOnLeft = true; }]),
        this.check(l.online, `${l.name} is online`, ["log in (mock)", () => { l.online = true; }]),
        this.check(this.mapRecall, "This map allows recall", ["move (mock)", () => { this.mapRecall = true; }]),
        h("div", "row", rb), ...(this.recallNote ? [h("div", "note", this.recallNote)] : [])));
      // Divorce
      if (this.confirmDivorce) {
        const yes = h("div", "btn dng", "Divorce"); yes.addEventListener("click", () => this.divorce());
        const no = h("div", "btn", "Stay together"); no.addEventListener("click", () => { this.confirmDivorce = false; this.render(); });
        col.append(h("div", "warn", `A divorce ends the marriage and the wedding rings become plain rings. Neither of you can marry again for ${COOLDOWN} days.`, h("div", "row", yes, no)));
      } else {
        const d = h("div", "btn dng", "Request divorce"); d.addEventListener("click", () => { this.confirmDivorce = true; this.render(); });
        col.append(h("div", "row", d));
      }
    } else this.proposal(col);
    const allow = h("div", "tog", h("div", "sw" + (this.allow ? " on" : "")), "Allow marriage requests");
    allow.addEventListener("click", () => { this.allow = !this.allow; this.render(); });
    col.append(allow);
    return col;
  }

  /** Not married: candidates and MarriageRequest's checks. */
  private proposal(col: HTMLElement): void {
    const list = h("div", "");
    CANDIDATES.forEach((c, i) => {
      const e = h("div", "cand" + (this.pick === i ? " on" : ""), h("div", "tg", CLASS_TAG[c.cls] ?? "?"), h("div", "n", h("b", "", c.name), h("small", "", `Lv ${c.level} ${c.cls} · ${c.online ? c.where : "Offline"}`)));
      e.addEventListener("click", () => { this.pick = i; this.facing = false; this.render(); });
      list.append(e);
    });
    const c = CANDIDATES[this.pick]!;
    const checks = [
      this.check(this.level >= MIN_LEVEL, `You are level ${MIN_LEVEL}+`),
      this.check(c.level >= MIN_LEVEL, `${c.name} is level ${MIN_LEVEL}+`),
      this.check(this.cooldown === 0, `No divorce in the last ${COOLDOWN} days`, ["skip (mock)", () => { this.cooldown = 0; }]),
      this.check(c.online, `${c.name} is online`),
      this.check(this.facing, `Standing next to ${c.name}, facing each other`, ["walk over (mock)", () => { this.facing = true; }]),
    ];
    const ok = this.level >= MIN_LEVEL && c.level >= MIN_LEVEL && !this.cooldown && c.online && this.facing;
    const p = h("div", "btn lmain" + (ok ? "" : " dis"), `💍 Propose to ${c.name}`);
    p.addEventListener("click", () => {
      p.textContent = "Waiting for an answer…"; p.classList.add("dis");
      window.setTimeout(() => {
        this.lover = { ...c, ringOnLeft: true }; this.days = 0; this.myRingLeft = true; this.chat = [["", `You married ${c.name}. Lover buff active: +${EXP_BONUS}% EXP.`]]; this.letters = [];
        this.expTogether = 0; this.recalls = 0; this.recallNote = "";
        this.render();
        this.celebrate(`💍 You married ${c.name}!`, "Wedding rings were placed on your left hands");
      }, 1500);
    });
    col.append(h("div", "lbl", svg(ICON.heart), "Propose"), list, h("div", "box", ...checks), h("div", "row", p));
  }

  private divorce(): void {
    const l = this.lover!;
    this.lover = null; this.cooldown = COOLDOWN; this.confirmDivorce = false; this.facing = false;
    this.pick = Math.max(0, CANDIDATES.findIndex((c) => c.name !== l.name));
    this.render();
  }

  private rightCol(): HTMLElement {
    const col = h("div", "col b");
    const l = this.lover;
    if (l) {
      const tabs = h("div", "tabs");
      for (const t of ["Chat", "Letters"] as const) { const d = h("div", this.tab === t ? "on" : "", t === "Chat" ? "LOVER CHAT" : "LOVE LETTERS"); d.addEventListener("click", () => { this.tab = t; this.render(); }); tabs.append(d); }
      col.append(tabs);
      if (this.tab === "Chat") {
        const chat = h("div", "chat");
        for (const [who, text] of this.chat.slice(-40)) chat.append(h("div", who === "" ? "sys" : who === "GMRyu" ? "me" : "", ...(who ? [h("b", "", who + ": ")] : []), text));
        const inp = h("input"); inp.type = "text"; inp.placeholder = l.online ? `Whisper to ${l.name}…` : `${l.name} is offline`; inp.value = this.draft;
        const send = () => {
          const t = this.draft.trim(); if (!t || !l.online) return;
          this.chat.push(["GMRyu", t]); this.draft = "";
          window.setTimeout(() => { this.chat.push([l.name, ["♥", "Miss you too!", "Coming!", "Hehe ♥", "See you soon."][Math.floor(Math.random() * 5)]!]); if (this.open && this.tab === "Chat") this.render(); }, 1300);
          this.render(); this.el.querySelector<HTMLInputElement>(".send input")?.focus();
        };
        inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") send(); });
        inp.addEventListener("input", () => { this.draft = inp.value; });
        const sb = h("div", "btn" + (l.online ? "" : " dis"), "Send"); sb.addEventListener("click", send);
        col.append(chat, h("div", "send", h("span", "", ":)"), inp, sb));
        window.setTimeout(() => { chat.scrollTop = chat.scrollHeight; });
      } else {
        const ta = h("textarea"); ta.rows = 3; ta.placeholder = `Write a letter to ${l.name} (sent as mail)…`; ta.value = this.letter;
        ta.addEventListener("keydown", (e) => e.stopPropagation());
        ta.addEventListener("input", () => { this.letter = ta.value; });
        const sb = h("div", "btn lmain", "Send letter");
        sb.addEventListener("click", () => { if (!this.letter.trim()) return; this.letters.unshift(["GMRyu", "just now", this.letter.trim()]); this.letter = ""; this.render(); });
        col.append(h("div", "send", ta), h("div", "row", sb));
        for (const [from, when, text] of this.letters) col.append(h("div", "letter", h("small", "", `${from === "GMRyu" ? "To " + l.name : "From " + from} · ${when}`), text));
      }
      // Memories
      const tl = h("div", "tl", h("div", "", "First met in Bichon Province", h("small", "", dateBack(this.days + 12))), h("div", "", `Married ${l.name}`, h("small", "", dateBack(this.days))));
      for (const m of MILESTONES) tl.append(h("div", m <= this.days ? "" : "fut", `${m} days together`, h("small", "", m <= this.days ? dateBack(this.days - m) : `in ${m - this.days} days`)));
      col.append(h("div", "lbl", svg(ICON.cal), "Memories"), tl);
    } else {
      col.append(h("div", "lbl", svg(ICON.info), "How marriage works"), h("div", "box note",
        h("div", "", h("b", "", "Proposing: "), `both of you must be level ${MIN_LEVEL} or higher, standing next to each other and facing each other.`), h("br"),
        h("div", "", h("b", "", "Lover buff: "), `+${EXP_BONUS}% EXP for both while you are both online.`), h("br"),
        h("div", "", h("b", "", "Wedding rings: "), "worn on the left hand they let you use @recalllover. A lost ring can be replaced for gold."), h("br"),
        h("div", "", h("b", "", "Chat: "), "start a line with :) to talk to your partner."), h("br"),
        h("div", "", h("b", "", "Divorce: "), `ends the marriage; neither of you can marry for ${COOLDOWN} days.`)));
      if (this.cooldown) { const s = h("div", "btn", "Skip cooldown (mock)"); s.addEventListener("click", () => { this.cooldown = 0; this.render(); }); col.append(h("div", "row", s)); }
    }
    return col;
  }

  private celebrate(title: string, sub: string): void {
    const panel = this.el.querySelector<HTMLElement>(".panel");
    if (!panel) return;
    panel.append(h("div", "toast", title, h("small", "", sub)));
    const w = panel.clientWidth, ht = panel.clientHeight;
    for (let i = 0; i < 50; i++) {
      const c = h("div", "cf", svg(ICON.heart));
      const a = Math.random() * Math.PI * 2, d = 120 + Math.random() * 300;
      Object.assign(c.style, { left: w / 2 + "px", top: ht / 2 + "px", color: ["var(--ac)", "#fff", "color-mix(in srgb, var(--ac) 50%, #fff)", "#f2c94c"][i % 4]! });
      c.style.setProperty("--dx", Math.cos(a) * d + "px"); c.style.setProperty("--dy", Math.sin(a) * d + 120 + "px"); c.style.setProperty("--rot", Math.random() * 360 - 180 + "deg");
      panel.append(c);
    }
    window.setTimeout(() => { for (const c of panel.querySelectorAll(".cf, .toast")) c.remove(); }, 3000);
  }
}
