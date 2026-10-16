// Mail window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// MailListDialog / MailReadLetterDialog / MailComposeLetterDialog / MailComposeParcelDialog around Crystal's ClientMail
// (SenderName, Message, Opened, Locked, CanReply, Collected, DateSent, Gold, Items): folders (Inbox, Unread, Attachments,
// Locked, Sent) with counts, search, a mail list (sender badge, subject + preview, gold / item / lock markers, unread
// dot, age), Collect all / Mark all read, a reading pane (collect the gold and items, lock, reply, delete; locked mail
// cannot be deleted, a parcel with gold / items asks first and the attachments are lost, like MailDialogs.cs) and a
// composer (recipient suggestions, item slots filled from the bag: 1 without a stamp, 5 with one, like
// PlayerObject.SendMail; gold, the stamp, postage worked out like the server's GetMailCost: Settings.MailCostPer1KGold
// 100 per whole 1,000 gold, MailItemInsurancePercentage 5 % of each item's worth rounded down, free with a stamp when
// MailFreeWithStamp). `collected` is ClientMail.Collected: the parcel has reached you and can be collected; until then
// it waits for a post office NPC (NPCScript CollectParcelKey) unless the server auto-sends. Collecting empties the
// mail's gold and items (CollectMail). Mock data; reward item Image numbers are from a copy of the live Server.MirDB
// (2026-10-15).

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Folder = "Inbox" | "Unread" | "Attachments" | "Locked" | "Sent";
type Item = [string, number, number, number]; // name, Items.Lib image, count, worth (gold, for insurance)
interface Mail {
  id: number; from: string; to: string; system: boolean; subject: string; body: string; ago: string; minutes: number;
  opened: boolean; locked: boolean; collected: boolean; gold: number; items: Item[]; sent?: boolean;
}
const COST_PER_1K = 100, INSURANCE = 5, MAX_ITEMS = 5;

let id = 1;
const m = (from: string, system: boolean, subject: string, body: string, minutes: number, gold = 0, items: Item[] = [], opened = false, locked = false): Mail =>
  ({ id: id++, from, to: "GMRyu", system, subject, body, ago: age(minutes), minutes, opened, locked, collected: minutes < 1000, gold, items });
function age(min: number): string { return min < 60 ? `${min} min` : min < 1440 ? `${Math.round(min / 60)} h` : `${Math.round(min / 1440)} d`; }
const MAILS: Mail[] = [
  m("Auction House", true, "Item sold: DragonSlayer", "Your DragonSlayer sold for 2,400,000 gold.\nThe auction fee (5 %) has been taken. Collect your gold below.", 12, 2_280_000),
  m("Seren", false, "Pots for tonight ♥", "Left you some sun potions for the Bone Cave run. Don't forget the amulets this time!\n\nSee you at 8.", 40, 0, [["SunPotion(M)", 312, 20, 6000], ["Amulet", 270, 200, 4000]]),
  m("Game Master", true, "Weekend event: double EXP", "Double EXP runs this Saturday and Sunday, 18:00 - 23:00 server time.\nHave fun, and please report any problems to a GM.", 95, 0, [], false, true),
  m("GameMaster (guild)", true, "Guild war declared: Nightfall", "Nightfall has declared war on GameMaster.\nThe war lasts 3 hours. Guild members may attack Nightfall members anywhere outside safe zones.", 180, 0, [], true),
  m("Kaelith", false, "Raid loot split", "Here's your share from Sunday's raid. The ring rolled to you.", 600, 350_000, [["WhiteGoldRing", 773, 1, 80000]], true),
  m("Quest Board", true, "Reward: The Bone Elite", "Thank you for slaying the Bone Elite. Your reward is attached.", 1500, 30_000, [["HealthStone(M)", 586, 1, 12000], ["RecallNecklace", 247, 1, 45000]], true, false),
  m("Brannoc", false, "re: ice storm book", "Still got the book if you want it. 150k and it's yours, mail me the gold and I'll send it over.", 2900, 0, [], true),
  m("Auction House", true, "Listing expired: BlackTigerArmour(M)", "Nobody bought your BlackTigerArmour(M). The item has been returned.", 4300, 0, [["BlackTigerArmour(M)", 1030, 1, 200000]], true),
  m("Game Master", true, "Welcome to Crystal!", "Welcome, GMRyu!\n\nA starter pack is attached. Visit the Bichon Wall captain for your first quests.\nGood luck out there.", 20000, 10_000, [["TownTeleport", 402, 5, 500], ["SunPotion", 16, 10, 300]], true, true),
];
// The welcome pack was collected long ago: CollectMail empties the mail's gold and items.
for (const x of MAILS) if (x.from === "Game Master" && x.subject.startsWith("Welcome")) { x.collected = true; x.gold = 0; x.items = []; }
const SENT: Mail[] = [
  { ...m("GMRyu", false, "Bone Cave tonight?", "Group up at 8? I'll bring pots.", 300, 0, [], true), to: "Seren", sent: true, collected: true },
  { ...m("GMRyu", false, "Your share", "Thanks for the help with the war.", 3000, 100_000, [], true), to: "Thorne", sent: true, collected: true },
];
/** Bag items the composer can attach (name, image, count, worth). */
const BAG: Item[] = [["SunPotion(M)", 312, 8, 6000], ["(HP)DrugXL", 813, 20, 2000], ["(MP)DrugXL", 814, 20, 2000], ["TownTeleport", 402, 5, 500], ["DragonSword", 48, 1, 150000],
  ["HeavenSword", 74, 1, 300000], ["SpiritNecklace", 249, 1, 60000], ["RedOrchidRing", 433, 1, 90000], ["SmashWheel", 541, 1, 70000], ["BenedictionOil", 26, 1, 250000], ["Candle", 130, 1, 100]];
