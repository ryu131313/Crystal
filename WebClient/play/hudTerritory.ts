// Guild territories window for the scene lab's UI tester (2026-10-15), a modern take on the desktop's
// GuildTerritoryDialog (a 7-per-page list of Idx / owner guild / leader / status / price with a Buy button) around the
// server's real rules (NPCSegment BuyGT / ExtendGT / TeleportGT / GTSale / GTCancelSale, PlayerObject.
// PurchaseGuildTerritory, GuildObject.GTForSale / EndGTSale, Envir.LoadGTInfo):
//  - the estates: every territory (MapInfo.GT / GTIndex) from a copy of the live Server.MirDB, play/lab-data/
//    territories.json: 10 estates, each a court plus three floors (GA0-GA39), with the NPCs and spawns of each floor;
//  - renting a vacant estate (Settings.BuyGTGold 10,000,000 guild gold, GTDays 30), extending it (ExtendGT 1,000,000 for
//    another 30 days), listing it for sale (at least 2,000,000, and only while standing in it) or cancelling the sale,
//    buying another guild's estate that is for sale (the price goes to the seller's guild; the keys change hands after
//    24 h, until then teleporting in is refused), and teleporting in (the lab's camera goes to the court's safe zone);
//  - leader-only actions (Ranks[0]); a "View as member" switch shows the refusals; one estate per guild (HasGT).
// The other guilds, leaders, lease days and prices are mock; guild gold is the guild storage's (mock). Nothing is sent
// anywhere. Look: an estate list with ownership chips and lease bars, a filter row, and a detail side with a stacked
// blueprint of the floors (amenities from the floor's NPCs), the lease timeline and the actions.

import { HudWindow, h, injectCss } from "./hudWindow.ts";
import type { WorldHost } from "./hudMinimap.ts";

export interface TerritoryHost {
  world(): WorldHost | undefined;
  say(text: string): void;
  confirm(title: string, text: string): Promise<boolean>;
  amount(title: string, text: string, min: number, max: number, value: number): Promise<number | null>;
}
interface Floor { f: string; t: string; npcs: string[]; mobs: string[]; zone: [number, number] | null }
interface Row { gt: number; floors: Floor[] }
interface Estate { gt: number; floors: Floor[]; owner: string; leader: string; days: number; price: number; handover: number }

const BUY = 10_000_000, EXTEND = 1_000_000, DAYS = 30, MIN_SALE = 2_000_000;
const MY_GUILD = "GameMaster", ME = "GMRyu";
/** Mock owners by estate: [guild, leader, days left, sale price]. */
const OWNERS: Record<number, [string, string, number, number]> = {
  0: ["Nightfall", "Garrick", 21, 0], 1: ["Lionheart", "Ironbark", 9, 14_500_000], 3: ["Silver Dawn", "Moonshade", 27, 0], 4: ["Red Lotus", "Ashenveil", 4, 6_000_000],
  6: ["Eclipse", "Umbra", 16, 0], 7: ["Iron Covenant", "Talon", 12, 22_000_000],
};
const FLOOR_NAME: Record<string, string> = { GTCourt: "Court", GT1F: "First floor", GT2F: "Second floor", GT3F: "Third floor" };
/** NPC name -> [amenity, icon path]. */
const AMENITY: [RegExp, string, string][] = [
  [/Store/i, "Storage", "M4 8l8-4 8 4v10l-8 4-8-4zM4 8l8 4 8-4M12 12v10"],
  [/Transporter/i, "Transporter", "M12 3a9 9 0 110 18 9 9 0 010-18zM12 7v5l3 3"],
  [/Bulletin/i, "Bulletin board", "M4 4h16v12H4zM8 20h8M12 16v4M7 8h10M7 11h6"],
  [/Peddler/i, "Peddler", "M6 7h12l-1 13H7zM9 7a3 3 0 016 0"],
  [/Steward/i, "Steward", "M12 3a3.5 3.5 0 110 7 3.5 3.5 0 010-7zM5 21c0-4 3-7 7-7s7 3 7 7"],
  [/Stairs/i, "Stairs", "M4 20h4v-4h4v-4h4V8h4"],
];
const MOB_ICON = "M12 3l3 5-3 2-3-2zM5 21l7-11 7 11";
const fmt = (n: number) => n.toLocaleString("en-GB");
/** Crest initials: the capitals ("GameMaster" GM, "Silver Lotus" SL), else the first two letters. */
const crest = (name: string) => { const caps = name.replace(/[^A-Z]/g, ""); return (caps.length >= 2 ? caps : name).slice(0, 2).toUpperCase(); };

