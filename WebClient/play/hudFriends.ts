// Friends window mock for the scene lab's UI tester (window style from hudWindow.ts), a richer take on the desktop's
// FriendDialog. Crystal's ClientFriend is Name / Memo / Blocked / Online; around that: a "bonds" strip (marriage and
// mentor, systems Crystal has), Friends / Requests / Blocked / Recent tabs, search, favourites / online / offline groups,
// an add-friend box, and a profile pane (class badge, status, location or last seen, status message, the editable memo,
// recent activity, a whisper thread with a composer, and Whisper / Group / Inspect / Block / Remove). Mock data only.

import { HudWindow, h, injectCss } from "./hudWindow.ts";

type Tab = "Friends" | "Requests" | "Blocked" | "Recent";
interface Friend {
  name: string; cls: string; level: number; online: boolean; where: string; seen: string; status: string; memo: string;
  fav: boolean; blocked: boolean; since: string; guild: string; activity: string[]; chat: [boolean, string][];
}
const CLS_SHORT: Record<string, string> = { Warrior: "WAR", Wizard: "WIZ", Taoist: "TAO", Assassin: "ASN", Archer: "ARC" };
const f = (name: string, cls: string, level: number, online: boolean, where: string, seen: string, status: string, memo: string, fav = false, guild = "GameMaster",
  activity: string[] = [], chat: [boolean, string][] = []): Friend => ({ name, cls, level, online, where, seen, status, memo, fav, blocked: false, since: "", guild, activity, chat });
const FRIENDS: Friend[] = [
  f("Seren", "Archer", 31, true, "Stone Temple", "", "Farming stones, whisper if you need any", "Wife ♥ · always up for Bone Cave", true, "GameMaster",
    ["Reached level 31", "Obtained WhiteGoldRing", "Completed The Bone Elite"], [[false, "omw to the temple"], [true, "need a hand?"], [false, "yes please, bring pots"]]),
  f("Kaelith", "Assassin", 42, true, "Gi-Ryoong", "", "LF group Gi-Ryoong 3F", "Officer, runs Sunday raids", true, "GameMaster", ["Defeated a Zuma Taurus", "Reached level 42"]),
  f("Brannoc", "Wizard", 45, false, "", "2 h ago", "", "Sells ice storm books cheap", false, "GameMaster", ["Reached level 45"]),
  f("Wren", "Taoist", 29, true, "Bichon Province", "", "Mentee! learning the ropes", "My mentee", false, "GameMaster", ["Reached level 29", "Learned Summon Skeleton"]),
  f("Thorne", "Warrior", 38, true, "Serpent Valley", "", "", "Met at the Bichon wall event", false, "Lionheart", ["Joined Lionheart"]),
  f("Isolde", "Wizard", 36, false, "", "yesterday", "brb holiday", "", false, "Silver Dawn"),
  f("Mordrake", "Taoist", 31, false, "", "3 days ago", "", "Owes me 200k", false, "GameMaster"),
  f("Pip", "Archer", 24, true, "Woomyon Woods", "", "first archer, be nice", "", false, "—"),
  f("Grimwald", "Warrior", 47, false, "", "1 week ago", "", "Tank for boss runs", false, "GameMaster"),
  f("Nyra", "Taoist", 24, true, "Mongchon", "", "healing for food", "", false, "GameMaster"),
].map((x, i) => ({ ...x, since: `${1 + ((i * 7) % 27)}/${1 + ((i * 3) % 9)}/2026` }));
const BLOCKED: Friend[] = [f("GoldSeller99", "Warrior", 7, true, "Bichon Province", "", "CHEAP GOLD www…", "Spam", false, "—"), f("Kroll", "Assassin", 33, false, "", "4 days ago", "", "PK'd me twice at the wall", false, "Nightfall")]
  .map((x) => ({ ...x, blocked: true, since: "—" }));
const REQUESTS: [string, string, number, string][] = [["Halvard", "Warrior", 35, "We grouped at Bone Cave"], ["Luma", "Wizard", 29, ""], ["Corvin", "Archer", 41, "Guildie of Thorne"]];
const RECENT: [string, string, number, string][] = [["Ashka", "Assassin", 39, "Grouped · 20 min ago"], ["Dorrin", "Taoist", 33, "Traded · 1 h ago"], ["Vexa", "Wizard", 22, "Grouped · yesterday"], ["Varek", "Assassin", 35, "Guild war · 2 days ago"]];
const BONDS = { lover: { name: "Seren", days: 41 }, mentor: { name: "Wren", role: "Mentee", progress: 0.62 } };