const CONTACTS = ["Seren", "Kaelith", "Brannoc", "Wren", "Thorne", "Isolde", "Mordrake", "Pip", "Grimwald", "Nyra"];

const ICON = {
  inbox: "M3 13l3-8h12l3 8v6H3zM3 13h5l1 3h6l1-3h5",
  unread: "M3 6h18v12H3zM3 6l9 7 9-7",
  clip: "M8 12l6-6a3 3 0 014 4l-8 8a5 5 0 01-7-7l7-7",
  lock: "M6 11h12v10H6zM8 11V7a4 4 0 018 0v4",
  sent: "M3 11l18-8-8 18-2-8z",
  compose: "M4 20h4L19 9l-4-4L4 16zM13 7l4 4",
  coin: "M12 3a9 9 0 110 18 9 9 0 010-18zM12 7a5 5 0 110 10 5 5 0 010-10z",
  trash: "M5 7h14M10 7V4h4v3M6 7l1 13h10l1-13",
  reply: "M10 8L4 13l6 5M4 13h11a5 5 0 015 5",
  star: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z",
  stamp: "M5 4h14v16H5zM8 8h8v6H8zM5 4l1 1M19 4l-1 1M5 20l1-1M19 20l-1-1",
};
const svgIcon = (d: string, cls = "ml-ic") => {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", cls);
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
};
const fmt = (n: number) => n.toLocaleString("en-GB");
const initials = (n: string) => n.replace(/[^A-Za-z ]/g, "").split(" ").filter(Boolean).map((p) => p[0]).join("").slice(0, 2).toUpperCase() || "?";