const CSS = `
.hudm .gtwin { width: 940px; }
.hudm .gtwin .panel { height: 580px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .gtwin .top { display: flex; align-items: center; gap: 14px; padding: 12px 16px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 40% 140% at 0% 50%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 70%); }
.hudm .gtwin .crest { width: 44px; height: 44px; flex: none; display: flex; align-items: center; justify-content: center; font-size: 14px; font-weight: 800; color: #fff;
  clip-path: polygon(50% 0, 100% 18%, 100% 62%, 50% 100%, 0 62%, 0 18%); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 70%, #fff), color-mix(in srgb, var(--ac) 45%, #000)); }
.hudm .gtwin .crest.sm { width: 30px; height: 30px; font-size: 10px; background: linear-gradient(180deg, color-mix(in srgb, var(--cc) 75%, #fff), color-mix(in srgb, var(--cc) 40%, #000)); }
.hudm .gtwin .me b { display: block; font-size: 15px; color: #fff; } .hudm .gtwin .me span { font-size: 11px; color: var(--dim); }
.hudm .gtwin .stat { display: flex; flex-direction: column; padding: 0 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gtwin .stat small { font-size: 9px; font-weight: 700; letter-spacing: .14em; color: var(--dim); } .hudm .gtwin .stat b { font-size: 14px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gtwin .stat b.gold { color: #f3e08a; }
.hudm .gtwin .role { margin-left: auto; display: flex; border-radius: 12px; overflow: hidden; border: 1px solid var(--fr); }
.hudm .gtwin .role div { padding: 4px 12px; font-size: 11px; cursor: pointer; color: var(--dim); }
.hudm .gtwin .role div.on { color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .gtwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .gtwin .list { width: 400px; flex: none; display: flex; flex-direction: column; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gtwin .chips { display: flex; gap: 5px; padding: 10px 12px 6px; }
.hudm .gtwin .chips div { padding: 3px 10px; border-radius: 11px; font-size: 11px; cursor: pointer; color: var(--dim); border: 1px solid var(--fr); }
.hudm .gtwin .chips div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 25%, transparent); } .hudm .gtwin .chips i { font-style: normal; margin-left: 5px; color: var(--ac); }
.hudm .gtwin .rows { flex: 1; overflow-y: auto; padding: 4px 10px 10px; display: flex; flex-direction: column; gap: 5px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gtwin .er { position: relative; display: grid; grid-template-columns: 34px 1fr auto; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); cursor: pointer;
  border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: color-mix(in srgb, var(--frl) 4%, transparent); transition: border-color .12s, background .12s; }
.hudm .gtwin .er:hover { border-color: color-mix(in srgb, var(--ac) 50%, var(--fr)); }
.hudm .gtwin .er.sel { border-color: var(--ac); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 22%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gtwin .er.mine { box-shadow: inset 3px 0 0 #f3e08a; }
.hudm .gtwin .num { width: 34px; height: 34px; border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 800; color: var(--ac);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .gtwin .er .nm b { display: block; font-size: 12.5px; color: #fff; } .hudm .gtwin .er .nm span { font-size: 10.5px; color: var(--dim); }
.hudm .gtwin .er .lb { grid-column: 2 / 4; height: 3px; border-radius: 2px; background: #04060a; overflow: hidden; } .hudm .gtwin .er .lb i { display: block; height: 100%; background: var(--ac); }
.hudm .gtwin .er .lb.low i { background: #ff8a5a; }
.hudm .gtwin .tag { font-size: 9.5px; font-weight: 800; letter-spacing: .08em; padding: 2px 8px; border-radius: 9px; white-space: nowrap; border: 1px solid; text-transform: uppercase; }
.hudm .gtwin .tag.vacant { color: #6fe08a; border-color: color-mix(in srgb, #6fe08a 50%, transparent); }
.hudm .gtwin .tag.sale { color: #f3e08a; border-color: color-mix(in srgb, #f3e08a 50%, transparent); }
.hudm .gtwin .tag.owned { color: var(--dim); border-color: var(--fr); }
.hudm .gtwin .tag.mine { color: #000; background: #f3e08a; border-color: #f3e08a; }
.hudm .gtwin .tag.pend { color: var(--ac); border-color: var(--ac); }
.hudm .gtwin .det { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 14px 16px; gap: 12px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gtwin .dh { display: flex; align-items: center; gap: 12px; }
.hudm .gtwin .dh h3 { margin: 0; font-size: 18px; color: #fff; letter-spacing: .04em; } .hudm .gtwin .dh small { display: block; font-size: 11px; color: var(--dim); }
.hudm .gtwin .dh .tag { margin-left: auto; }
.hudm .gtwin .bp { display: flex; flex-direction: column-reverse; gap: 6px; perspective: 900px; }
.hudm .gtwin .fl { position: relative; display: flex; align-items: center; gap: 10px; padding: 8px 12px; border-radius: var(--rad); transform: rotateX(18deg); transform-origin: 50% 100%;
  background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 85%, transparent), color-mix(in srgb, var(--pb) 90%, transparent)); border: 1px solid color-mix(in srgb, var(--ac) 30%, var(--fr));
  box-shadow: 0 6px 12px rgba(0,0,0,.45); animation: hudm-gtfl .35s ease-out backwards; }
.hudm .gtwin .fl:nth-child(2) { animation-delay: .05s; } .hudm .gtwin .fl:nth-child(3) { animation-delay: .1s; } .hudm .gtwin .fl:nth-child(4) { animation-delay: .15s; }
@keyframes hudm-gtfl { from { opacity: 0; transform: rotateX(18deg) translateY(10px); } }
.hudm .gtwin .fl .lv { width: 78px; flex: none; font-size: 10px; font-weight: 800; letter-spacing: .1em; color: var(--ac); }
.hudm .gtwin .fl .lv small { display: block; font-size: 9px; color: var(--dim); font-weight: 600; letter-spacing: .04em; }
.hudm .gtwin .fl .am { display: flex; flex-wrap: wrap; gap: 4px; }
.hudm .gtwin .fl .am span { display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 9px; font-size: 10.5px; border: 1px solid var(--fr); background: rgba(0,0,0,.25); }
.hudm .gtwin .fl .am svg { width: 12px; height: 12px; fill: none; stroke: var(--ac); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .gtwin .fl .am span.mob svg { stroke: #ff8a7a; }
.hudm .gtwin .fl .map { margin-left: auto; font-size: 10px; color: var(--dim); }
.hudm .gtwin .card { border-radius: var(--rad); padding: 10px 12px; background: rgba(0,0,0,.22); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); font-size: 12px; }
.hudm .gtwin .card h4 { margin: 0 0 8px; font-size: 10px; letter-spacing: .16em; color: var(--ac); }
.hudm .gtwin .kv { display: grid; grid-template-columns: 110px 1fr; row-gap: 5px; } .hudm .gtwin .kv span { color: var(--dim); } .hudm .gtwin .kv b { color: #fff; font-weight: 600; }
.hudm .gtwin .kv b.gold { color: #f3e08a; }
.hudm .gtwin .tl { position: relative; height: 10px; margin: 8px 0 4px; border-radius: 5px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .gtwin .tl i { position: absolute; left: 0; top: 0; bottom: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, #000), var(--ac)); }
.hudm .gtwin .tl u { position: absolute; top: 0; bottom: 0; background: repeating-linear-gradient(135deg, rgba(111,224,138,.55) 0 4px, rgba(111,224,138,.25) 4px 8px); }
.hudm .gtwin .tll { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); }
.hudm .gtwin .acts { display: flex; flex-wrap: wrap; gap: 8px; }
.hudm .gtwin .b { height: 30px; padding: 0 14px; display: inline-flex; align-items: center; gap: 6px; border-radius: var(--rad); cursor: pointer; user-select: none; font-size: 12px; font-weight: 600;
  border: 1px solid var(--fr); background: linear-gradient(180deg, var(--pc), var(--pb)); color: var(--tx); }
.hudm .gtwin .b:hover { border-color: var(--ac); color: #fff; }
.hudm .gtwin .b.main { color: #fff; border-color: var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 55%, var(--pc)), color-mix(in srgb, var(--ac) 25%, var(--pb))); box-shadow: 0 0 10px var(--glow); }
.hudm .gtwin .b.no { opacity: .45; }
.hudm .gtwin .why { font-size: 11px; color: #ffb3a6; min-height: 14px; }
.hudm .gtwin .note { font-size: 11px; color: var(--dim); }
.hudm .gtwin .warp { position: absolute; inset: 0; z-index: 5; display: flex; align-items: center; justify-content: center; font-size: 20px; font-weight: 800; letter-spacing: .2em; color: #fff;
  background: radial-gradient(circle, color-mix(in srgb, var(--ac) 35%, transparent), rgba(2,4,10,.9)); text-shadow: 0 0 18px var(--glow); animation: hudm-gtwarp 1.2s ease-in-out forwards; }
@keyframes hudm-gtwarp { 0% { opacity: 0; } 30%, 70% { opacity: 1; } 100% { opacity: 0; } }
`;