const ICON = {
  star: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z",
  heart: "M12 21s-7.5-4.6-9.5-9.3C1 8 3.4 4.5 7 4.5c2 0 3.6 1.1 5 3 1.4-1.9 3-3 5-3 3.6 0 6 3.5 4.5 7.2C19.5 16.4 12 21 12 21z",
  book: "M4 4h6a2 2 0 012 2v14a2 2 0 00-2-2H4zM20 4h-6a2 2 0 00-2 2v14a2 2 0 012-2h6z",
  memo: "M5 3h10l4 4v14H5zM15 3v4h4M8 11h8M8 15h6",
  add: "M12 5v14M5 12h14",
  chat: "M4 5h16v10H9l-5 4z",
  send: "M3 11l18-8-8 18-2-8z",
};
const svgIcon = (d: string, cls = "fr-ic") => {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", cls);
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
};

const CSS = `
.hudm .fwin { --winh: 520px; width: 700px; }
.hudm .fwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .fr-ic { width: 13px; height: 13px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
/* Bonds strip */
.hudm .fwin .bonds { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; padding: 10px 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .fwin .bond { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .fwin .bond .fr-ic { width: 22px; height: 22px; color: var(--ac); filter: drop-shadow(0 0 5px var(--glow)); }
.hudm .fwin .bond .t { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); }
.hudm .fwin .bond .t b { display: block; font-size: 13px; color: #fff; }
.hudm .fwin .bond .bar { height: 4px; margin-top: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .fwin .bond .bar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .fwin .bond .btn { height: 22px; padding: 0 9px; font-size: 10px; }
/* Body */
.hudm .fwin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .fwin .left { width: 300px; display: flex; flex-direction: column; padding: 10px 8px 10px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .fwin .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .fwin .seg div { flex: 1; height: 26px; display: flex; align-items: center; justify-content: center; gap: 4px; cursor: pointer; user-select: none;
  font-size: 11px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .fwin .seg div + div { border-left: 1px solid var(--fr); }
.hudm .fwin .seg div i { font-style: normal; font-size: 10px; padding: 0 5px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .fwin .seg div i.hot { background: var(--ac); color: #fff; box-shadow: 0 0 6px var(--glow); }
.hudm .fwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 24%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .fwin input.in { width: 100%; box-sizing: border-box; height: 26px; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .fwin input.in:focus, .hudm .fwin textarea:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .fwin input.in::placeholder, .hudm .fwin textarea::placeholder { color: var(--dim); }
.hudm .fwin .search { margin: 8px 0 4px; }
.hudm .fwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .fwin .grp { display: flex; align-items: center; gap: 6px; height: 24px; margin-top: 4px; padding: 0 4px; cursor: pointer; user-select: none;
  font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .fwin .grp .chev { transition: transform .15s; font-size: 10px; width: 10px; }
.hudm .fwin .grp.shut .chev { transform: rotate(-90deg); }
.hudm .fwin .grp span:last-child { margin-left: auto; letter-spacing: 0; color: var(--dim); }
.hudm .fwin .row { display: flex; align-items: center; gap: 9px; padding: 6px 6px 6px 8px; margin-bottom: 3px; cursor: pointer; border: 1px solid transparent; border-radius: var(--rad);
  background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .fwin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .fwin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .fwin .row.off { opacity: .62; }
.hudm .fwin .av { position: relative; width: 30px; height: 30px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center;
  font-size: 9px; font-weight: 800; letter-spacing: .04em; color: var(--tx); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .fwin .av::after { content: ""; position: absolute; right: -1px; bottom: -1px; width: 9px; height: 9px; border-radius: 50%; background: #59606e; border: 2px solid color-mix(in srgb, var(--pb) 90%, #000); }
.hudm .fwin .av.on { border-color: var(--ac); }
.hudm .fwin .av.on::after { background: #4fe07a; box-shadow: 0 0 6px rgba(80, 230, 120, .7); }
.hudm .fwin .row .mid { flex: 1; min-width: 0; }
.hudm .fwin .row .nm { display: flex; align-items: center; gap: 6px; font-size: 12px; font-weight: 600; color: var(--tx); }
.hudm .fwin .row .nm small { font-size: 10px; font-weight: 500; color: var(--dim); }
.hudm .fwin .row .sub { margin-top: 2px; font-size: 10px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .fwin .row .sub em { color: color-mix(in srgb, var(--tx) 70%, var(--dim)); }
.hudm .fwin .star { color: color-mix(in srgb, var(--dim) 60%, transparent); padding: 3px; border-radius: 4px; }
.hudm .fwin .star:hover { color: var(--tx); }
.hudm .fwin .star.on { color: var(--ac); filter: drop-shadow(0 0 4px var(--ac)); }
.hudm .fwin .star.on svg { fill: currentColor; }
.hudm .fwin .row .btn { height: 22px; padding: 0 9px; font-size: 10px; }
.hudm .fwin .add { display: flex; gap: 6px; margin-top: 8px; }
.hudm .fwin .add .btn { height: 26px; flex: none; }
.hudm .fwin .add .btn .fr-ic { margin-right: 4px; }
.hudm .fwin .note { font-size: 10px; color: var(--dim); margin-top: 4px; min-height: 13px; }
.hudm .fwin .note.ok { color: var(--ac); }
.hudm .fwin .empty { padding: 26px 8px; text-align: center; color: var(--dim); font-size: 12px; }
/* Profile */
.hudm .fwin .detail { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .fwin .dscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 11px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .fwin .prof { display: flex; gap: 14px; align-items: center; }
.hudm .fwin .prof .av { width: 58px; height: 58px; font-size: 14px; border-width: 2px; }
.hudm .fwin .prof .av.on { box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .fwin .prof .av::after { width: 13px; height: 13px; right: 1px; bottom: 1px; }
.hudm .fwin .prof h3 { margin: 0; font-size: 18px; color: #fff; display: flex; align-items: center; gap: 8px; }
.hudm .fwin .prof .pill { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .fwin .prof .pill.on { color: #c8ffd6; border-color: #3fbf66; background: rgba(60, 190, 100, .18); }
.hudm .fwin .prof .meta { margin-top: 4px; font-size: 11px; color: var(--dim); }
.hudm .fwin .prof .meta b { color: var(--tx); font-weight: 600; }
.hudm .fwin .quote { font-size: 12px; font-style: italic; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); padding-left: 10px; border-left: 2px solid var(--ac); }
.hudm .fwin .lbl { display: flex; align-items: center; gap: 6px; margin-bottom: 5px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .fwin .lbl .fr-ic { color: var(--acd); }
.hudm .fwin textarea { width: 100%; box-sizing: border-box; height: 46px; padding: 6px 8px; resize: none; font: 12px/1.45 "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .fwin .facts { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 10px; }
.hudm .fwin .facts div { display: flex; justify-content: space-between; font-size: 11px; padding: 3px 7px; border-radius: calc(var(--rad) * .5); background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .fwin .facts span { color: var(--dim); } .hudm .fwin .facts b { color: var(--tx); font-weight: 600; }
.hudm .fwin .acts { display: flex; flex-wrap: wrap; gap: 3px 10px; font-size: 11px; color: color-mix(in srgb, var(--tx) 80%, var(--dim)); }
.hudm .fwin .acts div::before { content: "◆"; margin-right: 5px; font-size: 7px; color: var(--ac); vertical-align: 2px; }
.hudm .fwin .thread { display: flex; flex-direction: column; gap: 4px; max-height: 110px; overflow-y: auto; padding: 6px; border-radius: var(--rad); background: color-mix(in srgb, var(--pb) 55%, #000);
  border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .fwin .msg { max-width: 80%; padding: 4px 9px; border-radius: 10px; font-size: 12px; align-self: flex-start; background: color-mix(in srgb, var(--frl) 12%, transparent); }
.hudm .fwin .msg.me { align-self: flex-end; color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .fwin .thread .none { color: var(--dim); font-size: 11px; text-align: center; padding: 6px; }
.hudm .fwin .compose { display: flex; gap: 6px; margin-top: 6px; }
.hudm .fwin .compose .btn { height: 26px; padding: 0 10px; }
.hudm .fwin .actions { display: flex; gap: 5px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .fwin .actions .btn { height: 26px; padding: 0 10px; white-space: nowrap; }
.hudm .fwin .actions .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .fwin .actions .btn.warn { border-color: #b33; color: #fbb; }
.hudm .fwin .actions .sp { flex: 1; }
`;