const CSS = `
.hudm .xwin { --winh: 560px; width: 920px; }
.hudm .xwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .ml-ic { width: 14px; height: 14px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .xwin .xmain { flex: 1; min-height: 0; display: flex; }
/* Folders */
.hudm .xwin .nav { width: 160px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .xwin .compose-btn { height: 32px; margin-bottom: 8px; justify-content: center; gap: 6px; color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb));
  box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); font-size: 12px; }
.hudm .xwin .nav .it { display: flex; align-items: center; gap: 9px; height: 30px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad);
  font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; }
.hudm .xwin .nav .it:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .xwin .nav .it.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .xwin .nav .it i { margin-left: auto; font-style: normal; font-size: 10px; padding: 0 6px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .xwin .nav .it i.hot { background: var(--ac); color: #fff; box-shadow: 0 0 6px var(--glow); }
.hudm .xwin .nav .sp { flex: 1; }
.hudm .xwin .nav .cap { font-size: 10px; color: var(--dim); padding: 0 6px; }
.hudm .xwin .nav .cap .bar { height: 4px; margin-top: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .xwin .nav .cap .bar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
/* List */
.hudm .xwin .lst { width: 330px; flex: none; display: flex; flex-direction: column; min-height: 0; padding: 10px 8px 8px 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .xwin input.in, .hudm .xwin textarea { width: 100%; box-sizing: border-box; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .xwin input.in { height: 26px; padding: 0 8px; }
.hudm .xwin textarea { height: 110px; } /* was inherited from lab.html's page-wide textarea rule */
.hudm .xwin textarea { padding: 8px; resize: none; line-height: 1.5; }
.hudm .xwin input.in:focus, .hudm .xwin textarea:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .xwin input.in::placeholder, .hudm .xwin textarea::placeholder { color: var(--dim); }
.hudm .xwin .bulk { display: flex; gap: 5px; margin: 8px 0 6px; }
.hudm .xwin .bulk .btn { height: 22px; padding: 0 8px; font-size: 10px; }
.hudm .xwin .bulk .btn.dis { opacity: .4; pointer-events: none; }
.hudm .xwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 3px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .xwin .ml { position: relative; display: flex; gap: 9px; padding: 8px 8px 8px 12px; margin-bottom: 3px; cursor: pointer; border-radius: var(--rad); border: 1px solid transparent;
  background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .xwin .ml:hover { border-color: color-mix(in srgb, var(--ac) 45%, transparent); background: color-mix(in srgb, var(--ac) 7%, transparent); }
.hudm .xwin .ml.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .xwin .ml.new::before { content: ""; position: absolute; left: 4px; top: 50%; width: 5px; height: 5px; margin-top: -2.5px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .xwin .av { width: 32px; height: 32px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 11px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .xwin .av.sys { color: var(--ac); border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .xwin .ml .mid { flex: 1; min-width: 0; }
.hudm .xwin .ml .top { display: flex; align-items: baseline; gap: 6px; }
.hudm .xwin .ml .top b { flex: 1; min-width: 0; font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .xwin .ml.new .top b, .hudm .xwin .ml.new .subj { color: #fff; font-weight: 700; }
.hudm .xwin .ml .top span { font-size: 10px; color: var(--dim); white-space: nowrap; }
.hudm .xwin .ml .subj { font-size: 12px; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
.hudm .xwin .ml .pv { font-size: 11px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
.hudm .xwin .tags { display: flex; gap: 5px; margin-top: 4px; }
.hudm .xwin .tag { display: inline-flex; align-items: center; gap: 3px; font-size: 10px; font-weight: 600; padding: 0 6px; height: 16px; border-radius: 8px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .xwin .tag .ml-ic { width: 10px; height: 10px; }
.hudm .xwin .tag.gold { color: #f2c94c; border-color: rgba(242, 201, 76, .45); }
.hudm .xwin .tag.done { color: var(--dim); border-color: color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .xwin .empty { padding: 30px 10px; text-align: center; color: var(--dim); font-size: 12px; }
/* Reading pane / composer */
.hudm .xwin .read { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .xwin .rscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 12px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .xwin .rh { display: flex; gap: 12px; align-items: center; }
.hudm .xwin .rh .av { width: 42px; height: 42px; font-size: 13px; }
.hudm .xwin .rh h3 { margin: 0; font-size: 16px; color: #fff; }
.hudm .xwin .rh .meta { font-size: 11px; color: var(--dim); margin-top: 2px; }
.hudm .xwin .rh .meta b { color: var(--tx); font-weight: 600; }
.hudm .xwin .body { white-space: pre-wrap; font-size: 13px; line-height: 1.6; color: color-mix(in srgb, var(--tx) 92%, var(--dim)); padding: 12px 14px; border-radius: var(--rad);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .xwin .lbl { display: flex; align-items: center; gap: 6px; margin-bottom: 6px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .xwin .lbl .ml-ic { color: var(--acd); }
.hudm .xwin .att { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.hudm .xwin .att.done .eq { filter: grayscale(1) brightness(.5); }
.hudm .xwin .goldchip { height: 44px; padding: 0 14px; display: inline-flex; align-items: center; gap: 8px; border-radius: var(--rad); font-size: 14px; font-weight: 700; color: #f2c94c;
  background: rgba(242, 201, 76, .08); border: 1px solid rgba(242, 201, 76, .4); font-variant-numeric: tabular-nums; }
.hudm .xwin .goldchip .ml-ic { width: 18px; height: 18px; }
.hudm .xwin .att.done .goldchip { color: var(--dim); border-color: color-mix(in srgb, var(--fr) 60%, transparent); background: none; }
.hudm .xwin .actions { display: flex; gap: 5px; align-items: center; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .xwin .actions .btn { height: 26px; padding: 0 10px; white-space: nowrap; gap: 5px; }
.hudm .xwin .actions .btn.on { color: var(--ac); border-color: var(--ac); }
.hudm .xwin .actions .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .xwin .actions .btn.dis { opacity: .4; pointer-events: none; }
.hudm .xwin .actions .btn.warn { border-color: #b33; color: #fbb; }
.hudm .xwin .actions .sp { flex: 1; }
.hudm .xwin .actions .note { font-size: 11px; color: var(--dim); }
.hudm .xwin .slots { display: flex; gap: 6px; }
.hudm .xwin .slot5 { position: relative; }
.hudm .xwin .slot5 .x { position: absolute; right: -5px; top: -5px; z-index: 2; width: 16px; height: 16px; border-radius: 50%; display: none; align-items: center; justify-content: center;
  font-size: 11px; color: #fff; background: #a33; cursor: pointer; }
.hudm .xwin .slot5:hover .x { display: flex; }
.hudm .xwin .slot5 .plus { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: 20px; color: var(--dim); cursor: pointer; z-index: 1; }
.hudm .xwin .picker { display: flex; flex-wrap: wrap; gap: 4px; padding: 8px; margin-top: 6px; border-radius: var(--rad); background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--ac); }
.hudm .xwin .picker .ttl { width: 100%; font-size: 10px; color: var(--dim); margin-bottom: 2px; }
.hudm .xwin .row2 { display: flex; gap: 10px; align-items: center; }
.hudm .xwin .row2 .in { width: 160px; }
.hudm .xwin .tog { width: 34px; height: 18px; border-radius: 9px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); flex: none; }
.hudm .xwin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 12px; height: 12px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .xwin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); }
.hudm .xwin .tog.on::after { left: 18px; background: #fff; }
.hudm .xwin .cost { display: grid; grid-template-columns: 1fr auto; gap: 3px 12px; font-size: 11px; padding: 8px 12px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .xwin .cost span { color: var(--dim); } .hudm .xwin .cost b { text-align: right; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .xwin .cost .tot { font-size: 13px; color: #f2c94c; } .hudm .xwin .cost .free { color: var(--ac); }
.hudm .xwin .count { font-size: 10px; color: var(--dim); text-align: right; margin-top: 2px; }
.hudm .xwin .err { font-size: 11px; color: #ff8a6a; }
.hudm .xwin .toast { font-size: 11px; color: var(--ac); }
`;