export class TerritoryMock extends HudWindow {
  private estates: Estate[] = [];
  private sel = 0;
  private filter: "All" | "Vacant" | "For sale" | "Owned" = "All";
  private leader = true;
  private guildGold = 18_500_000;
  private why = "";
  private timer = 0;
  private loaded = false;

  constructor(private readonly host: TerritoryHost) {
    super("gtwin", 42, 40);
    injectCss("hudm-gt-css", CSS);
  }

  protected override shown(): void {
    if (!this.loaded) { this.loaded = true; void this.load(); return; }
    this.render();
    window.clearInterval(this.timer);
    this.timer = window.setInterval(() => this.tick(), 1000);
  }

  private async load(): Promise<void> {
    this.el.replaceChildren(h("div", "panel", this.titleBar("GUILD TERRITORIES", "Close"), h("div", "note", "Loading the estates…")));
    const rows = await fetch("lab-data/territories.json").then((r) => r.json() as Promise<Row[]>).catch(() => [] as Row[]);
    this.estates = rows.map((r) => { const o = OWNERS[r.gt]; return { gt: r.gt, floors: r.floors, owner: o?.[0] ?? "None", leader: o?.[1] ?? "None", days: o?.[2] ?? 0, price: o ? o[3] : BUY, handover: 0 }; });
    this.sel = this.estates.findIndex((e) => e.owner === "None");
    if (this.sel < 0) this.sel = 0;
    this.shown();
  }