export class FriendsMock extends HudWindow {
  private tab: Tab = "Friends";
  private friends = FRIENDS.map((x) => ({ ...x, activity: [...x.activity], chat: x.chat.map((c) => [...c] as [boolean, string]) }));
  private blocked = BLOCKED.map((x) => ({ ...x }));
  private requests = REQUESTS.map((r) => [...r] as [string, string, number, string]);
  private recent = RECENT.map((r) => [...r] as [string, string, number, string]);
  private picked = "Seren";
  private shut = new Set<string>();
  private confirmRemove = "";
  private note = "";
  private search!: HTMLInputElement;
  private seg!: HTMLElement;
  private list!: HTMLElement;
  private detail!: HTMLElement;
  private noteEl!: HTMLElement;

  constructor() {
    super("fwin", 160, 50);
    injectCss("hudm-friends-css", CSS);
    this.build();
  }

  protected override shown(): void { this.render(); }

  private build(): void {
    const bond = (icon: string, title: string, line: HTMLElement | string, btn: string) => h("div", "bond", svgIcon(icon), h("div", "t", h("b", "", title), line), h("div", "btn", btn));
    const mentorBar = h("div", "bar", h("i"));
    (mentorBar.firstChild as HTMLElement).style.width = BONDS.mentor.progress * 100 + "%";
    const bonds = h("div", "bonds",
      bond(ICON.heart, `Married to ${BONDS.lover.name}`, `Together ${BONDS.lover.days} days · online now`, "Recall"),
      bond(ICON.book, `Mentoring ${BONDS.mentor.name}`, h("div", "", `${BONDS.mentor.role} progress ${Math.round(BONDS.mentor.progress * 100)}% to graduation`, mentorBar), "Whisper"));
    this.seg = h("div", "seg");
    this.search = h("input", "in search");
    this.search.placeholder = "Search friends, places, notes…";
    this.search.addEventListener("input", () => this.renderList());
    this.search.addEventListener("keydown", (e) => e.stopPropagation());
    this.list = h("div", "list");
    const addIn = h("input", "in");
    addIn.placeholder = "Add a friend by name";
    addIn.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") addBtn.click(); });
    const addBtn = h("div", "btn", svgIcon(ICON.add), "Add");
    addBtn.addEventListener("click", () => {
      const n = addIn.value.trim();
      if (!n) return;
      this.note = this.friends.some((x) => x.name.toLowerCase() === n.toLowerCase()) ? `${n} is already your friend.` : `Friend request sent to ${n}.`;
      addIn.value = "";
      this.renderNote();
    });
    this.noteEl = h("div", "note");
    this.detail = h("div", "detail");
    this.el.append(h("div", "panel", this.titleBar("FRIENDS", "Close (F)"), bonds,
      h("div", "cols", h("div", "left", this.seg, this.search, this.list, h("div", "add", addIn, addBtn), this.noteEl), this.detail)));
  }