export class MailMock extends HudWindow {
  /** Unread count changed (the HUD's Mail button shows it). */
  onUnread: (n: number) => void = () => {};
  private inbox = MAILS.map((x) => ({ ...x, items: x.items.map((i) => [...i] as Item) }));
  private sent = SENT.map((x) => ({ ...x }));
  private folder: Folder = "Inbox";
  private picked = 1;
  private composing = false;
  private draft = { to: "", body: "", gold: 0, stamp: true, items: [] as Item[] };
  private picker = false;
  private confirmDelete = 0;
  private toast = "";
  private stamps = 3;
  private query = "";
  private nav!: HTMLElement; private list!: HTMLElement; private read!: HTMLElement; private bulk!: HTMLElement;

  constructor() {
    super("xwin", 52, 40);
    injectCss("hudm-mail-css", CSS);
    this.build();
    queueMicrotask(() => this.onUnread(this.unread()));
  }

  protected override shown(): void { this.render(); }
  unread(): number { return this.inbox.filter((x) => !x.opened).length; }

  private build(): void {
    this.nav = h("div", "nav");
    const search = h("input", "in");
    search.placeholder = "Search mail…";
    search.addEventListener("keydown", (e) => e.stopPropagation());
    search.addEventListener("input", () => { this.query = search.value.trim().toLowerCase(); this.renderList(); });
    this.bulk = h("div", "bulk");
    this.list = h("div", "list");
    this.read = h("div", "read");
    this.el.append(h("div", "panel", this.titleBar("MAIL", "Close (M)"), h("div", "xmain", this.nav, h("div", "lst", search, this.bulk, this.list), this.read)));
  }

