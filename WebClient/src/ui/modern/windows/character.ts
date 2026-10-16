// Ported from play/hudCharacter.ts (CharacterMock, baseBody, dollInto, topPointX): the new UI's character window, wired
// to the real game. Title bar, name / level + class / guild / guild rank, a paperdoll between two columns of five
// equipment cells, four cells under it, and an arrow tab that slides the Stats panel out (and back).
// Changes from the mock:
//  - the 14 cells are core/itemCells.ts cells on the Equipment grid ("eq", index = EquipmentSlot): pick / drag / drop /
//    right-click go through core/drag.ts + dragRoutes.ts (C.EquipItem / C.RemoveItem / C.MergeItem via Inventory, the
//    cell locked until the server answers; amulet stacks keep their count, durability colours from the cell service);
//  - the doll follows the classic CharacterPage.AfterDraw logic (src/ui/characterWindow.ts drawDoll): armour / weapon
//    by ItemInfos.realItem, the helmet by its own Image, else the hair (Prguse 441 + hair, assassin / female offsets),
//    the wings (Prguse2 1202 / 1204 + gender, additive) for an armour with WingEffect 1 / 2, the gender's base body;
//  - topPointX is memoised by picture URL (Performance.md T4-4); the doll redraws only when its look key changes;
//  - the Stats panel reads UserStats (ctx.player.stats) with the classic STATS I / II formats, HP / MP rows included;
//    it flips to the window's left side when it would open off the layer's right edge (Review-Core.md);
//  - name / level / class from the user object, guild name + rank from the guild model; rows without data are hidden;
//  - no mock data, no fake wear() / CorrectSlot copies, no 'sel' flash timer. Live values refresh on a 250 ms ticker
//    while the window is shown (PlayerState has no change events) and on inventory / guild / item-info events.
// Hotkeys: the registry default (MENU_WINDOWS: KeybindOptions.Equipment + Equipment2) through the input router.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { EquipmentSlot, MirClass, MirGender, Stat } from "../../../protocol/enums.generated.ts";
import type { ItemInfos } from "../../../game/items.ts";
import type { UserStats } from "../../../game/stats.ts";
import { Lib, libPicture, type LibPicture } from "../../mir/controls.ts";
import { UiWindow } from "../core/wm.ts";
import { asButton, h, injectCss, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { className } from "../core/format.ts";
import { classVar } from "../core/styles.ts";
import type { ItemCell } from "../core/itemCells.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

const E = EquipmentSlot;
/** [slot, slot name, empty-slot icon]. */
type SlotDef = readonly [number, string, IconName];
const LEFT: readonly SlotDef[] = [
  [E.Weapon, "Weapon", "sword"], [E.Armour, "Armour", "shield"], [E.Torch, "Torch", "sun"],
  [E.BraceletL, "Bracelet", "ring"], [E.RingL, "Ring", "ring"],
];
const RIGHT: readonly SlotDef[] = [
  [E.Helmet, "Helmet", "cap"], [E.Necklace, "Necklace", "gem"], [E.Amulet, "Amulet", "tag"],
  [E.BraceletR, "Bracelet", "ring"], [E.RingR, "Ring", "ring"],
];
const BOTTOM: readonly SlotDef[] = [
  [E.Belt, "Belt", "pouch"], [E.Boots, "Boots", "walk"], [E.Stone, "Stone", "gem"], [E.Mount, "Mount", "horse"],
];

/** What the Stats rows read. */
interface StatSource { s(k: number): number; hp: number; mp: number; w: UserStats | null; expPct: number }
/** The Stats panel (CharacterDialog STATS I / II, characterWindow.ts render formats); null = a section break. */
const STAT_ROWS: readonly (readonly [string, (v: StatSource) => string] | null)[] = [
  ["AC", (v) => `${v.s(Stat.MinAC)}-${v.s(Stat.MaxAC)}`],
  ["MAC", (v) => `${v.s(Stat.MinMAC)}-${v.s(Stat.MaxMAC)}`],
  ["DC", (v) => `${v.s(Stat.MinDC)}-${v.s(Stat.MaxDC)}`],
  ["MC", (v) => `${v.s(Stat.MinMC)}-${v.s(Stat.MaxMC)}`],
  ["SC", (v) => `${v.s(Stat.MinSC)}-${v.s(Stat.MaxSC)}`],
  null,
  ["HP", (v) => `${v.hp}/${v.s(Stat.HP)}`],
  ["MP", (v) => `${v.mp}/${v.s(Stat.MP)}`],
  null,
  ["Accuracy", (v) => `+${v.s(Stat.Accuracy)}`],
  ["Agility", (v) => `+${v.s(Stat.Agility)}`],
  ["Attack Speed", (v) => `${v.s(Stat.AttackSpeed)}`],
  ["Luck", (v) => `${v.s(Stat.Luck)}`],
  ["Critical Rate", (v) => `${v.s(Stat.CriticalRate)}%`],
  ["Critical Damage", (v) => `${v.s(Stat.CriticalDamage)}`],
  null,
  ["Magic Resist", (v) => `+${v.s(Stat.MagicResist)}`],
  ["Poison Resist", (v) => `+${v.s(Stat.PoisonResist)}`],
  ["Health Recovery", (v) => `+${v.s(Stat.HealthRecovery)}`],
  ["Mana Recovery", (v) => `+${v.s(Stat.SpellRecovery)}`],
  ["Poison Recovery", (v) => `+${v.s(Stat.PoisonRecovery)}`],
  ["Holy", (v) => `+${v.s(Stat.Holy)}`],
  ["Freeze", (v) => `+${v.s(Stat.Freezing)}`],
  ["Poison Attack", (v) => `+${v.s(Stat.PoisonAttack)}`],
  null,
  ["Bag Weight", (v) => `${v.w?.currentBagWeight ?? 0}/${v.s(Stat.BagWeight)}`],
  ["Wear Weight", (v) => `${v.w?.currentWearWeight ?? 0}/${v.s(Stat.WearWeight)}`],
  ["Hand Weight", (v) => `${v.w?.currentHandWeight ?? 0}/${v.s(Stat.HandWeight)}`],
  null,
  ["Experience", (v) => `${Math.round(v.expPct * 100) / 100}%`],
  ["EXP Rate", (v) => `+${v.s(Stat.ExpRatePercent)}%`],
  ["Drop Rate", (v) => `+${v.s(Stat.ItemDropRatePercent)}%`],
  ["Gold Rate", (v) => `+${v.s(Stat.GoldDropRatePercent)}%`],
];

/** The Stats panel width (CSS .stats width). */
const STATS_W = 270;

const CSS = `
.hudm .cwin { --winh: 476px; width: 360px; }
.hudm .cwin .panel { height: var(--winh); box-sizing: border-box; }
.hudm .cwin .who { text-align: center; padding: 8px 0 4px; line-height: 1.25; min-height: 58px; }
.hudm .cwin .who b { display: block; font-size: 16px; color: #fff; }
.hudm .cwin .who span { display: block; font-size: 11px; color: var(--dim); }
.hudm .cwin .who span.cls { color: var(--cc, var(--dim)); }
.hudm .cwin .who em { display: block; font-style: normal; font-size: 12px; font-weight: 600; color: var(--ac); }
.hudm .cwin .body { position: relative; margin: 6px 22px 0; height: 300px; }
.hudm .cwin .col { position: absolute; top: 0; bottom: 0; display: flex; flex-direction: column; justify-content: space-between; }
.hudm .cwin .col.l { left: 0; } .hudm .cwin .col.r { right: 0; }
.hudm .cwin .figure { position: absolute; left: 52px; right: 52px; top: 0; bottom: 0; overflow: hidden; border-radius: var(--rad); pointer-events: none;
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%),
    radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .cwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
.hudm .cwin .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .cwin .org div.wing { mix-blend-mode: plus-lighter; }
.hudm .cwin .bottom { display: flex; justify-content: center; gap: 6px; margin: 10px 0 0; }
/* The arrow tab on the right edge: opens / closes the Stats panel. */
.hudm .cwin .tab { position: absolute; right: 3px; top: 120px; width: 14px; height: 44px; display: flex; align-items: center; justify-content: center;
  cursor: pointer; color: var(--ac); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); background: linear-gradient(180deg, var(--pa), var(--pb)); }
.hudm .cwin .tab .ic { width: 12px; height: 12px; }
.hudm .cwin .tab:hover, .hudm .cwin .tab:focus-visible { border-color: var(--ac); color: #fff; }
.hudm .cwin .stats { position: absolute; left: 100%; top: 0; width: ${STATS_W}px; height: var(--winh); box-sizing: border-box; display: flex; flex-direction: column;
  border-radius: 0 var(--rad) var(--rad) 0; background: var(--page); border: 1px solid var(--fr); border-left: 0;
  box-shadow: 1px 0 0 #000, 0 1px 0 #000, 0 -1px 0 #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 10px 10px 30px rgba(0,0,0,.6);
  opacity: 0; transform: translateX(-30px); visibility: hidden; transition: opacity .18s ease-out, transform .18s ease-out, visibility 0s .18s; }
.hudm .cwin.open .stats { opacity: 1; transform: none; visibility: visible; transition: opacity .18s ease-out, transform .18s ease-out; }
.hudm .cwin.open .panel { border-top-right-radius: 0; border-bottom-right-radius: 0; }
/* Flipped: the panel opens on the window's left side (no room on the right). */
.hudm .cwin.flip .stats { left: auto; right: 100%; border-radius: var(--rad) 0 0 var(--rad); border-left: 1px solid var(--fr); border-right: 0; transform: translateX(30px); }
.hudm .cwin.flip.open .stats { transform: none; }
.hudm .cwin.flip.open .panel { border-radius: 0 var(--rad) var(--rad) 0; }
.hudm .cwin .stats .list { flex: 1; overflow-y: auto; padding: 6px 10px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cwin .stats .r { display: flex; justify-content: space-between; height: 21px; align-items: center; padding: 0 8px; font-size: 12px; border-radius: calc(var(--rad) * .5); }
.hudm .cwin .stats .r:nth-child(odd) { background: color-mix(in srgb, var(--frl) 7%, transparent); }
.hudm .cwin .stats .r span { color: var(--dim); }
.hudm .cwin .stats .r b { font-weight: 600; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .cwin .stats .gap { height: 8px; }
@media (prefers-reduced-motion: reduce) { .hudm .cwin .stats, .hudm .cwin.open .stats { transition: none; } }
`;

// — The paperdoll (shared: the inspect and hero windows may reuse Paperdoll + dollLook) —

/** One doll layer: a picture of a library, with an extra offset (the assassin hair). */
export interface DollPart { readonly lib: string; readonly index: number; readonly ox: number; readonly oy: number }
/** What the doll shows (CharacterPage.AfterDraw's inputs, resolved to pictures). */
export interface DollLook {
  readonly female: boolean;
  readonly wing: DollPart | null;
  readonly armour: DollPart | null;
  readonly weapon: DollPart | null;
  /** The helmet, else the hair (null: a helmet without a picture). */
  readonly head: DollPart | null;
}

const part = (lib: string, index: number, ox = 0, oy = 0): DollPart | null => index >= 0 ? { lib, index, ox, oy } : null;

/** CharacterPage.AfterDraw (src/ui/characterWindow.ts drawDoll) as data: armour / weapon by ItemInfos.realItem (the
 *  wearer's level and class), the helmet by its own Image, else the hair; wings (Prguse2) for WingEffect 1 / 2. */
export function dollLook(o: {
  equipment: readonly (UserItem | null)[]; infos: ItemInfos; level: number; cls: number; gender: number; hair: number; wingEffect: number;
}): DollLook {
  const info = (it: UserItem | null): ItemInfo | undefined => it ? o.infos.get(it.ItemIndex) : undefined;
  const real = (it: UserItem | null): ItemInfo | undefined => { const i = info(it); return i ? o.infos.realItem(i, o.level, o.cls) : undefined; };
  const armour = o.equipment[E.Armour] ?? null, weapon = o.equipment[E.Weapon] ?? null, helmet = o.equipment[E.Helmet] ?? null;
  const male = o.gender === MirGender.Male;
  const wing = armour && (o.wingEffect === 1 || o.wingEffect === 2) ? 1200 + (o.wingEffect === 1 ? 2 : 4) + (male ? 0 : 1) : -1;
  let head: DollPart | null;
  if (helmet) head = part(Lib.StateItems, info(helmet)?.Image ?? -1);
  else {
    const assassin = o.cls === MirClass.Assassin;
    head = part(Lib.Prguse, 441 + o.hair + (assassin ? 20 : 0) + (male ? 0 : 40), assassin ? (male ? 6 : 4) : 0, assassin ? (male ? 25 : 18) : 0);
  }
  return {
    female: !male,
    wing: part(Lib.Prguse2, wing),
    armour: armour ? part(Lib.StateItems, real(armour)?.Image ?? -1) : null,
    weapon: weapon ? part(Lib.StateItems, real(weapon)?.Image ?? -1) : null,
    head,
  };
}

const partKey = (p: DollPart | null): string => p ? `${p.lib}#${p.index}@${p.ox},${p.oy}` : "-";
const lookKey = (l: DollLook): string => [l.female ? "f" : "m", partKey(l.wing), partKey(l.armour), partKey(l.weapon), partKey(l.head)].join("|");

/** The paperdoll element: `el` is the figure box (size it with CSS); set() redraws only when the look changed. The
 *  doll is placed so the head's top point sits on the box's centre line, scaled as large as fits on both sides of it. */
export class Paperdoll {
  readonly el: HTMLElement;
  private org: HTMLElement;
  private key = "";

  constructor(cls = "figure") {
    this.org = h("div", "org");
    this.el = h("div", cls, this.org);
  }

  /** Draws `look` (call it while the box is laid out: it measures the box once per change). */
  set(look: DollLook): void {
    const key = lookKey(look);
    if (key === this.key) return;
    this.key = key;
    const org = h("div", "org"); // a fresh layer, so a doll still loading for the old look lands in the detached one
    this.org.replaceWith(org);
    this.org = org;
    void this.draw(look, org, key);
  }

  /** Forgets the drawn look (the next set() redraws, e.g. after the box changed size). */
  reset(): void { this.key = ""; }

  private async draw(look: DollLook, org: HTMLElement, key: string): Promise<void> {
    const parts = [look.wing, look.armour, look.weapon, look.head];
    const all = await Promise.all([baseBody(look.female), ...parts.map((p) => p ? libPicture(p.lib, p.index) : Promise.resolve(null))]);
    if (org !== this.org || !org.isConnected) return;
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity, drawn = 0;
    all.forEach((pic, i) => {
      if (!pic) return;
      const p = i > 0 ? parts[i - 1]! : null;
      const x = pic.x + (p?.ox ?? 0), y = pic.y + (p?.oy ?? 0);
      const d = h("div", i === 1 ? "wing" : "");
      Object.assign(d.style, { left: x + "px", top: y + "px", width: pic.width + "px", height: pic.height + "px", backgroundImage: `url(${pic.url})` });
      org.append(d);
      drawn++;
      x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x + pic.width); y1 = Math.max(y1, y + pic.height);
    });
    if (!drawn) { if (this.key === key) this.key = ""; return; } // nothing loaded: try again on the next set()
    const head = all[4] ?? null;
    const apex = head ? await topPointX(head) + (look.head?.ox ?? 0) : (x0 + x1) / 2;
    if (org !== this.org || !org.isConnected) return;
    const fw = this.el.clientWidth, fh = this.el.clientHeight;
    if (!fw || !fh) { if (this.key === key) this.key = ""; return; } // not laid out (closed): drawn again on the next open
    const k = Math.min((fh - 8) / (y1 - y0), (fw / 2 - 4) / Math.max(1, apex - x0, x1 - apex));
    org.style.transform = `scale(${k})`;
    org.style.left = fw / 2 - apex * k + "px";
    org.style.top = (fh - (y1 - y0) * k) / 2 - y0 * k + "px";
  }
}