  private renderNote(): void {
    this.noteEl.textContent = this.note;
    this.noteEl.classList.toggle("ok", this.note.startsWith("Friend request sent"));
  }

  private render(): void {
    this.renderSeg();
    this.renderList();
    this.renderDetail();
    this.renderNote();
  }

  private renderSeg(): void {
    this.seg.replaceChildren();
    const counts: Record<Tab, number> = { Friends: this.friends.length, Requests: this.requests.length, Blocked: this.blocked.length, Recent: this.recent.length };
    for (const t of ["Friends", "Requests", "Blocked", "Recent"] as Tab[]) {
      const b = h("div", t === this.tab ? "on" : "", t, h("i", t === "Requests" && counts[t] ? "hot" : "", String(counts[t])));
      b.addEventListener("click", () => { this.tab = t; this.confirmRemove = ""; this.render(); });
      this.seg.append(b);
    }
  }

  private matches(...fields: string[]): boolean {
    const q = this.search.value.trim().toLowerCase();
    return !q || fields.some((s) => s.toLowerCase().includes(q));
  }

  private renderList(): void {
    this.list.replaceChildren();
    if (this.tab === "Friends" || this.tab === "Blocked") {
      const pool = (this.tab === "Friends" ? this.friends : this.blocked).filter((x) => this.matches(x.name, x.cls, x.where, x.memo, x.status, x.guild));
      const groups: [string, Friend[]][] = this.tab === "Friends"
        ? [["Favourites", pool.filter((x) => x.fav)], ["Online", pool.filter((x) => !x.fav && x.online)], ["Offline", pool.filter((x) => !x.fav && !x.online)]]
        : [["Blocked", pool]];
      for (const [g, items] of groups) {
        if (!items.length) continue;
        const head = h("div", "grp" + (this.shut.has(g) ? " shut" : ""), h("span", "chev", "▾"), g, h("span", "", String(items.length)));
        head.addEventListener("click", () => { if (this.shut.has(g)) this.shut.delete(g); else this.shut.add(g); this.renderList(); });
        this.list.append(head);
        if (this.shut.has(g)) continue;
        for (const x of items) this.list.append(this.friendRow(x));
      }
      if (!pool.length) this.list.append(h("div", "empty", "No one matches."));
      return;
    }
    const rows = (this.tab === "Requests" ? this.requests : this.recent).filter((r) => this.matches(r[0], r[1], r[3]));
    if (!rows.length) this.list.append(h("div", "empty", this.tab === "Requests" ? "No pending requests." : "Nobody recently."));
    for (const r of rows) {
      const [name, cls, level, note] = r;
      const row = h("div", "row", h("div", "av", CLS_SHORT[cls] ?? ""), h("div", "mid", h("div", "nm", name, h("small", "", `${cls} ${level}`)), h("div", "sub", note || "wants to be friends")));
      if (this.tab === "Requests") {
        const ok = h("div", "btn", "Accept"), no = h("div", "btn", "Decline");
        ok.addEventListener("click", () => {
          this.requests = this.requests.filter((x) => x !== r);
          this.friends.push({ ...f(name, cls, level, true, "Bichon Province", "", "", "", false, "—", ["Became your friend"]), since: "today" });
          this.note = `${name} is now your friend.`; this.render();
        });
        no.addEventListener("click", () => { this.requests = this.requests.filter((x) => x !== r); this.render(); });
        row.append(ok, no);
      } else {
        const add = h("div", "btn", "Add");
        add.addEventListener("click", () => { this.note = `Friend request sent to ${name}.`; this.recent = this.recent.filter((x) => x !== r); this.render(); });
        row.append(add);
      }
      this.list.append(row);
    }
  }

