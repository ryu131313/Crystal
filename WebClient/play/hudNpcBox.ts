// NPC item box for the scene lab's UI tester (2026-10-15), a modern take on the desktop's NPCDropDialog: the NPC
// services that take one item from your bag (PanelType Disassemble, Downgrade, Reset, ReplaceWedRing; repair and
// special repair are the merchant's Repair tab since 2026-10-16, hudMerchant.ts + hudRepair.ts, which covers worn gear
// and the bag in one list; selling, consigning and refining have their own windows). Drag an item onto the altar (or click it in
// the bag strip, or right-click it); the bag strip dims what this service can't take and says why. The right side
// shows what will happen before you pay: the awakening level going down, the added stats being wiped, what a disassembly can give back. Rules and prices are the
// game's (UserItem.DisassemblePrice / DowngradePrice / ResetPrice, PlayerObject.DisassembleItem / DowngradeAwakening /
// ResetAddedItem, NPCDropDialog's checks): disassembly needs a graded,
// non-awakening item, destroys it and gives an awakening material of its grade (sometimes one grade off, counts as the
// server's); downgrading always removes one awakening level (1 in 20 also costs 1 max durability); a reset wipes the
// added stats. Disassembly needs the button held. The item sits on the altar (out of the bag) until the service runs or
// the window closes. Per-item state is the item tooltip's (hudTooltip.ts copyOf / editCopy / setDura), so the tooltip
// and the HUD's durability strip agree. Mock only: nothing is sent anywhere.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { BAG_SLOTS, addToBag, bag, bagChanged, info, onBag, type Stack } from "./hudItems.ts";
import { bestCell, bindCell, host as dragHost, registerHost, transfer, type DragHost } from "./hudDrag.ts";
import { GRADE, copyOf, editCopy } from "./hudTooltip.ts";

export type Service = "disassemble" | "downgrade" | "reset" | "wedring";
export interface NpcBoxHost { gold(): number; spend(n: number): boolean; say(text: string): void; gearChanged(): void }

const GRADE_N: Record<string, number> = { None: 0, Common: 1, Rare: 2, Legendary: 3, Mythical: 4, Heroic: 5 };
const RING_COST = 125; // Settings.ReplaceWedRingCost (x 10 x the new ring's RequiredAmount)
/** [title, button, icon path, what it does]. */
const SVC: Record<Service, [string, string, string, string]> = {
  disassemble: ["Disassemble", "Disassemble", "M4 20l7-7M13 11l7-7M9 4l3 3-2 2-3-3zM15 14l3 3-2 2-3-3z", "Breaks a graded item down into awakening materials. The item is destroyed."],
  downgrade: ["Downgrade awakening", "Downgrade", "M12 4v12M6 10l6 6 6-6M5 20h14", "Removes the last awakening level from an item."],
  reset: ["Reset stats", "Reset", "M4 12a8 8 0 1 0 3-6.2M4 4v4h4", "Wipes an item's added stats back to its base stats."],
  wedring: ["Replace wedding ring", "Replace ring", "M12 9a6 6 0 110 12 6 6 0 010-12zM9 3h6l-3 6z", "Moves your marriage bond onto another ring."],
};
const ORDER: Service[] = ["disassemble", "downgrade", "reset", "wedring"];
const GLYPH = ["Bravery", "Magic", "Soul", "Protection", "EvilSlayer", "Body"];
const fmt = (n: number) => Math.round(n).toLocaleString("en-GB");

