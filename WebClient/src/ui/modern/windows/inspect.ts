// Ported from play/hudInspect.ts (InspectMock): the new UI's inspect window, a modern InspectDialog (Client/MirScenes/
// Dialogs/MainDialogs.cs; the classic port is src/ui/inspectWindow.ts) over the Inspect model (src/game/inspect.ts).
// It opens when S.PlayerInspect arrives (Ctrl + right-click on a player / hero: GameUi command "inspect" -> the model's
// inspect() with its 500 ms gate; a ranking row; the chat name menu). The window is built lazily, so the open-on-event
// hook lives outside it: the registry entry's watch hook (windowRegistry.ts, run by modernUi.ts at UI start) calls
// watchInspect(host).
//
// Changes from the mock:
//  - real data only: the banner (class, name, level, guild + guild rank, lover) and the equipment come from the model;
//    the equipment cells are read-only item cells on a custom grid ("x:inspect", core/itemCells.ts) whose tooltip uses
//    the INSPECTED player's level / class / gender (GameScene.CreateItemLabel with Inspect) with your worn item in the
//    same slot as the compare row;
//  - the paperdoll is the classic CharacterPage.AfterDraw (realItem armour / weapon, wings, helmet or hair), drawn from
//    picture offsets and scaled by arithmetic (no layout reads, no topPointX pixel scan);
//  - Stats: the gear part of UserObject.RefreshStats (stats.ts refreshStats with the equipment minus without it: item
//    stats, added stats, awakening, sockets, set and Mir set bonuses), theirs beside yours; base stats, skills and buffs
//    are left out on both sides;
//  - Compare: slot by slot against your equipment, the main stat changes as chips (no invented gear score);
//  - Profile: S.PlayerProfile's text (the tab is hidden while it is empty);
//  - buttons: Whisper (chat "/name "), Group invite, Mail (when a mail window is registered), Add friend / friend
//    request (hidden when they are already a friend), Trade (as the classic dialog: always offered to a player; the
//    server pairs you with the player you face). All hidden for a hero (InspectDialog hides them for IsHero).
// Hidden (no Crystal data): the gear score, the "Ranked #N" title, location, PK points, play time, badges, set chips,
// Report (no player-report system) and Observe (observer mode is not ported; the classic hides it too).
// Narrow layout (below a 1040 px layer): the doll column becomes the "Equipment" tab.

import type { UserItem } from "../../../protocol/server.ts";
import { EquipmentSlot, MirClass, MirGender, Stat } from "../../../protocol/enums.generated.ts";
import { defaultBaseStats, refreshStats } from "../../../game/stats.ts";
import { serverRules } from "../../../game/serverRules.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import type { ItemLabelContext } from "../../itemLabel.ts";
import { button, h, injectCss, setText, svg, tabs, UiWindow, type Btn } from "../core/index.ts";
import { className, classTag, fmt, range, signed } from "../core/format.ts";
import { classVar, gradeVar } from "../core/styles.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { baseBody, dollPicture, type DollPicture } from "./character.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const INSPECT_KEY = "inspect";
/** The custom cell grid of the inspected equipment (read-only). */
const GRID = "x:inspect" as const;

const E = EquipmentSlot;
/** The Character window's order: left column, right column, bottom row. */
const LEFT = [E.Weapon, E.Armour, E.Torch, E.BraceletL, E.RingL];
const RIGHT = [E.Helmet, E.Necklace, E.Amulet, E.BraceletR, E.RingR];
const BOTTOM = [E.Belt, E.Boots, E.Stone, E.Mount];
const SLOTS = [...LEFT, ...RIGHT, ...BOTTOM];
const SLOT_NAME: Readonly<Record<number, string>> = {
  [E.Weapon]: "Weapon", [E.Armour]: "Armour", [E.Helmet]: "Helmet", [E.Torch]: "Torch", [E.Necklace]: "Necklace",
  [E.BraceletL]: "Bracelet", [E.BraceletR]: "Bracelet", [E.RingL]: "Ring", [E.RingR]: "Ring", [E.Amulet]: "Amulet",
  [E.Belt]: "Belt", [E.Boots]: "Boots", [E.Stone]: "Stone", [E.Mount]: "Mount",
};

