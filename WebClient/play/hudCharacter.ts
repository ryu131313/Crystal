// Character window mock for the scene lab's UI tester (play/hudMock.ts owns it and puts it in the HUD layer, so it takes
// the same theme variables: --pa / --pb panels, --fr frame, --ac accent, --tx text, --rad corners, --gl glow).
// Layout from the user's reference (2026-10-15): title bar, name / guild / rank, a paperdoll between two columns of five
// equipment slots, four slots under it, and an arrow tab on the right edge that slides a Stats panel out (and back).
// The paperdoll is built like the desktop's CharacterPage.AfterDraw (characterWindow.ts drawDoll): armour, weapon and
// helmet pictures from StateItem.Lib by the items' Image, placed by their own offsets. Mock data only (items and Image
// numbers from a copy of the live Server.MirDB, 2026-10-15); nothing is wired to a game.

import { EquipmentSlot, MirClass } from "../src/protocol/enums.generated.ts";
import { Lib, libPicture, type LibPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { info, type Stack } from "./hudItems.ts";
import { bestCell, bindCell, host, registerHost, type DragHost } from "./hudDrag.ts";

/** [slot, slot name, item name, ItemInfo.Image (Items.Lib icon; StateItem.Lib paperdoll for armour / weapon / helmet)];
 *  an empty slot has name "" and Image -1. Changed in place by drag and drop. */
type Equip = [number, string, string, number];
const E = EquipmentSlot;
const LEFT: Equip[] = [
  [E.Weapon, "Weapon", "DragonSlayer", 57], [E.Armour, "Armour", "TitanArmour(M)", 592], [E.Torch, "Torch", "DragonTorch", 132],
  [E.BraceletL, "Bracelet", "WhiteGoldBracelet", 766], [E.RingL, "Ring", "WhiteGoldRing", 773],
];
const RIGHT: Equip[] = [
  [E.Helmet, "Helmet", "BrassHelmet", 107], [E.Necklace, "Necklace", "WhiteGoldNecklace", 781], [E.Amulet, "Amulet", "AmuletOfRevival", 277],
  [E.BraceletR, "Bracelet", "WhiteGoldBracelet", 766], [E.RingR, "Ring", "WhiteGoldRing", 773],
];
const BOTTOM: Equip[] = [
  [E.Belt, "Belt", "WhiteGoldBelt", 761], [E.Boots, "Boots", "WhiteGoldShoes", 752], [E.Stone, "Stone", "PowerStone(M)", 576], [E.Mount, "Mount", "WhiteTiger", 3757],
];
const WORN: Equip[] = [...LEFT, ...RIGHT, ...BOTTOM];
/** What the character wears (item names, "" = empty, in WORN order), for the item tooltip's comparison (hudTooltip.ts)
 *  and the inspect window; kept current by drag and drop. */
export const EQUIPPED_NAMES: string[] = WORN.map((e) => e[2]);
/** What the character wears now, with each slot's name (the merchant's Repair tab). */
export const wornGear = (): { slot: string; name: string; img: number }[] => WORN.filter((e) => e[2]).map((e) => ({ slot: e[1], name: e[2], img: e[3] }));
/** Stack size per worn slot (WORN order): amulets keep their count in the amulet / bracelet slot, like the desktop. */
const WORN_COUNTS: number[] = WORN.map((e) => e[2] ? 1 : 0);
/** MirItemCell.CorrectSlot: the item types each slot takes (the right bracelet slot also takes amulets). */
const SLOT_TYPES: Record<number, string[]> = {
  [E.Weapon]: ["Weapon"], [E.Armour]: ["Armour"], [E.Helmet]: ["Helmet"], [E.Torch]: ["Torch"], [E.Necklace]: ["Necklace"], [E.BraceletL]: ["Bracelet"],
  [E.BraceletR]: ["Bracelet", "Amulet"], [E.RingL]: ["Ring"], [E.RingR]: ["Ring"], [E.Amulet]: ["Amulet"], [E.Boots]: ["Boots"], [E.Belt]: ["Belt"],
  [E.Stone]: ["Stone"], [E.Mount]: ["Mount"],
};
const CLASSES = ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"];
/** Why the character can't wear `s` in WORN[i] (CorrectSlot + CanWearItem: level, class, gender), or "". */
function wearRule(i: number, s: Stack, level: number, cls: number): string {
  const [slot, slotName] = WORN[i]!;
  const it = info(s.name);
  if (!it) return "Unknown item";
  const types = SLOT_TYPES[slot] ?? [];
  if (!types.includes(it.type)) return `${slotName} slot: ${types.map((t) => t.toLowerCase() + "s").join(" or ")} only`;
  if (it.reqType === "Level" && it.req > level) return `Needs level ${it.req}`;
  const c = CLASSES[cls] ?? "Warrior";
  if (it.reqClass !== "None" && !it.reqClass.includes(c) && !(it.reqClass === "WarWizTao" && cls <= 2)) return `${it.reqClass} only`;
  if (it.reqGender === "Female") return "Female characters only";
  return "";
}
/** The Stats panel: Crystal's stat names (CharacterDialog STATS I / II), mock values; null = a section break. */
export const STATS: ([string, string] | null)[] = [
  ["AC", "15-41"], ["MAC", "12-24"], ["DC", "22-107"], ["MC", "0-8"], ["SC", "0-8"],
  null,
  ["Accuracy", "+17"], ["Agility", "+18"], ["Attack Speed", "+15"], ["Luck", "+2"], ["Critical Rate", "1%"], ["Critical Damage", "+0"],
  null,
  ["Magic Resist", "+2"], ["Poison Resist", "+1"], ["Health Recovery", "+3"], ["Mana Recovery", "+1"], ["Poison Recovery", "0"],
  ["Holy", "0"], ["Freeze", "0"], ["Poison Attack", "0"],
  null,
  ["Bag Weight", "142/300"], ["Wear Weight", "20/157"], ["Hand Weight", "74/226"],
  null,
  ["EXP Rate", "+100%"], ["Drop Rate", "+50%"], ["Gold Rate", "+50%"],
];
/** Name / guild / rank (the user's picks, 2026-10-15); the level and class line follows the HUD mock (setProfile). */
const PROFILE = { name: "GMRyu", guild: "GameMaster", rank: "Leader" };
const CLASS_NAME: Record<number, string> = { [MirClass.Warrior]: "Warrior", [MirClass.Wizard]: "Wizard", [MirClass.Taoist]: "Taoist" };

const CSS = `
.hudm .cwin { --winh: 476px; width: 360px; }
.hudm .cwin .panel { height: var(--winh); }
.hudm .cwin .stats .title { border-radius: 0 calc(var(--rad) - 1px) 0 0; }
.hudm .cwin.open .panel .title { border-top-right-radius: 0; }
.hudm .cwin .who { text-align: center; padding: 8px 0 4px; line-height: 1.25; }
.hudm .cwin .who b { display: block; font-size: 16px; color: #fff; }
.hudm .cwin .who span { display: block; font-size: 11px; color: var(--dim); }
.hudm .cwin .who em { display: block; font-style: normal; font-size: 12px; font-weight: 600; color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .cwin .body { position: relative; margin: 6px 22px 0; height: 300px; }
.hudm .cwin .col { position: absolute; top: 0; bottom: 0; display: flex; flex-direction: column; justify-content: space-between; }
.hudm .cwin .col.l { left: 0; } .hudm .cwin .col.r { right: 0; }
.hudm .cwin .figure { position: absolute; left: 52px; right: 52px; top: 0; bottom: 0; overflow: hidden; border-radius: var(--rad);
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%),
    radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .cwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
.hudm .cwin .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .cwin .bottom { display: flex; justify-content: center; gap: 6px; margin: 10px 0 0; }
/* The arrow tab on the right edge: opens / closes the Stats panel. */
.hudm .cwin .tab { position: absolute; right: 3px; top: 120px; width: 14px; height: 44px; display: flex; align-items: center; justify-content: center;
  cursor: pointer; font-size: 14px; font-weight: 700; color: var(--ac); border: 1px solid var(--fr);
  border-radius: calc(var(--rad) * .8); background: linear-gradient(180deg, var(--pa), var(--pb)); }
.hudm .cwin .tab:hover { border-color: var(--ac); box-shadow: 0 0 var(--glr) var(--glow); color: #fff; }
.hudm .cwin .stats { position: absolute; left: 100%; top: 0; width: 270px; height: var(--winh); display: flex; flex-direction: column;
  border-radius: 0 var(--rad) var(--rad) 0; background: var(--page); border: 1px solid var(--fr); border-left: 0;
  box-shadow: 1px 0 0 #000, 0 1px 0 #000, 0 -1px 0 #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 10px 10px 30px rgba(0,0,0,.6);
  opacity: 0; transform: translateX(-30px); visibility: hidden; transition: opacity .18s ease-out, transform .18s ease-out, visibility 0s .18s; }
.hudm .cwin.open .stats { opacity: 1; transform: none; visibility: visible; transition: opacity .18s ease-out, transform .18s ease-out; }
.hudm .cwin.open .panel { border-top-right-radius: 0; border-bottom-right-radius: 0; }
.hudm .cwin .stats .list { flex: 1; overflow-y: auto; padding: 6px 10px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cwin .stats .r { display: flex; justify-content: space-between; height: 21px; align-items: center; padding: 0 8px; font-size: 12px; border-radius: calc(var(--rad) * .5); }
.hudm .cwin .stats .r:nth-child(odd) { background: color-mix(in srgb, var(--frl) 7%, transparent); }
.hudm .cwin .stats .r span { color: var(--dim); }
.hudm .cwin .stats .r b { font-weight: 600; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .cwin .stats .gap { height: 8px; }
`;

export class CharacterMock extends HudWindow {
  private figure!: HTMLElement;
  private line!: HTMLElement;
  private org!: HTMLElement;
  private cells: HTMLElement[] = [];
  private level = 1;
  private cls = 0;

  constructor() {
    super("cwin", 40, 70);
    injectCss("hudm-char-css", CSS);
    this.build();
    const me: DragHost = registerHost({
      id: "equip", size: WORN.length, live: () => this.open,
      get: (i) => { const e = WORN[i]!; return e[2] ? { name: e[2], img: e[3], count: Math.max(1, WORN_COUNTS[i] ?? 1) } : null; },
      set: (i, st) => this.wear(i, st),
      accepts: (i, st) => wearRule(i, st, this.level, this.cls),
      verb: () => "Equip",
      quick: (i) => { const st = WORN[i]![2] ? me.get(i) : null; return st ? bestCell(host("bag"), st, me) : null; },
    });
  }

  protected override shown(): void { void this.drawDoll(); }

  /** "Level N Class" from the HUD mock. */
  setProfile(level: number, cls: number): void {
    this.line.textContent = `Level ${level} ${CLASS_NAME[cls] ?? ""}`;
    this.level = level; this.cls = cls;
  }

  private build(): void {
    const title = this.titleBar("CHARACTER", "Close (C)");
    this.line = h("span");
    const who = h("div", "who", h("b", "", PROFILE.name), this.line, h("span", "", PROFILE.guild), h("em", "", PROFILE.rank));

    this.cells = WORN.map((_, i) => this.cell(i));
    const col = (side: string, items: Equip[]) => { const c = h("div", "col " + side); for (const it of items) c.append(this.cells[WORN.indexOf(it)]!); return c; };
    this.org = h("div", "org");
    this.figure = h("div", "figure", this.org);
    this.figure.title = "Armour: " + LEFT[1]![2];
    const body = h("div", "body", col("l", LEFT), this.figure, col("r", RIGHT));
    const bottom = h("div", "bottom");
    for (const it of BOTTOM) bottom.append(this.cells[WORN.indexOf(it)]!);

    const tab = h("div", "tab", "›");
    tab.title = "Stats";
    tab.addEventListener("click", () => {
      const open = this.el.classList.toggle("open");
      tab.textContent = open ? "‹" : "›";
    });

    const list = h("div", "list");
    for (const row of STATS) list.append(row ? h("div", "r", h("span", "", row[0]), h("b", "", row[1])) : h("div", "gap"));
    const stats = h("div", "stats", this.titleBar("STATS", null), list);

    this.el.append(h("div", "panel", title, who, body, bottom, tab), stats);
  }

  /** An equipment cell, a drag and drop target (hudDrag.ts host "equip"). */
  private cell(i: number): HTMLElement {
    const [, slotName, item, image] = WORN[i]!;
    const n = WORN_COUNTS[i] ?? 1;
    const c = item ? itemCell(this.el, `${slotName}: ${item}${n > 1 ? ` (${n})` : ""}`, image, n) : itemCell(this.el, "", -1);
    if (!item) c.title = `${slotName} (empty)`;
    bindCell(c, "equip", i);
    return c;
  }

  /** Puts an item on (or takes it off): the slot, the names the tooltip compares with, and the doll when the armour,
   *  weapon or helmet changed. */
  private wear(i: number, s: Stack | null): void {
    const e = WORN[i]!;
    e[2] = s?.name ?? ""; e[3] = s ? s.img : -1;
    WORN_COUNTS[i] = s ? s.count : 0;
    EQUIPPED_NAMES[i] = e[2];
    const c = this.cell(i);
    this.cells[i]!.replaceWith(c);
    this.cells[i] = c;
    c.classList.add("sel");
    window.setTimeout(() => c.classList.remove("sel"), 400);
    if (e[0] === E.Armour || e[0] === E.Weapon || e[0] === E.Helmet) {
      this.figure.title = "Armour: " + (LEFT[1]![2] || "none");
      const org = h("div", "org"); // a fresh layer, so a doll still loading for the old gear lands in the detached one
      this.org.replaceWith(org); this.org = org;
      this.dollDrawn = false;
      if (this.open) void this.drawDoll();
    }
  }

  /** The doll (shared with the hero window: dollInto). */
  private dollDrawn = false;
  private async drawDoll(): Promise<void> {
    if (this.dollDrawn) return;
    this.dollDrawn = true;
    const org = this.org;
    const done = await dollInto(this.figure, org, [LEFT[1]![3], LEFT[0]![3], RIGHT[0]![3]], false);
    if (org !== this.org || !org.isConnected) return;
    if (!done) this.dollDrawn = false;
  }
}

/** The base body from the desktop's CharacterPage background (Prguse 340 male / 341 female, at (8, 90) in the dialog,
 *  the origin the StateItem offsets use): the figure's rectangle of that page, with the dark page texture and the grey
 *  slot frames keyed out (near black or unsaturated pixels). */
const bodies = new Map<boolean, Promise<LibPicture | null>>();
/** baseBody, keyed once per gender (each call made a new blob URL that was never revoked). */
function baseBody(female: boolean): Promise<LibPicture | null> {
  let p = bodies.get(female);
  if (!p) {
    p = makeBaseBody(female).then((r) => { if (!r) bodies.delete(female); return r; }); // a failed load is tried again next time
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
  const cx = 78, cy = 52, cw = 96, ch = 184; // the figure's box in the page, clear of the frame art above it and the slot frames below (the armour ends at page y 231)
  const c = document.createElement("canvas");
  c.width = cw; c.height = ch;
  const g = c.getContext("2d")!;
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

/** CharacterPage.AfterDraw: the base body, then armour, weapon, helmet from StateItem by Image, each at its own offset
 *  from one origin. The doll is placed so the helmet's top point sits on the window's centre line (the figure area is
 *  centred in the window), scaled as large as fits on both sides of that line and in the height. */
export async function dollInto(figure: HTMLElement, org: HTMLElement, images: number[], female: boolean): Promise<boolean> {
  // images: armour, weapon, helmet StateItem numbers (desktop draw order); -1 = nothing worn there
  const all = await Promise.all([baseBody(female), ...images.map((i) => i < 0 ? null : libPicture(Lib.StateItems, i))]);
  const pics = all.filter((p): p is LibPicture => !!p);
  if (!pics.length) return false;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const p of pics) {
    const d = h("div");
    Object.assign(d.style, { left: p.x + "px", top: p.y + "px", width: p.width + "px", height: p.height + "px", backgroundImage: `url(${p.url})` });
    org.append(d);
    x0 = Math.min(x0, p.x); y0 = Math.min(y0, p.y); x1 = Math.max(x1, p.x + p.width); y1 = Math.max(y1, p.y + p.height);
  }
  const helmet = all[3] ?? null;
  const apex = helmet ? await topPointX(helmet) : (x0 + x1) / 2;
  const fw = figure.clientWidth, fh = figure.clientHeight;
  const k = Math.min((fh - 8) / (y1 - y0), (fw / 2 - 4) / Math.max(apex - x0, x1 - apex));
  org.style.transform = `scale(${k})`;
  org.style.left = fw / 2 - apex * k + "px";
  org.style.top = (fh - (y1 - y0) * k) / 2 - y0 * k + "px";
  return true;
}

/** The x (in doll coordinates) of a picture's top point: the middle of its topmost row of opaque pixels. */
async function topPointX(p: LibPicture): Promise<number> {
  const img = new Image();
  img.src = p.url;
  await img.decode().catch(() => {});
  const c = document.createElement("canvas");
  c.width = p.width; c.height = p.height;
  const g = c.getContext("2d")!;
  g.drawImage(img, 0, 0);
  const d = g.getImageData(0, 0, p.width, p.height).data;
  for (let y = 0; y < p.height; y++) {
    let sum = 0, n = 0;
    for (let x = 0; x < p.width; x++) if (d[(y * p.width + x) * 4 + 3]! > 0) { sum += x; n++; }
    if (n) return p.x + sum / n + 0.5;
  }
  return p.x + p.width / 2;
}