const CSS = `
.hudm .nbwin { width: 640px; }
.hudm .nbwin .panel { display: flex; flex-direction: column; }
.hudm .nbwin .svcs { display: flex; gap: 4px; padding: 10px 14px 0; flex-wrap: wrap; }
.hudm .nbwin .svcs div { display: flex; align-items: center; gap: 6px; padding: 4px 10px; border-radius: 12px; font-size: 11px; font-weight: 600; cursor: pointer; color: var(--dim); border: 1px solid var(--fr); }
.hudm .nbwin .svcs svg { width: 13px; height: 13px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .nbwin .svcs div:hover { color: #fff; border-color: var(--ac); }
.hudm .nbwin .svcs div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 28%, transparent); box-shadow: 0 0 8px var(--glow); }
.hudm .nbwin .blurb { padding: 8px 16px 0; font-size: 11.5px; color: var(--dim); }
.hudm .nbwin .blurb b { color: var(--ac); }
.hudm .nbwin .main { display: flex; gap: 14px; padding: 12px 14px; }
/* altar */
.hudm .nbwin .altar { position: relative; width: 230px; height: 230px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: var(--rad);
  background: radial-gradient(circle at 50% 55%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 62%), radial-gradient(ellipse 70% 20% at 50% 88%, rgba(0,0,0,.6), transparent); }
.hudm .nbwin .ring { position: absolute; width: 150px; height: 150px; border-radius: 50%; border: 1px dashed color-mix(in srgb, var(--ac) 45%, transparent); animation: hudm-nbspin 18s linear infinite; }
.hudm .nbwin .ring.b { width: 180px; height: 180px; border-style: solid; border-color: color-mix(in srgb, var(--fr) 70%, transparent); animation-duration: 40s; animation-direction: reverse; }
.hudm .nbwin .ring::before { content: ""; position: absolute; left: 50%; top: -4px; width: 7px; height: 7px; margin-left: -3.5px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 10px var(--ac); }
@keyframes hudm-nbspin { to { transform: rotate(360deg); } }
.hudm .nbwin .altar.full .ring { animation-duration: 6s; border-color: var(--ac); }
.hudm .nbwin .altar.danger .ring { border-color: #ff5a4a; } .hudm .nbwin .altar.danger .ring::before { background: #ff5a4a; box-shadow: 0 0 10px #ff5a4a; }
.hudm .nbwin .slot { position: relative; z-index: 1; width: 76px; height: 76px; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  background: radial-gradient(circle at 50% 35%, var(--pc), #05070b 75%); border: 2px solid var(--fr); box-shadow: 0 0 0 1px #000, inset 0 0 14px #000; }
.hudm .nbwin .slot .eq { width: 76px; height: 76px; border-radius: 50%; border: 0; background: transparent; box-shadow: none; }
.hudm .nbwin .slot .eq::after { display: none; }
.hudm .nbwin .slot .eq i { transform: scale(1.5); }
.hudm .nbwin .altar.full .slot { border-color: var(--gc, var(--ac)); box-shadow: 0 0 0 1px #000, 0 0 22px color-mix(in srgb, var(--gc, var(--ac)) 55%, transparent), inset 0 0 14px #000; }
.hudm .nbwin .altar .nm { position: absolute; bottom: 8px; left: 0; right: 0; text-align: center; font-size: 13px; font-weight: 700; color: var(--gc, #fff); text-shadow: 0 0 8px #000; }
.hudm .nbwin .altar .nm small { display: block; font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .nbwin .altar .empty { position: absolute; bottom: 14px; left: 10px; right: 10px; text-align: center; font-size: 11px; color: var(--dim); }
.hudm .nbwin .altar.work .slot { animation: hudm-nbwork .9s ease-in-out; } @keyframes hudm-nbwork { 30% { transform: scale(1.12); box-shadow: 0 0 40px var(--ac); } 60% { transform: scale(.94); } }
.hudm .nbwin .altar.shatter .slot .eq i { animation: hudm-nbshatter .8s ease-in forwards; } @keyframes hudm-nbshatter { to { transform: scale(2.4) rotate(25deg); opacity: 0; filter: blur(6px) brightness(3); } }
.hudm .nbwin .spark { position: absolute; left: 50%; top: 50%; width: 4px; height: 4px; border-radius: 50%; background: var(--sc, var(--ac)); box-shadow: 0 0 6px var(--sc, var(--ac)); pointer-events: none; }
/* preview */
.hudm .nbwin .prev { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; }
.hudm .nbwin .card { flex: 1; border-radius: var(--rad); padding: 10px 12px; background: rgba(0,0,0,.22); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); font-size: 12px; }
.hudm .nbwin .card h4 { margin: 0 0 8px; font-size: 10px; letter-spacing: .16em; color: var(--ac); }
.hudm .nbwin .row { display: flex; align-items: center; gap: 8px; min-height: 22px; }
.hudm .nbwin .row > span:first-child { width: 100px; color: var(--dim); flex: none; }
.hudm .nbwin .row b { color: #fff; font-variant-numeric: tabular-nums; } .hudm .nbwin .row .ar { color: var(--ac); } .hudm .nbwin .row .bad { color: #ff8a7a; } .hudm .nbwin .row .good { color: #6fe08a; }
.hudm .nbwin .row s { color: var(--dim); }
.hudm .nbwin .stars { color: var(--ac); letter-spacing: 2px; } .hudm .nbwin .stars s { color: #ff8a7a; text-decoration: line-through; }
.hudm .nbwin .loot { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 4px; }
.hudm .nbwin .loot i { width: 28px; height: 28px; border-radius: 6px; background: center / auto no-repeat, radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); image-rendering: pixelated; }
.hudm .nbwin .warn { padding: 6px 10px; border-radius: var(--rad); font-size: 11px; color: #ffc8c0; background: rgba(120,20,20,.35); border: 1px solid #c44; }
.hudm .nbwin .hint { color: var(--dim); font-size: 11.5px; line-height: 1.5; }
.hudm .nbwin .pay { display: flex; align-items: center; gap: 10px; }
.hudm .nbwin .cost { display: flex; align-items: center; gap: 6px; font-size: 15px; font-weight: 800; color: #f3e08a; font-variant-numeric: tabular-nums; }
.hudm .nbwin .cost i { width: 22px; height: 22px; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .nbwin .cost.poor { color: #ff8a7a; }
.hudm .nbwin .cost small { font-size: 10px; font-weight: 600; color: var(--dim); }
.hudm .nbwin .go { margin-left: auto; position: relative; overflow: hidden; min-width: 130px; height: 32px; display: flex; align-items: center; justify-content: center; cursor: pointer; user-select: none;
  border-radius: var(--rad); font-weight: 700; font-size: 12px; letter-spacing: .06em; color: #fff; border: 1px solid var(--ac);
  background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 55%, var(--pc)), color-mix(in srgb, var(--ac) 25%, var(--pb))); box-shadow: 0 0 0 1px #000, 0 0 12px var(--glow); }
.hudm .nbwin .go.red { border-color: #ff5a4a; background: linear-gradient(180deg, rgba(200,60,50,.75), rgba(90,20,20,.9)); box-shadow: 0 0 0 1px #000, 0 0 12px rgba(255,90,74,.45); }
.hudm .nbwin .go.dis { opacity: .4; pointer-events: none; }
.hudm .nbwin .go .fill { position: absolute; left: 0; top: 0; bottom: 0; width: 0; background: rgba(255,255,255,.25); }
.hudm .nbwin .go span { position: relative; }
/* bag strip */
.hudm .nbwin .bagl { display: flex; justify-content: space-between; padding: 0 16px; font-size: 10px; font-weight: 700; letter-spacing: .14em; color: var(--dim); }
.hudm .nbwin .bagl b { letter-spacing: 0; color: var(--tx); font-weight: 600; }
.hudm .nbwin .bagg { display: grid; grid-template-columns: repeat(10, 1fr); gap: 4px; padding: 6px 14px 14px; }
.hudm .nbwin .bagg .eq { width: auto; height: 52px; }
.hudm .nbwin .bagg .eq.no { opacity: .28; filter: grayscale(1); }
.hudm .nbwin .bagg .eq.yes { border-color: color-mix(in srgb, var(--ac) 60%, var(--fr)); }
.hudm .nbwin .res { position: absolute; left: 50%; top: 18px; translate: -50% 0; z-index: 3; white-space: nowrap; padding: 4px 12px; border-radius: 12px; font-size: 12px; font-weight: 700; color: #fff;
  background: rgba(8,12,20,.9); border: 1px solid var(--ac); box-shadow: 0 0 14px var(--glow); animation: hudm-nbres 2.4s ease-out forwards; }
@keyframes hudm-nbres { 0% { opacity: 0; transform: translateY(8px); } 12%, 80% { opacity: 1; transform: none; } 100% { opacity: 0; } }
`;