/** Ranged rows: [label, min stat, max stat]. */
const RANGES: readonly [string, number, number][] = [
  ["AC", Stat.MinAC, Stat.MaxAC], ["MAC", Stat.MinMAC, Stat.MaxMAC], ["DC", Stat.MinDC, Stat.MaxDC], ["MC", Stat.MinMC, Stat.MaxMC], ["SC", Stat.MinSC, Stat.MaxSC],
];
/** Single rows: [label, stat]. */
const EXTRA: readonly [string, number][] = [
  ["Accuracy", Stat.Accuracy], ["Agility", Stat.Agility], ["Attack speed", Stat.AttackSpeed], ["HP", Stat.HP], ["MP", Stat.MP],
  ["Holy", Stat.Holy], ["Luck", Stat.Luck], ["Magic resist", Stat.MagicResist], ["Poison resist", Stat.PoisonResist],
  ["Reflect", Stat.Reflect], ["Critical rate", Stat.CriticalRate], ["Critical damage", Stat.CriticalDamage], ["Freezing", Stat.Freezing],
  ["Poison attack", Stat.PoisonAttack], ["HP drain %", Stat.HPDrainRatePercent],
];
/** Compare-tab chips: [stat, label] (the tooltip's compare set). */
const CHIPS: readonly [number, string][] = [
  [Stat.MaxDC, "DC"], [Stat.MaxMC, "MC"], [Stat.MaxSC, "SC"], [Stat.MaxAC, "AC"], [Stat.MaxMAC, "MAC"], [Stat.Accuracy, "Acc"],
  [Stat.Agility, "Agi"], [Stat.AttackSpeed, "A.Spd"], [Stat.HP, "HP"], [Stat.MP, "MP"], [Stat.Luck, "Luck"],
];

/** The doll's box (HUD px) between the cell columns. */
const FIG_W = 184, FIG_H = 216;

const CSS = `
.hudm .inwin { width: 860px; }
.hudm .inwin .panel { height: 560px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .inwin .ban { display: flex; align-items: center; gap: 14px; padding: 10px 16px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 16%, transparent), transparent 60%); }
.hudm .inwin .ban .cl { width: 46px; height: 46px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800; color: #fff;
  border: 2px solid var(--cc, var(--ac)); background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); box-shadow: 0 0 0 2px #000; }
.hudm .inwin .ban .nm { flex: 1; min-width: 0; } .hudm .inwin .ban .nm b { font-size: 20px; color: #fff; }
.hudm .inwin .ban .nm small { display: block; font-size: 11px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .inwin .ban .nm small em { font-style: normal; color: var(--ac); }
.hudm .inwin .ban .lv { display: inline-flex; align-items: center; gap: 3px; color: var(--bad-soft); }
.hudm .inwin .ban .lv svg { width: 12px; height: 12px; }
.hudm .inwin .ibody { flex: 1; min-height: 0; display: flex; }
.hudm .inwin .dcol { width: 300px; flex: none; padding: 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .inwin .doll { display: flex; flex-direction: column; gap: 8px; align-items: center; }
.hudm .inwin .drow { position: relative; width: 280px; height: ${FIG_H}px; display: flex; justify-content: space-between; }
.hudm .inwin .drow .col { display: flex; flex-direction: column; gap: 4px; z-index: 1; }
.hudm .inwin .figure { position: absolute; left: 48px; top: 0; width: ${FIG_W}px; height: ${FIG_H}px; overflow: hidden; border-radius: var(--rad);
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%), radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .inwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
.hudm .inwin .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .inwin .org div.hd { background-size: 100% 100%; image-rendering: auto; }
.hudm .inwin .org div.wing { mix-blend-mode: plus-lighter; }
.hudm .inwin .bottom { display: flex; justify-content: center; gap: 4px; }
.hudm .inwin .icell > .e { font-size: 8px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; color: var(--dim); opacity: .6; }
.hudm .inwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .inwin .main > .ui-tabs { padding: 6px 12px 0; }
.hudm .inwin .pane { flex: 1; min-height: 0; overflow-y: auto; padding: 10px 14px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .inwin .sr { display: grid; grid-template-columns: 110px 80px 1fr 80px; align-items: center; gap: 8px; padding: 4px 0; font-size: 12px; }
.hudm .inwin .sr.hd { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .inwin .sr span:first-child { color: var(--dim); } .hudm .inwin .sr b { color: #fff; font-variant-numeric: tabular-nums; text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .inwin .sr b.me { text-align: left; color: var(--ac); }
.hudm .inwin .vs { position: relative; height: 8px; display: flex; } .hudm .inwin .vs i { height: 100%; }
.hudm .inwin .vs .t { margin-left: auto; background: linear-gradient(90deg, transparent, var(--gold)); border-radius: 4px 0 0 4px; }
.hudm .inwin .vs .m { background: linear-gradient(90deg, var(--ac), transparent); border-radius: 0 4px 4px 0; }
.hudm .inwin .vs::after { content: ""; position: absolute; left: 50%; top: -2px; bottom: -2px; width: 1px; background: var(--fr); }
.hudm .inwin .vs .h { width: 50%; display: flex; } .hudm .inwin .vs .h.r { justify-content: flex-start; }
.hudm .inwin .cr { display: grid; grid-template-columns: 64px 1fr 120px 1fr; align-items: center; gap: 8px; padding: 3px 0; font-size: 11px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .inwin .cr > span:first-child { color: var(--dim); } .hudm .inwin .cr .c { display: flex; align-items: center; gap: 6px; min-width: 0; }
.hudm .inwin .cr .c span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--gc, var(--tx)); }
.hudm .inwin .cr .d { display: flex; flex-wrap: wrap; gap: 3px; justify-content: center; }
.hudm .inwin .cr .d i { font-style: normal; font-weight: 700; font-size: 10px; padding: 0 5px; border-radius: 6px; }
.hudm .inwin .cr .d i.up { color: var(--gold); background: color-mix(in srgb, var(--gold) 14%, transparent); }
.hudm .inwin .cr .d i.dn { color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .inwin .cr .d i.eq { color: var(--dim); }
.hudm .inwin .cr.hd { font-size: 9px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .inwin .ptext { padding: 10px 12px; border-radius: var(--rad); font-size: 13px; line-height: 1.45; color: #fff; white-space: pre-wrap; overflow-wrap: anywhere;
  border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .inwin .acts { display: flex; gap: 6px; padding: 8px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .inwin .acts .btn { flex: 1; min-width: 0; }
.hudm .inwin .pane > .lbl { margin: 10px 0 4px; }
.hudm .inwin .pane > .doll { padding-top: 6px; }
.hudm.narrow .inwin { width: 600px; }
.hudm.narrow .inwin .dcol { display: none; }
.hudm.narrow .inwin .sr { grid-template-columns: 96px 70px 1fr 70px; }
.hudm.narrow .inwin .cr { grid-template-columns: 54px 1fr 96px 1fr; }
.hudm.narrow .inwin .acts .btn span { overflow: hidden; text-overflow: ellipsis; }
`;