  private friendRow(x: Friend): HTMLElement {
    const sub = x.online ? h("div", "sub", x.where, x.status ? " · " : "", x.status ? h("em", "", x.status) : "") : h("div", "sub", `Last seen ${x.seen}`);
    const row = h("div", "row" + (x.name === this.picked ? " sel" : "") + (x.online ? "" : " off"),
      h("div", "av" + (x.online ? " on" : ""), CLS_SHORT[x.cls] ?? ""), h("div", "mid", h("div", "nm", x.name, h("small", "", `${x.cls} ${x.level}`)), sub));
    if (!x.blocked) {
      const star = h("div", "star" + (x.fav ? " on" : ""), svgIcon(ICON.star));
      star.title = x.fav ? "Remove from favourites" : "Add to favourites";
      star.addEventListener("click", (e) => { e.stopPropagation(); x.fav = !x.fav; this.renderList(); });
      row.append(star);
    }
    row.addEventListener("click", () => { this.picked = x.name; this.confirmRemove = ""; this.renderList(); this.renderDetail(); });
    return row;
  }

  private renderDetail(): void {
    this.detail.replaceChildren();
    const pool = this.tab === "Blocked" ? this.blocked : this.friends;
    const x = pool.find((p) => p.name === this.picked) ?? (this.tab === "Friends" || this.tab === "Blocked" ? pool[0] : undefined);
    if (!x) {
      this.detail.append(h("div", "empty", this.tab === "Requests" ? "Accept a request to add them to your friends." : "Players you grouped, traded or fought alongside recently."));
      return;
    }
    this.picked = x.name;
    const status = h("span", "pill" + (x.online ? " on" : ""), x.blocked ? "Blocked" : x.online ? "Online" : "Offline");
    const prof = h("div", "prof", h("div", "av" + (x.online ? " on" : ""), CLS_SHORT[x.cls] ?? ""),
      h("div", "", h("h3", "", x.name, status), h("div", "meta", h("b", "", `${x.cls} ${x.level}`), " · ", x.online ? h("span", "", "In ", h("b", "", x.where)) : `Last seen ${x.seen}`),
        h("div", "meta", "Guild ", h("b", "", x.guild))));
    const parts: HTMLElement[] = [prof];
    if (x.status) parts.push(h("div", "quote", `“${x.status}”`));
    // The memo (ClientFriend.Memo): saved when the box loses focus.
    const memo = h("textarea");
    memo.placeholder = "Add a private note…";
    memo.value = x.memo;
    memo.addEventListener("keydown", (e) => e.stopPropagation());
    memo.addEventListener("change", () => { x.memo = memo.value; this.renderList(); });
    parts.push(h("div", "", h("div", "lbl", svgIcon(ICON.memo), "Memo · only you see this"), memo));
    const fact = (k: string, v: string) => h("div", "", h("span", "", k), h("b", "", v));
    parts.push(h("div", "facts", fact("Friends since", x.since), fact("Favourite", x.fav ? "Yes" : "No")));
    if (x.activity.length) {
      const acts = h("div", "acts");
      for (const a of x.activity) acts.append(h("div", "", a));
      parts.push(h("div", "", h("div", "lbl", "Recent activity"), acts));
    }
    if (!x.blocked) {
      const thread = h("div", "thread");
      const fill = () => {
        thread.replaceChildren();
        if (!x.chat.length) thread.append(h("div", "none", "No whispers yet."));
        for (const [me, text] of x.chat) thread.append(h("div", "msg" + (me ? " me" : ""), text));
        thread.scrollTop = thread.scrollHeight;
      };
      fill();
      const input = h("input", "in");
      input.placeholder = x.online ? `Whisper ${x.name}…` : `${x.name} is offline: they will see it later`;
      const send = h("div", "btn", svgIcon(ICON.send));
      const go = () => { const t = input.value.trim(); if (!t) return; x.chat.push([true, t]); input.value = ""; fill(); };
      input.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") go(); });
      send.addEventListener("click", go);
      parts.push(h("div", "", h("div", "lbl", svgIcon(ICON.chat), "Whispers"), thread, h("div", "compose", input, send)));
      this.whisperInput = input;
    }
    this.detail.append(h("div", "dscroll", ...parts), this.actions(x));
  }
  private whisperInput: HTMLInputElement | null = null;