export class NpcBoxMock extends HudWindow {
  private svc: Service = "disassemble";
  private slot: Stack | null = null;
  private npcName = "Bill the Blacksmith";
  private busy = false;
  private me: DragHost;
  private alt!: HTMLElement;

  constructor(private readonly host: NpcBoxHost) {
    super("nbwin", 192, 60);
    injectCss("hudm-npcbox-css", CSS);
    this.me = registerHost({
      id: "npcbox", size: 1, live: () => this.open && !this.busy,
      get: () => this.slot,
      set: (_i, st) => { this.slot = st; this.render(); },
      accepts: (_i, st) => this.busy ? "Busy" : this.why(st),
      verb: () => SVC[this.svc][1],
      quick: () => this.slot ? bestCell(dragHost("bag"), this.slot, this.me) : null,
      after: () => this.render(),
    });
    onBag(() => { if (this.open) this.render(); });
  }

  /** At an NPC: opens on one service. */
  openService(svc: Service, npc = "Bill the Blacksmith"): void {
    this.svc = svc; this.npcName = npc;
    this.toggle(true);
  }

  override toggle(show = this.el.hidden): void {
    if (!show) this.giveBack();
    super.toggle(show);
  }
  protected override shown(): void { this.render(); }

  /** The item on the altar goes back to the bag. */
  private giveBack(): void {
    if (!this.slot || this.busy) return;
    const left = addToBag(this.slot);
    // What does not fit stays on the altar (shown again next time) instead of vanishing.
    if (left) { this.slot = { ...this.slot, count: left }; this.host.say(`Your bag is full: ${this.slot.name} stays on the altar.`); }
    else this.slot = null;
    bagChanged();
  }