  private render(): void {
    this.renderNav();
    this.renderList();
    this.renderRead();
    this.onUnread(this.unread());
  }

  /** Gold or items still attached (collecting empties them). */
  private pending(x: Mail): boolean { return !x.sent && (x.gold > 0 || x.items.length > 0); }
  /** Attachments you can take now (ClientMail.Collected: the parcel has reached you). */
  private ready(x: Mail): boolean { return this.pending(x) && x.collected; }
  /** CollectMail: the gold and items move to you; the mail keeps only its letter. */
  private take(x: Mail): [number, number] { const r: [number, number] = [x.gold, x.items.length]; x.gold = 0; x.items = []; x.opened = true; return r; }

  private renderNav(): void {
    const compose = h("div", "btn compose-btn", svgIcon(ICON.compose), "New mail");
    compose.addEventListener("click", () => { this.composing = true; this.draft = { to: "", body: "", gold: 0, stamp: this.stamps > 0, items: [] }; this.picker = false; this.toast = ""; this.renderRead(); });
    const counts: [Folder, string, number, boolean][] = [
      ["Inbox", ICON.inbox, this.inbox.length, false], ["Unread", ICON.unread, this.unread(), true], ["Attachments", ICON.clip, this.inbox.filter((x) => this.pending(x)).length, false],
      ["Locked", ICON.lock, this.inbox.filter((x) => x.locked).length, false], ["Sent", ICON.sent, this.sent.length, false]];
    this.nav.replaceChildren(compose);
    for (const [f, d, n, hot] of counts) {
      const it = h("div", "it" + (f === this.folder ? " on" : ""), svgIcon(d), f, h("i", hot && n ? "hot" : "", String(n)));
      it.addEventListener("click", () => { this.folder = f; this.composing = false; const first = this.items()[0]; if (first) this.picked = first.id; this.render(); });
      this.nav.append(it);
    }
    const bar = h("div", "bar", h("i"));
    (bar.firstChild as HTMLElement).style.width = (this.inbox.length / 50 * 100) + "%";
    this.nav.append(h("div", "sp"), h("div", "cap", `Mailbox ${this.inbox.length} / 50 · stamps ${this.stamps}`, bar));
  }

  private items(): Mail[] {
    const src = this.folder === "Sent" ? this.sent : this.inbox;
    return src.filter((x) => (this.folder === "Inbox" || this.folder === "Sent" || (this.folder === "Unread" ? !x.opened : this.folder === "Attachments" ? this.pending(x) : x.locked))
      && (!this.query || [x.from, x.to, x.subject, x.body, ...x.items.map((i) => i[0])].some((s) => s.toLowerCase().includes(this.query))))
      .sort((a, b) => a.minutes - b.minutes);
  }