  private actions(x: Friend): HTMLElement {
    const bar = h("div", "actions");
    const btn = (label: string, cls: string, fn: () => void) => { const b = h("div", "btn " + cls, label); b.addEventListener("click", fn); bar.append(b); return b; };
    if (x.blocked) {
      bar.append(h("div", "sp"));
      btn("Unblock", "main", () => { this.blocked = this.blocked.filter((p) => p !== x); this.note = `${x.name} was unblocked.`; this.render(); });
      return bar;
    }
    btn("Whisper", "main", () => this.whisperInput?.focus());
    btn("Group invite", "", () => { this.note = `Group invite sent to ${x.name}.`; this.renderNote(); });
    btn("Inspect", "", () => { this.note = `Inspecting ${x.name}… (mock)`; this.renderNote(); });
    bar.append(h("div", "sp"));
    btn("Block", "", () => {
      this.friends = this.friends.filter((p) => p !== x);
      this.blocked.push({ ...x, blocked: true, fav: false });
      this.note = `${x.name} was blocked.`; this.picked = this.friends[0]?.name ?? ""; this.render();
    });
    btn(this.confirmRemove === x.name ? "Click again to remove" : "Remove", this.confirmRemove === x.name ? "warn" : "", () => {
      if (this.confirmRemove !== x.name) { this.confirmRemove = x.name; this.renderDetail(); return; }
      this.friends = this.friends.filter((p) => p !== x);
      this.note = `${x.name} was removed from your friends.`; this.confirmRemove = ""; this.picked = this.friends[0]?.name ?? ""; this.render();
    });
    return bar;
  }
}