  /** Once a second: the countdowns' text only (a full redraw replays the blueprint's animation); a redraw only when keys
   *  change hands. */
  private tick(): void {
    if (!this.open) { window.clearInterval(this.timer); return; }
    let done = false;
    for (const el of this.el.querySelectorAll<HTMLElement>("[data-until]")) {
      const t = +el.dataset.until!;
      if (t <= Date.now()) done = true; else el.textContent = this.left(t);
    }
    for (const e of this.estates) if (e.handover && e.handover <= Date.now()) { e.handover = 0; done = true; this.host.say(`The keys to Estate ${e.gt + 1} are yours.`); }
    if (done) this.render();
  }
  /** A countdown that tick() keeps current. */
  private cd(t: number): HTMLElement { const s = h("span", "", this.left(t)); s.dataset.until = String(t); return s; }
  /** The status chip (a pending handover counts down in place). */
  private tag(e: Estate): HTMLElement {
    const [st, label] = this.status(e);
    return st === "pend" ? h("span", "tag pend", "Keys in ", this.cd(e.handover)) : h("span", "tag " + st, label);
  }

  private mine(): Estate | undefined { return this.estates.find((e) => e.owner === MY_GUILD); }
  private status(e: Estate): ["vacant" | "sale" | "owned" | "mine" | "pend", string] {
    if (e.owner === MY_GUILD) return e.handover > Date.now() ? ["pend", "Keys pending"] : e.price ? ["sale", "Yours · for sale"] : ["mine", "Yours"];
    if (e.owner === "None") return ["vacant", "For rent"];
    return e.price ? ["sale", "For sale"] : ["owned", "Owned"];
  }
  private left(t: number): string { const s = Math.max(0, Math.floor((t - Date.now()) / 1000)); return `${Math.floor(s / 3600)}h ${String(Math.floor(s / 60) % 60).padStart(2, "0")}m ${String(s % 60).padStart(2, "0")}s`; }
  /** Where the lab camera is: inside this estate? (GTSale / GTCancelSale need CurrentMap.Info.GTIndex == the guild's.) */
  private inside(e: Estate): boolean { const m = this.host.world()?.map(); return !!m && e.floors.some((f) => f.f === m); }