  private renderList(): void {
    const items = this.items();
    // Bulk actions
    const collectable = this.inbox.filter((x) => this.ready(x));
    const all = h("div", "btn" + (collectable.length ? "" : " dis"), `Collect all${collectable.length ? ` (${collectable.length})` : ""}`);
    all.addEventListener("click", () => {
      let gold = 0, n = 0;
      for (const x of collectable) { const [g, c] = this.take(x); gold += g; n += c; }
      this.toast = `Collected ${fmt(gold)} gold and ${n} item${n === 1 ? "" : "s"}.`;
      this.render();
    });
    const markRead = h("div", "btn" + (this.unread() ? "" : " dis"), "Mark all read");
    markRead.addEventListener("click", () => { for (const x of this.inbox) x.opened = true; this.render(); });
    this.bulk.replaceChildren(all, markRead);
    this.list.replaceChildren();
    if (!items.length) this.list.append(h("div", "empty", this.query ? "No mail matches." : this.folder === "Unread" ? "You're all caught up." : "Nothing here."));
    for (const x of items) {
      const who = this.folder === "Sent" ? x.to : x.from;
      const tags = h("div", "tags");
      if (x.gold) tags.append(h("span", "tag gold" + (x.sent ? " done" : ""), svgIcon(ICON.coin), fmt(x.gold)));
      if (x.items.length) tags.append(h("span", "tag" + (x.sent ? " done" : ""), svgIcon(ICON.clip), `${x.items.length} item${x.items.length === 1 ? "" : "s"}`));
      if (this.pending(x) && !x.collected) tags.append(h("span", "tag", "At the post office"));
      if (x.locked) tags.append(h("span", "tag", svgIcon(ICON.lock), "Locked"));
      if (x.system) tags.append(h("span", "tag done", "No reply"));
      const row = h("div", "ml" + (x.id === this.picked && !this.composing ? " sel" : "") + (!x.opened && !x.sent ? " new" : ""),
        h("div", "av" + (x.system ? " sys" : ""), initials(who)),
        h("div", "mid", h("div", "top", h("b", "", (this.folder === "Sent" ? "To " : "") + who), h("span", "", x.ago)), h("div", "subj", x.subject), h("div", "pv", x.body.replace(/\n+/g, " ")), tags.childElementCount ? tags : ""));
      row.addEventListener("click", () => { this.picked = x.id; this.composing = false; this.confirmDelete = 0; this.toast = ""; x.opened = true; this.render(); });
      this.list.append(row);
    }
  }

  private renderRead(): void {
    this.read.replaceChildren();
    if (this.composing) { this.renderCompose(); return; }
    const src = this.folder === "Sent" ? this.sent : this.inbox;
    const x = src.find((p) => p.id === this.picked) ?? this.items()[0];
    if (!x) { this.read.append(h("div", "empty", "Pick a mail.")); return; }
    this.picked = x.id;
    const who = x.sent ? x.to : x.from;
    const parts: HTMLElement[] = [
      h("div", "rh", h("div", "av" + (x.system ? " sys" : ""), initials(who)), h("div", "", h("h3", "", x.subject),
        h("div", "meta", x.sent ? "To " : "From ", h("b", "", who), ` · ${x.ago} ago${x.system ? " · system mail" : ""}`))),
      h("div", "body", x.body),
    ];
    if (x.gold || x.items.length) {
      const att = h("div", "att" + (x.sent ? " done" : ""));
      if (x.gold) att.append(h("div", "goldchip", svgIcon(ICON.coin), `${fmt(x.gold)} gold`));
      for (const [n, img, c] of x.items) att.append(itemCell(this.el, c > 1 ? `${n} (${c})` : n, img, c));
      parts.push(h("div", "", h("div", "lbl", svgIcon(ICON.clip), x.sent ? "Sent with this mail" : x.collected ? "Attachments" : "Attachments · waiting at the post office"), att));
    }
    if (this.toast) parts.push(h("div", "toast", this.toast));
    this.read.append(h("div", "rscroll", ...parts), this.readActions(x));
  }