/** The base body from the desktop's CharacterPage background (Prguse 340 male / 341 female, at (8, 90) in the dialog,
 *  the origin the StateItem offsets use): the figure's rectangle of that page, with the dark page texture and the grey
 *  slot frames keyed out (near black or unsaturated pixels). One per gender for the page's life (a failed load retries).
 *  Shared with the inspect window (one cache, one copy of the crop / key-out constants). */
const bodies = new Map<boolean, Promise<LibPicture | null>>();
export function baseBody(female: boolean): Promise<LibPicture | null> {
  let p = bodies.get(female);
  if (!p) {
    p = makeBaseBody(female).then((r) => { if (!r) bodies.delete(female); return r; });
    bodies.set(female, p);
  }
  return p;
}
async function makeBaseBody(female: boolean): Promise<LibPicture | null> {
  const page = await libPicture(Lib.Prguse, 340 + (female ? 1 : 0));
  if (!page) return null;
  const img = new Image();
  img.src = page.url;
  await img.decode().catch(() => {});
  const cx = 78, cy = 52, cw = 96, ch = 184; // the figure's box in the page, clear of the frame art and the slot frames
  const c = document.createElement("canvas");
  c.width = cw; c.height = ch;
  const g = c.getContext("2d");
  if (!g) return null;
  g.drawImage(img, -cx, -cy);
  const d = g.getImageData(0, 0, cw, ch);
  const px = d.data;
  for (let i = 0; i < px.length; i += 4) {
    const r = px[i]!, gr = px[i + 1]!, b = px[i + 2]!;
    const hi = Math.max(r, gr, b), lo = Math.min(r, gr, b);
    if (hi < 48 || hi - lo < 14) px[i + 3] = 0;
  }
  g.putImageData(d, 0, 0);
  const blob = await new Promise<Blob | null>((res) => c.toBlob(res));
  if (!blob) return null;
  return { url: URL.createObjectURL(blob), width: cw, height: ch, x: 8 + cx, y: 90 + cy };
}