  private render(): void {
    const panel = h("div", "panel", this.titleBar("GUILD TERRITORIES", "Close"));
    const my = this.mine();
    const role = h("div", "role");
    for (const [label, v] of [["Leader", true], ["Member", false]] as const) {
      const d = h("div", this.leader === v ? "on" : "", `View as ${label.toLowerCase()}`);
      d.addEventListener("click", () => { this.leader = v; this.why = ""; this.render(); });
      role.append(d);
    }
    panel.append(h("div", "top", h("div", "crest", crest(MY_GUILD)), h("div", "me", h("b", "", MY_GUILD), h("span", "", `${this.leader ? "Leader" : "Member"} · ${ME}`)),
      h("div", "stat", h("small", "", "GUILD GOLD"), h("b", "gold", fmt(this.guildGold))),
      h("div", "stat", h("small", "", "YOUR ESTATE"), my ? (my.handover > Date.now() ? h("b", "", `No. ${my.gt + 1} · keys in `, this.cd(my.handover)) : h("b", "", `No. ${my.gt + 1} · ${my.days} days left`)) : h("b", "", "None")), role));

    // list
    const counts = { All: this.estates.length, Vacant: this.estates.filter((e) => e.owner === "None").length, "For sale": this.estates.filter((e) => e.owner !== "None" && e.price).length, Owned: this.estates.filter((e) => e.owner !== "None").length };
    const chips = h("div", "chips");
    for (const f of ["All", "Vacant", "For sale", "Owned"] as const) {
      const c = h("div", this.filter === f ? "on" : "", f, h("i", "", String(counts[f])));
      c.addEventListener("click", () => { this.filter = f; this.render(); });
      chips.append(c);
    }
    const rows = h("div", "rows");
    for (const [i, e] of this.estates.entries()) {
      if (this.filter === "Vacant" && e.owner !== "None" || this.filter === "For sale" && !(e.owner !== "None" && e.price) || this.filter === "Owned" && e.owner === "None") continue;
      const lb = h("div", "lb" + (e.days && e.days <= 5 ? " low" : ""), h("i"));
      (lb.firstChild as HTMLElement).style.width = e.owner === "None" ? "0" : Math.min(100, e.days / DAYS * 100) + "%";
      const r = h("div", "er" + (i === this.sel ? " sel" : "") + (e.owner === MY_GUILD ? " mine" : ""), h("div", "num", String(e.gt + 1)),
        h("div", "nm", h("b", "", e.owner === "None" ? `Estate ${e.gt + 1}` : e.owner), h("span", "", e.owner === "None" ? `Court + 3 floors · ${fmt(BUY)} for ${DAYS} days` : `Leader ${e.leader} · ${e.days} days left${e.price && e.owner !== "None" ? ` · ${fmt(e.price)}` : ""}`)),
        this.tag(e), lb);
      r.addEventListener("click", () => { this.sel = i; this.why = ""; this.render(); });
      rows.append(r);
    }
    const e = this.estates[this.sel];
    panel.append(h("div", "body", h("div", "list", chips, rows), e ? this.detail(e) : h("div", "det", h("div", "note", "No estates."))));
    this.el.replaceChildren(panel);
  }