  /** NPCDropDialog's / the server's checks: "" or why this service can't take the item. */
  private why(s: Stack): string {
    const it = info(s.name);
    if (!it) return "Unknown item";
    const cp = copyOf(it), g = GRADE_N[it.grade] ?? 0;
    switch (this.svc) {
      case "disassemble": return g === 0 || it.type === "Awakening" ? "Only graded items (Common or better)" : "";
      case "downgrade": return cp.awake ? "" : "Not awakened";
      case "reset": return Object.keys(cp.added).length ? "" : "No added stats to reset";
      case "wedring": return it.type === "Ring" ? "" : "Rings only";
    }
  }

  /** The service's price (UserItem.DisassemblePrice / DowngradePrice / ResetPrice, the wedding ring cost). */
  private cost(s: Stack): number {
    const it = info(s.name)!, cp = copyOf(it), g = GRADE_N[it.grade] ?? 0, added = Object.keys(cp.added).length;
    switch (this.svc) {
      case "disassemble": return Math.floor(1500 * g * ((added + cp.awake) * 0.1 + 1));
      case "downgrade": return 3000 * (1 + (cp.awake + 1) * 2) * g;
      case "reset": return Math.floor(3000 * g * (added * 0.2 + 1));
      case "wedring": return it.req * 10 * RING_COST; // PlayerObject.ReplaceWeddingRing
    }
  }