  private readActions(x: Mail): HTMLElement {
    const bar = h("div", "actions");
    const btn = (label: string, icon: string | null, cls: string, fn: () => void, title = "") => {
      const b = h("div", "btn " + cls);
      if (icon) b.append(svgIcon(icon));
      b.append(label);
      if (title) b.title = title;
      b.addEventListener("click", fn);
      bar.append(b);
      return b;
    };
    if (x.sent) { bar.append(h("span", "note", `Delivered to ${x.to}`)); return bar; }
    btn("Reply", ICON.reply, x.system ? "dis" : "", () => {
      this.composing = true;
      this.draft = { to: x.from, body: `\n\n— ${x.from} wrote —\n${x.body}`, gold: 0, stamp: this.stamps > 0, items: [] };
      this.picker = false; this.toast = ""; this.renderRead();
    }, x.system ? "System mail cannot be replied to" : "");
    btn(x.locked ? "Unlock" : "Lock", ICON.lock, x.locked ? "on" : "", () => { x.locked = !x.locked; this.render(); }, "Locked mail cannot be deleted");
    // Only locked mail is protected; deleting a parcel asks first and its attachments are lost
    // (the desktop's ParcelContainsItemsOrGoldDeleteConfirm).
    const asking = this.confirmDelete === x.id;
    btn(asking ? (this.pending(x) ? "Click again: the gold and items are lost" : "Click again to delete") : "Delete", ICON.trash, (x.locked ? "dis " : "") + (asking ? "warn" : ""), () => {
      if (x.locked) return;
      if (!asking) { this.confirmDelete = x.id; this.renderRead(); return; }
      this.inbox = this.inbox.filter((p) => p !== x); this.confirmDelete = 0; this.picked = this.items()[0]?.id ?? 0; this.render();
    }, x.locked ? "Unlock it first" : this.pending(x) ? "The attachments are deleted with the mail" : "");
    bar.append(h("div", "sp"));
    if (this.ready(x)) btn(`Collect${x.gold ? ` ${fmt(x.gold)} gold` : ""}${x.items.length ? `${x.gold ? " +" : ""} ${x.items.length} item${x.items.length === 1 ? "" : "s"}` : ""}`, ICON.coin, "main", () => {
      this.take(x); this.toast = "Collected to your bag."; this.render();
    });
    else if (this.pending(x)) btn("Visit a post office (mock)", null, "", () => {
      // NPCScript CollectParcelKey: every waiting parcel becomes collectable.
      for (const p of this.inbox) if (this.pending(p)) p.collected = true;
      this.toast = "The post office handed over your parcels."; this.render();
    }, "Parcels are collected from a post office NPC first");
    return bar;
  }

  // — Composer —
  private postage(): { gold: number; ins: number; total: number; worth: number } {
    const worth = this.draft.items.reduce((s, i) => s + i[3], 0);
    // GetMailCost: whole thousands of gold; insurance per item, rounded down.
    const gold = Math.floor(this.draft.gold / 1000) * COST_PER_1K;
    const ins = this.draft.items.reduce((s, i) => s + Math.floor(i[3] / 100 * INSURANCE), 0);
    return { gold, ins, worth, total: this.draft.stamp ? 0 : gold + ins };
  }