/** topPointX by picture URL (Performance.md T4-4: decode + getImageData once per picture, not per draw). */
const apexes = new Map<string, Promise<number>>();
function topPointX(p: LibPicture): Promise<number> {
  let a = apexes.get(p.url);
  if (!a) { a = measureTopPointX(p); apexes.set(p.url, a); }
  return a;
}
/** The x (in doll coordinates) of a picture's top point: the middle of its topmost row of opaque pixels. */
async function measureTopPointX(p: LibPicture): Promise<number> {
  const img = new Image();
  img.src = p.url;
  await img.decode().catch(() => {});
  const c = document.createElement("canvas");
  c.width = p.width; c.height = p.height;
  const g = c.getContext("2d");
  if (!g) return p.x + p.width / 2;
  g.drawImage(img, 0, 0);
  const d = g.getImageData(0, 0, p.width, p.height).data;
  for (let y = 0; y < p.height; y++) {
    let sum = 0, n = 0;
    for (let x = 0; x < p.width; x++) if (d[(y * p.width + x) * 4 + 3]! > 0) { sum += x; n++; }
    if (n) return p.x + sum / n + 0.5;
  }
  return p.x + p.width / 2;
}

// — The window —

export class CharacterWindow extends UiWindow {
  private readonly nameEl = h("b");
  private readonly lineEl = h("span", "cls");
  private readonly guildEl = h("span");
  private readonly rankEl = h("em");
  private readonly doll = new Paperdoll();
  private readonly cells = new Map<number, ItemCell>();
  private readonly slotNames = new Map<number, string>();
  /** The empty-slot hint last written per slot (title only while empty; the item tooltip covers a filled slot). */
  private readonly hints = new Map<number, string>();
  private readonly tab: HTMLElement;
  private readonly statVals: HTMLElement[] = [];
  private statsOpen = false;
  private lastCls = -1;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "cwin", 40, 70);
    injectCss("win-character", CSS);
    const m = ui.ctx.models;

    const cell = (d: SlotDef): HTMLElement => {
      const c = ui.cells.cell("eq", d[0], { size: 40, empty: svg(d[2]) });
      this.cells.set(d[0], c);
      this.slotNames.set(d[0], d[1]);
      return c.el;
    };
    const col = (side: string, defs: readonly SlotDef[]) => h("div", "col " + side, ...defs.map(cell));
    const body = h("div", "body", col("l", LEFT), this.doll.el, col("r", RIGHT));
    const bottom = h("div", "bottom", ...BOTTOM.map(cell));

    this.tab = asButton(h("div", "tab nodrag", svg("chevronRight")), "Stats");
    this.tab.title = "Stats";
    this.listen(this.tab, "click", () => this.setStatsOpen(!this.statsOpen));

    const list = h("div", "list");
    for (const row of STAT_ROWS) {
      if (!row) { list.append(h("div", "gap")); continue; }
      const val = h("b");
      this.statVals.push(val);
      list.append(h("div", "r", h("span", "", row[0]), val));
    }
    const stats = h("div", "stats", this.titleBar("STATS", null), list);

    const who = h("div", "who", this.nameEl, this.lineEl, this.guildEl, this.rankEl);
    this.el.append(h("div", "panel", this.titleBar("CHARACTER", "Close"), who, body, bottom, this.tab), stats);
    this.el.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: this.signal });

    // Model events mark the window stale (rendered next frame while open, else on the next open).
    const off = [
      m.inventory.subscribe(() => this.invalidate()),
      m.guild.changed.on(() => this.invalidate()),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
    ];
    this.signal.addEventListener("abort", () => { for (const f of off) f(); }, { once: true });
    // PlayerState has no events: level / HP / MP / stats / look are re-read while shown (every write is diffed).
    this.everyShown(250, () => this.invalidate());
  }

  protected override shown(): void { this.doll.reset(); this.invalidate(); }

  protected override place(): void {
    super.place();
    this.updateFlip();
  }

  private setStatsOpen(open: boolean): void {
    this.statsOpen = open;
    this.el.classList.toggle("open", open);
    this.tab.replaceChildren(svg(open ? "chevronLeft" : "chevronRight"));
    this.updateFlip();
    if (open) this.renderStats();
  }

  /** Opens the Stats panel on the left when the right side has no room for it (and the left has). */
  private updateFlip(): void {
    if (!this.statsOpen || this.x === null) return;
    const W = this.wm.layers.layout.width, w = this.el.offsetWidth;
    this.el.classList.toggle("flip", this.x + w + STATS_W > W && this.x >= STATS_W);
  }

  protected override render(): void {
    const ctx = this.ui.ctx, p = ctx.player, u = p.user, m = ctx.models;
    const cls = u?.look.class ?? MirClass.Warrior;
    setText(this.nameEl, u?.name ?? "");
    setText(this.lineEl, `Level ${p.level} ${className(cls)}`);
    if (cls !== this.lastCls) { this.lastCls = cls; this.lineEl.style.setProperty("--cc", classVar(cls)); }
    const guild = m.guild.name || u?.guildName || "";
    const rank = guild ? m.guild.rankName : "";
    setText(this.guildEl, guild);
    this.guildEl.hidden = !guild;
    setText(this.rankEl, rank);
    this.rankEl.hidden = !rank;

    for (const [slot, c] of this.cells) {
      const hint = m.inventory.equipment[slot] ? "" : `${this.slotNames.get(slot) ?? ""} (empty)`;
      if (this.hints.get(slot) !== hint) { this.hints.set(slot, hint); if (hint) c.el.title = hint; else c.el.removeAttribute("title"); }
    }

    this.doll.set(dollLook({
      equipment: m.inventory.equipment, infos: m.itemInfos, level: p.level, cls,
      gender: u?.look.gender ?? p.gender, hair: u?.look.hair ?? 0, wingEffect: p.stats?.wingEffect ?? 0,
    }));
    if (this.statsOpen) this.renderStats();
  }

  private renderStats(): void {
    const p = this.ui.ctx.player, st = p.stats?.stats ?? {};
    const v: StatSource = {
      s: (k) => st[k] ?? 0, hp: p.hp, mp: p.mp, w: p.stats,
      expPct: p.maxExperience > 0n ? (Number(p.experience) * 100) / Number(p.maxExperience) : 0,
    };
    let i = 0;
    for (const row of STAT_ROWS) if (row) setText(this.statVals[i++]!, row[1](v));
  }
}

registerWindow("character", (host) => new CharacterWindow(host));