  private render(): void {
    const panel = h("div", "panel", this.titleBar(SVC[this.svc][0].toUpperCase(), "Close (the item goes back to your bag)"));
    const svcs = h("div", "svcs");
    for (const s of ORDER) {
      const c = h("div", s === this.svc ? "on" : "");
      c.innerHTML = `<svg viewBox="0 0 24 24"><path d="${SVC[s][2]}"/></svg>`;
      c.append(SVC[s][0]);
      c.addEventListener("click", () => { if (this.busy) return; this.svc = s; if (this.slot && this.why(this.slot)) this.giveBack(); this.render(); });
      svcs.append(c);
    }
    const blurb = h("div", "blurb", h("b", "", this.npcName), " · ", SVC[this.svc][3]);

    // altar
    const it = this.slot ? info(this.slot.name) : undefined;
    const slotEl = h("div", "slot");
    const cell = this.slot ? itemCell(this.el, this.slot.name, this.slot.img, this.slot.count) : itemCell(this.el, "", -1);
    if (!this.slot) cell.title = "Drag an item here";
    bindCell(cell, "npcbox", 0);
    slotEl.append(cell);
    this.alt = h("div", "altar" + (this.slot ? " full" : "") + (this.svc === "disassemble" ? " danger" : ""), h("div", "ring b"), h("div", "ring"), slotEl);
    if (it) { this.alt.style.setProperty("--gc", GRADE[it.grade] ?? "#fff"); this.alt.append(h("div", "nm", this.slot!.name, h("small", "", `${it.grade === "None" ? "" : it.grade + " "}${it.type}`))); }
    else this.alt.append(h("div", "empty", "Drag an item here, or click one in your bag below"));

    // preview + pay
    const prev = h("div", "prev", this.preview());
    const goldIco = h("i");
    void libPicture(Lib.Items, 116).then((p) => { if (p) goldIco.style.backgroundImage = `url(${p.url})`; });
    const c = this.slot ? this.cost(this.slot) : 0, poor = c > this.host.gold();
    const costEl = h("div", "cost" + (poor ? " poor" : ""), goldIco, this.slot ? fmt(c) : "—", h("small", "", ` / ${fmt(this.host.gold())} gold`));
    const danger = this.svc === "disassemble";
    const go = h("div", "go" + (danger ? " red" : "") + (!this.slot || poor || this.busy ? " dis" : ""), h("span", "", danger ? `Hold to ${SVC[this.svc][1].toLowerCase()}` : SVC[this.svc][1]));
    go.title = !this.slot ? "Put an item on the altar first" : poor ? "Not enough gold" : "";
    if (danger) {
      const fill = h("div", "fill"); go.prepend(fill);
      let anim: Animation | null = null;
      go.addEventListener("mousedown", () => { anim = fill.animate([{ width: "0%" }, { width: "100%" }], { duration: 1100, fill: "forwards" }); anim.onfinish = () => this.run(); });
      const stop = () => { if (anim && anim.playState !== "finished") anim.cancel(); };
      go.addEventListener("mouseup", stop); go.addEventListener("mouseleave", stop);
    } else go.addEventListener("click", () => this.run());
    prev.append(h("div", "pay", costEl, go));

    // bag strip
    const bagg = h("div", "bagg");
    let fit = 0;
    bag.forEach((b, i) => {
      const cl = b ? itemCell(this.el, b.count > 1 ? `${b.name} (${b.count})` : b.name, b.img, b.count) : itemCell(this.el, "", -1);
      if (b) {
        const why = this.why(b);
        cl.classList.add(why ? "no" : "yes");
        if (why) cl.title = `${b.name}: ${why}`; else fit++;
        cl.addEventListener("click", () => { if (this.busy || why) return; transfer(dragHost("bag")!, i, this.me, 0); });
      }
      bindCell(cl, "bag", i);
      bagg.append(cl);
    });
    panel.append(svcs, blurb, h("div", "main", this.alt, prev), h("div", "bagl", h("span", "", "YOUR BAG"), h("b", "", `${fit} item${fit === 1 ? "" : "s"} this service takes · ${bag.filter(Boolean).length}/${BAG_SLOTS}`)), bagg);
    this.el.replaceChildren(panel);
  }

  private preview(): HTMLElement {
    const card = h("div", "card", h("h4", "", "WHAT HAPPENS"));
    if (!this.slot) { card.append(h("div", "hint", "Put an item on the altar to see the result and the price before you pay.\nItems this service can't take are dimmed in your bag (hover them for why).")); return card; }
    const it = info(this.slot.name)!, cp = copyOf(it), g = GRADE_N[it.grade] ?? 0;
    const row = (label: string, ...kids: (Node | string)[]) => h("div", "row", h("span", "", label), ...kids);
    switch (this.svc) {
      case "disassemble": {
        const quality = Object.keys(cp.added).length + cp.awake + 1, most = Math.max(1, Math.floor(g * it.req / 10 + quality) - 1);
        const loot = h("div", "loot");
        for (const gg of [g - 1, g, g + 1].filter((x) => x >= 1 && x <= 4)) for (let t = 0; t < 7; t++) {
          const i = h("i"); const img = t < 6 ? 3200 + t * 4 + gg - 1 : 3224 + gg - 1;
          i.title = t < 6 ? `${GLYPH[t]}Glyph${gg - 1}` : `AwakeningSoul${gg - 1}`;
          if (gg !== g) i.style.opacity = ".45";
          void libPicture(Lib.Items, img).then((p) => { if (p) i.style.backgroundImage = `url(${p.url}), radial-gradient(circle at 50% 35%, var(--pc), var(--pb))`; });
          loot.append(i);
        }
        card.append(h("div", "warn", `${this.slot.name} will be destroyed.`), row("You get", h("b", "", `1 material, up to ${most}`)), row("Grade", h("b", "", `${it.grade}`), h("span", "", " (sometimes one grade off)")), loot);
        break;
      }
      case "downgrade":
        card.append(row("Awakening", h("span", "stars", "★".repeat(cp.awake - 1), h("s", "", "★"), "☆".repeat(5 - cp.awake))), row("Level", h("b", "", String(cp.awake)), h("span", "ar", "→"), h("b", "", String(cp.awake - 1))),
          row("Risk", h("span", "", "1 in 20: −1 max durability")));
        break;
      case "reset":
        for (const [s, v] of Object.entries(cp.added)) card.append(row(s.replace(/^Max/, "").replace(/([a-z])([A-Z])/g, "$1 $2"), h("s", "", `+${v}`), h("span", "ar", "→"), h("b", "", "+0")));
        card.append(h("div", "hint", "The item keeps its base stats, sockets and awakening."));
        break;
      case "wedring":
        card.append(row("Bond", h("b", "", "Elowen ♥"), h("span", "ar", "→"), h("b", "good", this.slot.name)), h("div", "hint", `Your old wedding ring becomes an ordinary ring again. Price: the new ring's level (${it.req}) x 10 x ${RING_COST} gold.`));
        break;
    }
    return card;
  }