type Tab = "Equipment" | "Stats" | "Compare" | "Profile";

/** UserObject.RefreshStats' equipment part: stats with `equipment` minus stats without it (base, skills, buffs out). */
function gearStats(host: ModernUiHost, cls: number, level: number, equipment: readonly (UserItem | null)[]): Record<number, number> {
  const infos = host.ctx.models.itemInfos;
  const base = defaultBaseStats(cls);
  const input = { cls, level, inventory: [], infos, magics: [], buffs: [], guildBuffStats: null, baseStats: base };
  const worn = refreshStats({ ...input, equipment }).stats, bare = refreshStats({ ...input, equipment: [] }).stats;
  const out: Record<number, number> = {};
  for (const k of new Set([...Object.keys(worn), ...Object.keys(bare)])) {
    const n = Number(k), v = (worn[n] ?? 0) - (bare[n] ?? 0);
    if (v) out[n] = v;
  }
  return out;
}

/** One item's own stats (realItem for the wearer + added stats), for the compare chips. */
function itemStats(host: ModernUiHost, item: UserItem | null | undefined, cls: number, level: number): Record<number, number> {
  const out: Record<number, number> = {};
  if (!item) return out;
  const infos = host.ctx.models.itemInfos, info = infos.get(item.ItemIndex);
  if (!info) return out;
  const real = infos.realItem(info, level, cls);
  for (const src of [real.Stats, item.AddedStats]) for (const k of Object.keys(src)) { const n = Number(k); out[n] = (out[n] ?? 0) + (src[n] ?? 0); }
  return out;
}

// — Paperdoll (CharacterPage.AfterDraw) —

// The base body is character.ts baseBody (one cache for both windows).

interface Layer { pic: DollPicture; dx: number; dy: number; cls: string }

class InspectWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly ban = { badge: h("div", "cl"), name: h("b"), line: h("small"), guild: h("small") };
  private readonly dcol = h("div", "dcol");
  private readonly doll: HTMLElement;
  private readonly org = h("div", "org");
  private readonly main = h("div", "main");
  private readonly pane = h("div", "pane");
  private readonly acts = h("div", "acts");
  private tabBar: ReturnType<typeof tabs> | null = null;
  private tabKey = "";
  private tab: Tab = "Stats";
  private readonly btn: { whisper: Btn; group: Btn; mail: Btn; friend: Btn; trade: Btn };
  /** The doll drawn for this key (items, gender, hair, class); a newer draw drops an older one. */
  private dollKey = "";
  private dollGen = 0;
  /** Cells of the Compare pane (rebuilt with it). */
  private paneScope: AbortController | null = null;
  private lastName = "";
  /** The tab the pane shows (its scroll is kept while the same tab re-renders). */
  private paneTab: Tab | null = null;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "inspect inwin", 50, 40);
    injectCss("modern-inspect", CSS);
    this.m = ui.ctx.models;
    const m = this.m, model = m.inspect;
    this.lastName = model.name;
    this.tab = ui.rt.layout.narrow ? "Equipment" : "Stats";

    // The inspected equipment as a read-only custom grid (never a drop target).
    const offTarget = ui.cells.addTarget(GRID, { item: (i) => model.items[i] ?? null, accepts: () => null, drop: () => {} });
    this.signal.addEventListener("abort", offTarget, { once: true });
    // Their cells' tooltip: the inspected player as the wearer; every other cell keeps the integrator's hover.
    const prev = ui.cells.onHover;
    ui.cells.onHover = (cell, item) => {
      if (cell && item && cell.ref.grid === GRID) {
        ui.tooltip.show(cell.el, item, this.labelContext(), { inspect: true, compareWith: m.inventory.equipment[cell.ref.index] ?? null });
      } else prev(cell, item);
    };

    // Banner
    this.ban.badge.setAttribute("aria-hidden", "true");
    const ban = h("div", "ban", this.ban.badge, h("div", "nm", this.ban.name, this.ban.line, this.ban.guild));

    // Doll: two columns of five cells around the figure, four cells below
    const cell = (slot: number): HTMLElement => this.ui.cells.cell(GRID, slot, { readOnly: true, empty: document.createTextNode(SLOT_NAME[slot] ?? ""), signal: this.signal }).el;
    const col = (slots: readonly number[]) => h("div", "col", ...slots.map(cell));
    this.doll = h("div", "doll", h("div", "drow", col(LEFT), h("div", "figure", this.org), col(RIGHT)), h("div", "bottom", ...BOTTOM.map(cell)));

    // Buttons (InspectDialog's player buttons + the name label's whisper)
    this.btn = {
      whisper: button("Whisper", () => this.ui.chat.prefill(`/${model.name} `), { cls: "main", icon: "chat", title: "Whisper (/name in the chat box)" }),
      group: button("Invite", () => model.addToGroup(), { icon: "group", title: "Invite to group" }),
      mail: button("Mail", () => this.composeMail(), { icon: "mail", title: "Send mail" }),
      friend: button("Add friend", () => model.addFriend(), { icon: "friends" }),
      trade: button("Trade", () => model.tradeRequest(), { icon: "swap", title: "Trade (with the player you face)" }),
    };
    this.acts.append(this.btn.whisper.el, this.btn.group.el, this.btn.mail.el, this.btn.friend.el, this.btn.trade.el);

    this.main.append(this.pane, this.acts);
    this.el.append(h("div", "panel", this.titleBar("INSPECT", "Close"), ban, h("div", "ibody", this.dcol, this.main)));

    // Model / world events: invalidate (re-rendered on the next frame while open, else on the next open).
    const on = (off: () => void) => this.signal.addEventListener("abort", off, { once: true });
    on(model.received.on(() => {
      if (model.name !== this.lastName) { this.lastName = model.name; this.tab = this.ui.rt.layout.narrow ? "Equipment" : "Stats"; }
      this.dollKey = "";
      this.ui.cells.refresh();
      this.invalidate();
    }));
    on(model.profile.on(() => this.invalidate()));
    on(this.ui.events.on((e) => { if (e.kind === "itemInfo") { this.dollKey = ""; this.invalidate(); } }));
    on(m.inventory.subscribe(() => { if (this.tab === "Stats" || this.tab === "Compare") this.invalidate(); }));
    on(m.friends.changed.on(() => this.invalidate()));
    on(this.ui.rt.windowsChanged.on((k) => { if (k === "mail") this.invalidate(); }));
    on(this.ui.rt.layoutChanged.on(() => this.invalidate()));
  }

  /** GameScene.CreateItemLabel with Inspect: the inspected player's level, class and gender. */
  private labelContext(): ItemLabelContext {
    const base = this.ui.labelContext(), infos = this.m.itemInfos, model = this.m.inspect;
    const level = model.level, job = model.cls;
    return { ...base, level, job, gender: model.gender, realItem: (info) => infos.realItem(info, level, job) };
  }

  /** MailButton: MailComposeLetterDialog.ComposeMail(Name). The mail window (phase 3e) may expose composeTo(name). */
  private composeMail(): void {
    const w = this.ui.rt.window<UiWindow & { composeTo?: (name: string) => void }>("mail");
    if (!w) return;
    if (typeof w.composeTo === "function") w.composeTo(this.m.inspect.name);
    else this.ui.openWindow("mail", true);
  }

  protected override closed(): void {
    this.paneScope?.abort();
    this.paneScope = null;
    this.invalidate(); // the Compare cells are gone: rebuild on the next open
  }

  protected override disposed(): void { this.dollGen++; }

  protected override render(): void {
    const model = this.m.inspect, narrow = this.ui.rt.layout.narrow;

    // Banner
    this.ban.badge.style.setProperty("--cc", classVar(model.cls));
    setText(this.ban.badge, classTag(model.cls));
    setText(this.ban.name, model.name);
    setText(this.ban.line, `Level ${model.level} ${className(model.cls)}${model.isHero ? " · Hero" : ""}`);
    const guild = model.guildName ? [h("em", "", model.guildName), model.guildRank ? ` ${model.guildRank}` : ""] : [];
    const lover = model.loverName ? h("span", "lv", svg("heartFull"), model.loverName) : null;
    this.ban.guild.replaceChildren(...guild, guild.length && lover ? " · " : "", lover ?? "");
    this.ban.guild.hidden = !guild.length && !lover;

    // Tabs: the doll column is the Equipment tab in the narrow layout; Profile only with a profile text
    const hasProfile = !model.isHero && model.profileText.trim() !== "";
    const list: Tab[] = [...(narrow ? ["Equipment" as const] : []), "Stats", "Compare", ...(hasProfile ? ["Profile" as const] : [])];
    if (!list.includes(this.tab)) this.tab = narrow ? "Equipment" : "Stats";
    const key = list.join(",");
    if (key !== this.tabKey || !this.tabBar) {
      this.tabKey = key;
      this.tabBar?.el.remove();
      this.tabBar = tabs(list.map((t) => t.toUpperCase()), list.indexOf(this.tab), (i) => { this.tab = list[i]!; this.invalidate(); });
      this.main.prepend(this.tabBar.el);
    } else this.tabBar.select(list.indexOf(this.tab));

    // Doll: in its column, or in the pane (narrow Equipment tab)
    if (!narrow) { if (this.doll.parentElement !== this.dcol) this.dcol.append(this.doll); }
    this.drawDoll();

    // Pane
    this.paneScope?.abort();
    this.paneScope = new AbortController();
    const keep = this.paneTab === this.tab ? this.pane.scrollTop : 0;
    this.paneTab = this.tab;
    this.pane.replaceChildren();
    if (this.tab === "Equipment") this.pane.append(this.doll);
    else if (this.tab === "Stats") this.stats();
    else if (this.tab === "Compare") this.compare(this.paneScope.signal);
    else this.profile();
    this.pane.scrollTop = keep;

    // Buttons: none for a hero; Mail only with a mail window; Add friend only for a non-friend
    this.acts.hidden = model.isHero;
    this.btn.mail.el.hidden = !this.ui.rt.hasWindow("mail");
    const friend = this.m.friends.list.some((f) => !f.Blocked && f.Name === model.name);
    this.btn.friend.el.hidden = friend;
    const requests = serverRules(this.ui.ctx.session).rules?.FriendRequestsEnabled ?? false;
    this.btn.friend.setLabel(requests ? "Friend request" : "Add friend");
    this.btn.friend.el.title = requests ? "Send a friend request (they must accept)" : "Add to friends list";
  }

  private stats(): void {
    const model = this.m.inspect, player = this.ui.ctx.player;
    const t = gearStats(this.ui, model.cls, model.level, model.items);
    const me = gearStats(this.ui, player.user?.look.class ?? MirClass.Warrior, player.level, this.m.inventory.equipment);
    const p = this.pane;
    p.append(h("div", "sr hd", h("span", "", "From gear"), h("b", "", model.name), h("span", ""), h("b", "me", "You")));
    const row = (name: string, a: number, b: number, ta: string, tb: string) => {
      const max = Math.max(1, a, b);
      const ti = h("i", "t"), mi = h("i", "m");
      ti.style.width = Math.max(0, a) / max * 100 + "%";
      mi.style.width = Math.max(0, b) / max * 100 + "%";
      p.append(h("div", "sr", h("span", "", name), h("b", "", ta), h("div", "vs", h("div", "h", ti), h("div", "h r", mi)), h("b", "me", tb)));
    };
    for (const [n, lo, hi] of RANGES) {
      const a0 = t[lo] ?? 0, a1 = t[hi] ?? 0, b0 = me[lo] ?? 0, b1 = me[hi] ?? 0;
      if (a1 || b1 || a0 || b0) row(n, a1, b1, range(a0, a1), range(b0, b1));
    }
    p.append(h("div", "lbl", "Other"));
    let any = false;
    for (const [n, k] of EXTRA) {
      const a = t[k] ?? 0, b = me[k] ?? 0;
      if (a || b) { any = true; row(n, a, b, signed(a), signed(b)); }
    }
    if (!any) p.append(h("div", "empty-note", "No other stats from gear."));
    p.append(h("div", "lbl", "Item, socket, awakening and set stats; base stats, skills and buffs are not included."));
  }

  private compare(signal: AbortSignal): void {
    const model = this.m.inspect, player = this.ui.ctx.player, infos = this.m.itemInfos;
    const myCls = player.user?.look.class ?? MirClass.Warrior;
    const p = this.pane;
    p.append(h("div", "cr hd", h("span", "", "Slot"), h("span", "", model.name), h("span", "", "Theirs vs yours"), h("span", "", "You")));
    const side = (c: ItemCell, item: UserItem | null | undefined) => {
      const info = item ? infos.get(item.ItemIndex) : undefined;
      const name = h("span", "", item ? info?.Name ?? "…" : "—");
      if (info) name.style.setProperty("--gc", gradeVar(info.Grade));
      return h("div", "c", c.el, name);
    };
    for (const slot of SLOTS) {
      const theirs = model.items[slot], mine = this.m.inventory.equipment[slot];
      const a = itemStats(this.ui, theirs, model.cls, model.level), b = itemStats(this.ui, mine, myCls, player.level);
      const d = h("div", "d");
      for (const [k, label] of CHIPS) {
        const v = (a[k] ?? 0) - (b[k] ?? 0);
        if (v) d.append(h("i", v > 0 ? "up" : "dn", `${v > 0 ? "+" : ""}${fmt(v)} ${label}`));
        if (d.childElementCount >= 4) break;
      }
      if (!d.childElementCount) d.append(h("i", "eq", theirs || mine ? "=" : ""));
      const tc = this.ui.cells.cell(GRID, slot, { readOnly: true, size: 30, signal });
      const mc = this.ui.cells.cell("eq", slot, { readOnly: true, size: 30, signal });
      p.append(h("div", "cr", h("span", "", SLOT_NAME[slot] ?? ""), side(tc, theirs), d, side(mc, mine)));
    }
    p.append(h("div", "lbl", "Gold: their item is higher; blue: yours is."));
  }

  private profile(): void {
    this.pane.append(h("div", "lbl", "Profile"), h("div", "ptext", this.m.inspect.profileText));
  }

  /** CharacterPage.AfterDraw: base body, armour (realItem), wings (Prguse2), weapon (realItem), helmet or hair. */
  private drawDoll(): void {
    const model = this.m.inspect, infos = this.m.itemInfos, items = model.items;
    const key = `${model.name}|${model.cls}|${model.gender}|${model.hair}|${items.map((i) => i ? `${i.UniqueID}:${i.ItemIndex}:${infos.get(i.ItemIndex) ? 1 : 0}` : "").join(",")}`;
    if (key === this.dollKey) return;
    this.dollKey = key;
    const gen = ++this.dollGen;
    const real = (it: UserItem | null | undefined) => {
      const info = it ? infos.get(it.ItemIndex) : undefined;
      return info ? infos.realItem(info, model.level, model.cls) : undefined;
    };
    const armour = real(items[E.Armour]), weapon = real(items[E.Weapon]);
    const helmet = items[E.Helmet] ? infos.get(items[E.Helmet]!.ItemIndex) : undefined;
    const female = model.gender !== MirGender.Male;
    // Wings: the desktop takes the gender offset from MapObject.User (the inspecting player)
    const userMale = (this.ui.ctx.player.user?.look.gender ?? MirGender.Male) === MirGender.Male;
    const effect = armour?.Effect ?? 0;
    const wing = effect > 0 ? 1200 + (effect === 1 ? 2 : 4) + (userMale ? 0 : 1) : -1;
    const assassin = model.cls === MirClass.Assassin;
    const hairIndex = 441 + model.hair + (assassin ? 20 : 0) + (female ? 40 : 0);
    const hairDx = assassin ? (female ? 4 : 6) : 0, hairDy = assassin ? (female ? 18 : 25) : 0;
    const want: Promise<Layer | null>[] = [
      baseBody(female).then((pic) => pic && { pic, dx: 0, dy: 0, cls: "" }),
      armour ? dollPicture(Lib.StateItems, armour.Image).then((pic) => pic && { pic, dx: 0, dy: 0, cls: "" }) : Promise.resolve(null),
      wing >= 0 ? dollPicture(Lib.Prguse2, wing).then((pic) => pic && { pic, dx: 0, dy: 0, cls: "wing" }) : Promise.resolve(null),
      weapon ? dollPicture(Lib.StateItems, weapon.Image).then((pic) => pic && { pic, dx: 0, dy: 0, cls: "" }) : Promise.resolve(null),
      items[E.Helmet]
        ? (helmet ? dollPicture(Lib.StateItems, helmet.Image).then((pic) => pic && { pic, dx: 0, dy: 0, cls: "" }) : Promise.resolve(null))
        : libPicture(Lib.Prguse, hairIndex).then((pic) => pic && { pic, dx: hairDx, dy: hairDy, cls: "" }),
    ];
    void Promise.all(want).then((all) => {
      if (gen !== this.dollGen) return; // a newer draw (or dispose) superseded this one
      const layers = all.filter((l): l is Layer => !!l);
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
      const els: HTMLElement[] = [];
      for (const l of layers) {
        const x = l.pic.x + l.dx, y = l.pic.y + l.dy;
        const d = h("div", l.cls + (l.pic.hd ? " hd" : ""));
        Object.assign(d.style, { left: x + "px", top: y + "px", width: l.pic.width + "px", height: l.pic.height + "px", backgroundImage: `url("${l.pic.url}")` });
        els.push(d);
        if (l.cls !== "wing") { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x + l.pic.width); y1 = Math.max(y1, y + l.pic.height); }
      }
      this.org.replaceChildren(...els);
      if (!els.length || !Number.isFinite(x0)) { this.org.style.transform = ""; return; }
      // Scale to the figure box by arithmetic (no layout reads): the body's box, centred
      const k = Math.min((FIG_H - 8) / (y1 - y0), (FIG_W - 8) / (x1 - x0));
      const left = (FIG_W - (x1 - x0) * k) / 2 - x0 * k, top = (FIG_H - (y1 - y0) * k) / 2 - y0 * k;
      this.org.style.transform = `translate(${left}px, ${top}px) scale(${k})`;
    });
  }
}

registerWindow(INSPECT_KEY, (host) => new InspectWindow(host), { binds: [], watch: (host) => watchInspect(host) });

/** Opens the inspect window whenever S.PlayerInspect arrives (GameScene.PlayerInspect -> InspectDialog.Show). The window
 *  is built lazily, so this lives outside it: the entry's watch hook runs it once per game at UI start. Returns its
 *  removal; it also stops with the UI scope. */
export function watchInspect(host: ModernUiHost): () => void {
  const off = host.ctx.models.inspect.received.on(() => { host.openWindow(INSPECT_KEY, true); });
  host.rt.signal.addEventListener("abort", off, { once: true });
  return off;
}