  private detail(e: Estate): HTMLElement {
    const det = h("div", "det");
    det.append(h("div", "dh", e.owner === "None" ? h("div", "num", String(e.gt + 1)) : this.crestSm(e.owner), h("div", "", h("h3", "", `Estate ${e.gt + 1}`), h("small", "", e.owner === "None" ? "Unclaimed · four levels" : `Held by ${e.owner}`)), this.tag(e)));

    // blueprint: court at the bottom, floors above
    const bp = h("div", "bp");
    for (const f of e.floors) {
      const am = h("div", "am");
      for (const n of f.npcs) {
        const a = AMENITY.find(([re]) => re.test(n));
        const s = h("span"); s.innerHTML = `<svg viewBox="0 0 24 24"><path d="${a?.[2] ?? "M12 12h.01"}"/></svg>`; s.append(a?.[1] ?? n.replace(/^GT_?/, "").replace(/_/g, " "));
        am.append(s);
      }
      for (const m of [...new Set(f.mobs)]) { const s = h("span", "mob"); s.innerHTML = `<svg viewBox="0 0 24 24"><path d="${MOB_ICON}"/></svg>`; s.append(m.replace(/(\D)(\d)$/, "$1 $2")); am.append(s); }
      if (!am.children.length) am.append(h("span", "", "Open hall"));
      bp.append(h("div", "fl", h("div", "lv", FLOOR_NAME[f.t] ?? f.t, h("small", "", f.t)), am, h("div", "map", f.f)));
    }
    det.append(bp);

    // facts + lease timeline
    const card = h("div", "card", h("h4", "", e.owner === "None" ? "LEASE TERMS" : "LEASE"));
    const kv = h("div", "kv");
    const add = (k: string, v: string | Node, cls = "") => kv.append(h("span", "", k), h("b", cls, v));
    if (e.owner === "None") { add("Rent", `${fmt(BUY)} guild gold`, "gold"); add("Lease", `${DAYS} days`); add("Extend", `${fmt(EXTEND)} for ${DAYS} more days`); add("Rules", "Guild leader only · one estate per guild"); }
    else {
      add("Guild", e.owner); add("Leader", e.leader); add("Lease left", `${e.days} of ${DAYS}+ days`);
      if (e.price) add("Asking price", `${fmt(e.price)} guild gold`, "gold");
      if (e.handover > Date.now()) add("Keys", h("span", "", "handed over in ", this.cd(e.handover)));
    }
    card.append(kv);
    if (e.owner !== "None") {
      const tl = h("div", "tl", h("i"), h("u"));
      (tl.firstChild as HTMLElement).style.width = Math.min(100, e.days / (DAYS * 2) * 100) + "%";
      if (e.owner === MY_GUILD) Object.assign((tl.lastChild as HTMLElement).style, { left: Math.min(100, e.days / (DAYS * 2) * 100) + "%", width: DAYS / (DAYS * 2) * 100 + "%" });
      card.append(tl, h("div", "tll", h("span", "", "today"), h("span", "", e.owner === MY_GUILD ? "striped: what Extend adds" : ""), h("span", "", `${DAYS * 2} days`)));
    }
    det.append(card);

    // actions
    const acts = h("div", "acts");
    const btn = (text: string, main: boolean, why: string, run: () => void) => {
      const b = h("div", "b" + (main ? " main" : "") + (why ? " no" : ""), text);
      if (why) b.title = why;
      b.addEventListener("click", () => { if (why) { this.why = why; this.host.say(why); this.render(); return; } void run(); });
      acts.append(b);
    };
    const lead = this.leader ? "" : "Only the guild leader can do that.";
    const my = this.mine();
    if (e.owner === "None") {
      btn(`Rent · ${fmt(BUY)}`, true, lead || (my ? "Your guild already owns a territory." : this.guildGold < BUY ? "The guild's funds are insufficient." : ""), async () => {
        if (!await this.host.confirm("Rent territory", `Rent Estate ${e.gt + 1} for ${fmt(BUY)} guild gold? The lease runs ${DAYS} days.`)) return;
        this.guildGold -= BUY; e.owner = MY_GUILD; e.leader = ME; e.days = DAYS; e.price = 0;
        this.host.say(`Your guild rented Estate ${e.gt + 1} for ${DAYS} days.`); this.render();
      });
    } else if (e.owner === MY_GUILD) {
      btn("Teleport in", true, e.handover > Date.now() ? "The recent sale is still pending: the keys change hands 24 h after a purchase." : "", () => this.teleport(e));
      // The 24 h handover is the server's rule (GTBegin = now + 1 day); the lab can skip it.
      if (e.handover > Date.now()) btn("Skip the wait (lab)", false, "", () => { e.handover = 0; this.why = ""; this.host.say(`The keys to Estate ${e.gt + 1} are yours (lab: skipped the 24 h handover).`); this.render(); });
      btn(`Extend · ${fmt(EXTEND)}`, false, lead || (this.guildGold < EXTEND ? "Insufficient funds." : ""), () => { this.guildGold -= EXTEND; e.days += DAYS; this.host.say(`Lease extended by ${DAYS} days (${e.days} days left).`); this.render(); });
      if (!e.price) btn("List for sale…", false, lead || (!this.inside(e) ? "Stand in your territory to put it up for sale (Teleport in first)." : ""), async () => {
        const n = await this.host.amount("Sell territory", `Asking price for Estate ${e.gt + 1} (at least ${fmt(MIN_SALE)}).`, MIN_SALE, 500_000_000, 15_000_000);
        if (!n) return;
        e.price = n; this.host.say(`Estate ${e.gt + 1} is listed for sale at ${fmt(n)}.`); this.render();
      });
      else btn("Cancel sale", false, lead || (!this.inside(e) ? "Stand in your territory to cancel the sale." : ""), () => { e.price = 0; this.host.say("The territory sale was cancelled."); this.render(); });
    } else if (e.price) {
      btn(`Buy · ${fmt(e.price)}`, true, lead || (my ? "Your guild already owns a territory." : this.guildGold < e.price ? "Insufficient funds." : ""), async () => {
        if (!await this.host.confirm("Buy territory", `Buy Estate ${e.gt + 1} from ${e.owner} for ${fmt(e.price)} guild gold?\nThe keys change hands after 24 hours.`)) return;
        this.guildGold -= e.price; this.host.say(`Purchase made: ${e.owner} has been paid. The territory will be yours in 24 hours.`);
        e.owner = MY_GUILD; e.leader = ME; e.days = DAYS + 1; e.price = 0; e.handover = Date.now() + 24 * 3600_000; this.render();
      });
    } else acts.append(h("span", "note", "Not for sale. Its lease ends in " + e.days + " days."));
    btn("Look around (lab)", false, "", () => this.teleport(e, true));
    det.append(acts, h("div", "why", this.why));
    return det;
  }