  private renderCompose(): void {
    const d = this.draft;
    const to = h("input", "in");
    to.placeholder = "Recipient";
    to.value = d.to;
    to.setAttribute("list", "hudm-mail-to");
    const dl = h("datalist"); dl.id = "hudm-mail-to";
    for (const c of CONTACTS) dl.append(Object.assign(h("option"), { value: c }));
    to.addEventListener("keydown", (e) => e.stopPropagation());
    to.addEventListener("input", () => { d.to = to.value; });
    const body = h("textarea");
    body.rows = 7; body.maxLength = 500; body.placeholder = "Write your letter…"; body.value = d.body;
    const count = h("div", "count", `${d.body.length} / 500`);
    body.addEventListener("keydown", (e) => e.stopPropagation());
    body.addEventListener("input", () => { d.body = body.value; count.textContent = `${d.body.length} / 500`; });
    // Five attachment slots; without a stamp only the first is open (SendMail sends items[0] only and the desktop
    // disables the other cells). An empty open slot opens the bag picker.
    const open = d.stamp ? MAX_ITEMS : 1;
    const slots = h("div", "slots");
    for (let i = 0; i < MAX_ITEMS; i++) {
      const it = d.items[i];
      const wrap = h("div", "slot5", it ? itemCell(this.el, `${it[0]}${it[2] > 1 ? ` (${it[2]})` : ""}`, it[1], it[2]) : itemCell(this.el, "", -1));
      if (it) { const x = h("div", "x", "×"); x.addEventListener("click", () => { d.items.splice(i, 1); this.renderRead(); }); wrap.append(x); }
      else if (i < open) { const p = h("div", "plus", "+"); p.title = "Attach an item from your bag"; p.addEventListener("click", () => { this.picker = !this.picker; this.renderRead(); }); wrap.append(p); }
      else { wrap.style.opacity = ".35"; wrap.title = "Use a mail stamp to send up to 5 items"; }
      slots.append(wrap);
    }
    const attach: HTMLElement[] = [h("div", "lbl", svgIcon(ICON.clip), `Items · ${d.items.length} / ${open}${d.stamp ? "" : " (5 with a stamp)"}`), slots];
    if (this.picker) {
      const pk = h("div", "picker", h("div", "ttl", "Your bag · click an item to attach it"));
      for (const b of BAG) {
        if (d.items.includes(b)) continue;
        const c = itemCell(this.el, `${b[0]}${b[2] > 1 ? ` (${b[2]})` : ""}`, b[1], b[2]);
        c.addEventListener("click", () => { if (d.items.length < open) d.items.push(b); this.picker = d.items.length < open && this.picker; this.renderRead(); });
        pk.append(c);
      }
      attach.push(pk);
    }
    const gold = h("input", "in");
    gold.placeholder = "0";
    gold.value = d.gold ? String(d.gold) : "";
    gold.addEventListener("keydown", (e) => e.stopPropagation());
    const stamp = h("div", "tog" + (d.stamp ? " on" : ""));
    stamp.addEventListener("click", () => {
      if (this.stamps <= 0) return;
      d.stamp = !d.stamp;
      if (!d.stamp && d.items.length > 1) d.items.length = 1; // without a stamp only the first item goes
      this.renderRead();
    });
    const cost = h("div", "cost");
    const fillCost = () => {
      const p = this.postage();
      cost.replaceChildren(h("span", "", `Gold postage (${COST_PER_1K} per whole 1,000)`), h("b", "", fmt(p.gold)), h("span", "", `Item insurance (${INSURANCE} % of ${fmt(p.worth)})`), h("b", "", fmt(p.ins)),
        h("span", "", "Total"), d.stamp ? h("b", "tot free", "Free with stamp") : h("b", "tot", `${fmt(p.total + d.gold)} gold`));
    };
    gold.addEventListener("input", () => { d.gold = Math.min(50_000_000, Math.floor(Number(gold.value.replace(/[^0-9]/g, "")) || 0)); fillCost(); });
    fillCost();
    const err = h("div", "err");
    const parts = [
      h("div", "rh", h("div", "av", "✎"), h("div", "", h("h3", "", "New mail"), h("div", "meta", "Letters are free; gold and items cost postage unless you use a stamp."))),
      h("div", "", h("div", "lbl", "To"), to, dl),
      h("div", "", h("div", "lbl", "Message"), body, count),
      h("div", "", ...attach),
      h("div", "row2", h("div", "", h("div", "lbl", svgIcon(ICON.coin), "Gold"), gold), h("div", "", h("div", "lbl", svgIcon(ICON.stamp), `Mail stamp · ${this.stamps} left`), stamp)),
      cost, err,
    ];
    const bar = h("div", "actions");
    const cancel = h("div", "btn", "Discard");
    cancel.addEventListener("click", () => { this.composing = false; this.render(); });
    const send = h("div", "btn main", svgIcon(ICON.sent), "Send");
    send.addEventListener("click", () => {
      const target = d.to.trim();
      if (!target) { err.textContent = "Who is it for?"; return; }
      if (!d.body.trim() && !d.items.length && !d.gold) { err.textContent = "Write something, or attach gold or items."; return; }
      if (d.stamp) this.stamps--;
      this.sent.unshift({ id: id++, from: "GMRyu", to: target, system: false, subject: d.body.trim().split("\n")[0]?.slice(0, 40) || (d.items.length ? "Parcel" : "Gold"), body: d.body.trim() || "(parcel)",
        ago: "just now", minutes: 0, opened: true, locked: false, collected: true, gold: d.gold, items: [...d.items], sent: true });
      this.composing = false; this.folder = "Sent"; this.picked = this.sent[0]!.id;
      this.toast = `Sent to ${target}.`;
      this.render();
    });
    bar.append(cancel, h("div", "sp"), send);
    this.read.append(h("div", "rscroll", ...parts), bar);
  }
}