  /** Pays and does it (the server's outcome), with the altar's effect. */
  private run(): void {
    const s = this.slot;
    if (!s || this.busy) return;
    const why = this.why(s);
    if (why) { this.host.say(why); return; }
    const c = this.cost(s);
    if (!this.host.spend(c)) { this.host.say("You don't have enough gold."); return; }
    const it = info(s.name)!, cp = copyOf(it), g = GRADE_N[it.grade] ?? 0;
    this.busy = true;
    const destroy = this.svc === "disassemble";
    this.alt.classList.add(destroy ? "shatter" : "work");
    this.sparks(destroy ? "#ff8a5a" : undefined);
    let msg = "", later: (() => void) | null = null;
    switch (this.svc) {
      case "disassemble": {
        const roll = Math.random(), gg = Math.max(1, Math.min(4, roll < 0.15 ? g - 1 : roll > 0.85 ? g + 1 : g));
        const t = Math.floor(Math.random() * 7), name = t < 6 ? `${GLYPH[t]}Glyph${gg - 1}` : `AwakeningSoul${gg - 1}`;
        const quality = Object.keys(cp.added).length + cp.awake + 1, n = 1 + Math.floor(Math.random() * Math.max(1, Math.floor(g * it.req / 10 + quality) - 1));
        msg = `${s.name} broke down into ${name} x${n}`;
        later = () => { if (addToBag({ name, img: t < 6 ? 3200 + t * 4 + gg - 1 : 3224 + gg - 1, count: n })) this.host.say(`No room for ${name}: sent to your mailbox.`); };
        break;
      }
      case "downgrade": {
        editCopy(s.name, { awake: cp.awake - 1 });
        if (Math.random() < 0.05) editCopy(s.name, { max: Math.max(0, cp.max - 1000) / it.dura });
        msg = `${s.name} is now awakening level ${cp.awake - 1}`;
        break;
      }
      case "reset": editCopy(s.name, { added: {} }); msg = `${s.name}'s added stats were reset`; break;
      case "wedring": msg = `Your marriage bond now lives in ${s.name}`; break;
    }
    window.setTimeout(() => {
      this.busy = false;
      if (destroy) this.slot = null;
      later?.();
      if (later) bagChanged();
      this.host.say(`${msg} (−${fmt(c)} gold).`);
      this.host.gearChanged();
      this.render();
      const r = h("div", "res", msg); this.alt.append(r);
    }, 900);
  }

  private sparks(colour?: string): void {
    for (let i = 0; i < 18; i++) {
      const s = h("div", "spark");
      if (colour) s.style.setProperty("--sc", colour);
      const a = Math.random() * Math.PI * 2, d = 60 + Math.random() * 60;
      this.alt.append(s);
      s.animate([{ transform: "translate(-50%,-50%) scale(1)", opacity: 1 }, { transform: `translate(${Math.cos(a) * d - 2}px, ${Math.sin(a) * d - 2}px) scale(.3)`, opacity: 0 }],
        { duration: 600 + Math.random() * 400, easing: "cubic-bezier(.2,.7,.3,1)", fill: "forwards" }).onfinish = () => s.remove();
    }
  }
}