  private crestSm(name: string): HTMLElement {
    const c = h("div", "crest sm", crest(name));
    let x = 0; for (const ch of name) x = (x * 31 + ch.charCodeAt(0)) >>> 0;
    c.style.setProperty("--cc", `hsl(${200 + (x % 50)} 70% 55%)`); // stays in the blue family
    return c;
  }

  /** TeleportGT: to the court's safe zone (the lab's camera loads the map). */
  private teleport(e: Estate, look = false): void {
    const w = this.host.world(), court = e.floors[0]!;
    const [x, y] = court.zone ?? [50, 50];
    this.el.querySelector(".panel")?.append(h("div", "warp", look ? "VIEWING ESTATE " + (e.gt + 1) : "ENTERING YOUR ESTATE"));
    window.setTimeout(() => {
      if (w) { if (w.map() === court.f) w.walk(x, y); else { w.travel(court.f); let n = 0; const t = window.setInterval(() => { if (w.map() === court.f || ++n > 50) { window.clearInterval(t); w.walk(x, y); this.render(); } }, 200); } }
      this.host.say(look ? `Viewing Estate ${e.gt + 1} (${court.f}).` : `Welcome to your estate (${court.f} ${x}, ${y}).`);
      this.why = ""; this.render();
    }, 700);
  }
}
