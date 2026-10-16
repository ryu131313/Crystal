// Ported from play/hudAwaken.ts: the new UI's awakening window over the real Awakening model (src/game/awakening.ts).
// Desktop counterpart: NPCAwakeDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCAwakening /
// AwakeningNeedMaterials / AwakeningLockedItem / Awakening; the classic port is src/ui/npcServices.ts (its rules are kept).
//
// Server rules (PlayerObject.Awakening / AwakeningNeedMaterials / HasAwakeningNeedMaterials, Shared Awake): the item and
// its materials are searched in the BAG only (worn gear can't be awakened); materials are the item's grade (the glyph of
// the chosen type, Shape = type - 1, and an awakening soul, Shape 100) in the counts the server sends; the first
// awakening fixes the type. A failure deletes the item. Changes from the mock:
//  - opens only when the server opens it (S.NPCAwakening while the NPC page is the awakening page: Awakening.shown) and
//    closes with the NPC session (NpcSession.closed, a new NPC page = GameScene.NPCResponse) and on every S.Awakening
//    answer (GameScene.Awakening hides the dialog); there is no menu entry or hotkey;
//  - closing the window (any path) calls Awakening.hide(): every cell's item is unlocked through the server
//    (C.AwakeningLockedItem Locked false, echoed back);
//  - the gear list is the bag's weapons / helmets / armour with a grade (the reasons are the server's refusals); a click
//    puts the item in the base cell (Awakening.put 0: a local lock), the type chips ask the server for the materials
//    (C.AwakeningNeedMaterials), the material counts and the price come from the server (S.AwakeningNeedMaterials,
//    UserItem.AwakeningPrice), the odds from S.ServerRules (hidden until it arrives);
//  - the material cells (3-4, drag a bag stack in, right-click takes it out) are filled from the bag before the
//    hold-to-confirm Awaken sends C.Awakening, as NPCAwakeDialog.CheckNeedMaterials requires; the window then waits for
//    the answer (nothing is simulated: the hit orbs are the world's S.ObjectEffect, the result a popup);
//  - HIDDEN: the five hit orbs and the roll animation (the server plays them as effects on your character), the log,
//    the extra cells 5-6 (Crystal's C.Awakening ignores them) and Downgrade (C.DowngradeAwakening only works on the
//    NPC's [@DOWNGRADE] page: that NPC opens the drop panel in the npcBox window; this window only names its price).
// Narrow layout (a layer below 1040 px): narrower columns and a smaller circle.

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { AwakeType, BindMode, ItemGrade, ItemType } from "../../../protocol/enums.generated.ts";
import type { Awakening } from "../../../game/awakening.ts";
import { awakeLevel, downgradePrice } from "../../../game/items.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { serverRules, type ServerRules } from "../../../game/serverRules.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import { UiWindow, fmt, gradeName, h, holdButton, injectCss, later, setText, type Btn } from "../core/index.ts";
import type { CellRef, CustomTarget } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const AWAKEN_WINDOW = "awaken";

/** The awakening cells as a custom drop grid: 0 = base item, 3 / 4 = materials (1-2 are the server's list, 5-6 hidden). */
const GRID = "x:awaken" as const;
const BASE = 0, MAT_CELLS = [3, 4] as const;
/** How long the window waits for S.Awakening before it lets the player try again (no answer = the NPC page moved on). */
const ANSWER_TIMEOUT = 10_000;

/** The stat each awakening type adds (the chips' labels; the model's texts are the glyph names). */
const TYPE_LABEL: Readonly<Record<number, string>> = {
  [AwakeType.DC]: "DC", [AwakeType.MC]: "MC", [AwakeType.SC]: "SC", [AwakeType.AC]: "AC", [AwakeType.MAC]: "MAC", [AwakeType.HPMP]: "HP / MP",
};
/** NPCAwakeDialog.ItemCell_Click's type list for a not-yet-awakened item (the model's order: index i + 1). */
const typesFor = (type: number): number[] => type === ItemType.Weapon ? [AwakeType.DC, AwakeType.MC, AwakeType.SC]
  : type === ItemType.Helmet ? [AwakeType.AC, AwakeType.MAC] : [AwakeType.HPMP];
const isGear = (info: ItemInfo | undefined): info is ItemInfo =>
  !!info && (info.Type === ItemType.Weapon || info.Type === ItemType.Helmet || info.Type === ItemType.Armour);
const gradeColour = (grade: number): string => `var(--grade-${(gradeName(grade) || "none").toLowerCase()})`;
const itemName = (info: ItemInfo | undefined): string => info ? itemFriendlyName(info) || info.Name : "Unknown item";

/** The server's refusals for an item as the base (PlayerObject.Awakening / Awake.CheckAwakening), "" = it may try. */
function blocked(item: UserItem, info: ItemInfo | undefined, rules: ServerRules | null): string {
  if (!isGear(info)) return "Only weapons, helmets and armour";
  if (info.Grade === ItemGrade.None) return "Items without a grade can't be awakened";
  if (info.Bind & BindMode.DontUpgrade) return "This item can't be upgraded";
  if (item.Rental && (item.Rental.BindingFlags & BindMode.DontUpgrade)) return "A rented item can't be upgraded";
  if (!info.CanAwakening) return "This item can't be awakened";
  if (rules && awakeLevel(item) >= rules.MaxAwakeLevel) return "Fully awakened";
  return "";
}

/** GameScene.Awakening result texts for the popups (the model emits -4..-1 itself). */
const T = {
  Title: "Awakening",
  Success: (name: string, level: number, gain: string) => `${name} reached awakening level ${level}${gain}.`,
  Destroyed: (name: string) => `The awakening failed. ${name} was destroyed.`,
};

// — The session (one per UI runtime): the last try, for the result popup after the window has closed —

class AwakenSession {
  /** The base item of the C.Awakening in flight (name + level before), null when none. */
  pending: { id: bigint; name: string; level: number; type: number } | null = null;

  constructor(private readonly host: ModernUiHost) {}

  /** S.Awakening after the model (which hid the dialog): success / destroyed popups (failures: Awakening.result). */
  handle(p: ServerPacket): void {
    if (p.type !== "Awakening") return;
    const tried = this.pending;
    this.pending = null;
    if (!tried) return;
    const popups = this.host.rt.popups;
    if (p.result === 1) {
      const inv = this.host.ctx.models.inventory;
      const item = inv.inventory.find((i) => i?.UniqueID === tried.id) ?? null;
      const level = item ? awakeLevel(item) : tried.level + 1;
      const gain = item && item.AwakeLevels.length > tried.level ? `: +${item.AwakeLevels[item.AwakeLevels.length - 1]} ${TYPE_LABEL[item.AwakeType] ?? ""}` : "";
      const info = item ? inv.info(item) : undefined;
      void popups.confirm({ title: T.Title, text: T.Success(tried.name, level, gain), kind: "info", buttons: "ok", ...(info ? { item: { name: tried.name, img: info.Image } } : {}) });
    } else if (p.result === 0) {
      void popups.confirm({ title: T.Title, text: T.Destroyed(tried.name), kind: "danger", buttons: "ok" });
    }
  }
}

function awakenSession(host: ModernUiHost): AwakenSession {
  return host.rt.service("awakenSession", () => new AwakenSession(host));
}

// — The window —

const CSS = `
.hudm .awwin { width: 980px; }
.hudm .awwin .panel { height: 580px; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.hudm .awwin .body { flex: 1; min-height: 0; display: flex; }
.hudm .awwin .list { width: 230px; flex: none; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 5px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .awwin .lbl { display: flex; justify-content: space-between; align-items: center; }
.hudm .awwin .lbl b { letter-spacing: 0; text-transform: none; color: var(--tx); font-weight: 600; }
.hudm .awwin .agi { display: flex; gap: 8px; align-items: center; padding: 6px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .awwin .agi:hover { border-color: color-mix(in srgb, var(--ac) 55%, transparent); } .hudm .awwin .agi.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .awwin .agi.no { opacity: .45; cursor: not-allowed; }
.hudm .awwin .agi .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .awwin .agi .t b { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .hudm .awwin .agi .t small { font-size: 10px; color: var(--dim); }
.hudm .awwin .stars { color: var(--gold); letter-spacing: 1px; font-size: 10px; }
.hudm .awwin .empty { font-size: 11px; color: var(--dim); padding: 8px 2px; line-height: 1.4; }
.hudm .awwin .stage { flex: 1; min-width: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; position: relative; padding: 0 10px;
  background: radial-gradient(circle at 50% 45%, color-mix(in srgb, var(--ac) 16%, transparent), transparent 55%); }
.hudm .awwin .circle { position: relative; width: 250px; height: 250px; flex: none; }
.hudm .awwin .circle svg.r { position: absolute; inset: 0; width: 100%; height: 100%; }
.hudm .awwin .circle .r1 { animation: hudm-awspin 30s linear infinite; } .hudm .awwin .circle .r2 { animation: hudm-awspin 18s linear infinite reverse; }
.hudm .awwin .circle.run .r1 { animation-duration: 3s; } .hudm .awwin .circle.run .r2 { animation-duration: 2s; }
@keyframes hudm-awspin { to { transform: rotate(360deg); } }
.hudm .awwin .circle circle, .hudm .awwin .circle path { fill: none; stroke: var(--ac); }
.hudm .awwin .acore { position: absolute; left: 50%; top: 50%; translate: -50% -50%; }
.hudm .awwin .acore .icell { border-radius: 50%; box-shadow: 0 0 0 1px #000, 0 0 24px var(--glow); }
.hudm .awwin .acore .icell::after { border-radius: 50%; }
.hudm .awwin .pip { position: absolute; width: 22px; height: 22px; margin: -11px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 800;
  border: 1px solid var(--fr); background: #05070c; color: var(--dim); }
.hudm .awwin .pip.on { color: #000; background: var(--gold); border-color: #fff; }
.hudm .awwin .types { display: flex; gap: 6px; flex-wrap: wrap; justify-content: center; min-height: 28px; }
.hudm .awwin .types button { padding: 5px 14px; border-radius: 14px; cursor: pointer; font: inherit; font-size: 12px; font-weight: 700; color: var(--tx); border: 1px solid var(--fr); background: var(--pb); }
.hudm .awwin .types button.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); }
.hudm .awwin .types button.lock { cursor: default; }
.hudm .awwin .types .hint { font-size: 11px; color: var(--dim); align-self: center; }
.hudm .awwin .lv { display: flex; gap: 6px; flex-wrap: wrap; justify-content: center; font-size: 11px; color: var(--dim); text-align: center; }
.hudm .awwin .lv span { padding: 2px 8px; border-radius: 8px; border: 1px solid var(--fr); } .hudm .awwin .lv b { color: var(--gold); }
.hudm .awwin .head { text-align: center; } .hudm .awwin .head b { font-size: 18px; } .hudm .awwin .head small { display: block; font-size: 11px; color: var(--dim); }
.hudm .awwin .down { font-size: 10px; color: var(--dim); text-align: center; max-width: 360px; }
.hudm .awwin .side { width: 320px; flex: none; padding: 12px 14px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); display: flex; flex-direction: column; gap: 10px; overflow-y: auto; }
.hudm .awwin .side > * { flex-shrink: 0; }
.hudm .awwin .mat { display: flex; align-items: center; gap: 8px; padding: 5px 7px; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); font-size: 12px; }
.hudm .awwin .mat .ic { width: 36px; height: 36px; flex: none; border-radius: var(--rad); border: 1px solid var(--fr); background: center / auto no-repeat, radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); image-rendering: pixelated; }
.hudm .awwin .mat .n { flex: 1; min-width: 0; } .hudm .awwin .mat .c { font-weight: 700; } .hudm .awwin .mat .c.ok { color: var(--ok); } .hudm .awwin .mat .c.no { color: var(--bad-soft); }
.hudm .awwin .mat.miss { border-color: color-mix(in srgb, var(--bad) 50%, transparent); }
.hudm .awwin .mcells { display: flex; gap: 8px; align-items: center; font-size: 10px; color: var(--dim); }
.hudm .awwin .kv { display: flex; justify-content: space-between; font-size: 12px; padding: 2px 0; } .hudm .awwin .kv span { color: var(--dim); } .hudm .awwin .kv b { color: #fff; }
.hudm .awwin .kv b.g { color: var(--gold); } .hudm .awwin .kv b.r { color: var(--bad-soft); }
.hudm .awwin .danger { padding: 8px 10px; border-radius: var(--rad); font-size: 11px; line-height: 1.4; color: #ffb3a6; border: 1px solid var(--bad); background: color-mix(in srgb, var(--bad) 12%, transparent); }
.hudm .awwin .danger b { color: var(--bad-soft); }
.hudm .awwin .go .btn { width: 100%; height: 40px; font-size: 14px; font-weight: 800; letter-spacing: .08em; }
.hudm .awwin .why { font-size: 11px; color: var(--bad-soft); text-align: center; min-height: 14px; } .hudm .awwin .why.ok { color: var(--ok); }
.hudm.narrow .awwin { width: 820px; }
.hudm.narrow .awwin .list { width: 186px; }
.hudm.narrow .awwin .side { width: 270px; }
.hudm.narrow .awwin .circle { width: 200px; height: 200px; }
`;

/** The rune circle (constant markup, no data). */
const CIRCLE_SVG = `<svg class="r r1" viewBox="0 0 250 250"><circle cx="125" cy="125" r="118" stroke-width="1" opacity=".5"/><circle cx="125" cy="125" r="108" stroke-width="2" stroke-dasharray="4 10" opacity=".8"/>
<path d="M125 12 L223 182 L27 182 Z" stroke-width="1" opacity=".35"/><path d="M125 238 L27 68 L223 68 Z" stroke-width="1" opacity=".35"/></svg>
<svg class="r r2" viewBox="0 0 250 250"><circle cx="125" cy="125" r="90" stroke-width="1.5" stroke-dasharray="20 6 2 6" opacity=".6"/><circle cx="125" cy="125" r="62" stroke-width="1" opacity=".4"/></svg>`;

interface GearRow { id: bigint; index: number; el: HTMLElement; cell: ItemCell; name: HTMLElement; sub: HTMLElement; key: string }
interface MatRow { el: HTMLElement; icon: HTMLElement; name: HTMLElement; count: HTMLElement; img: number }

class AwakenWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly aw: Awakening;
  /** C.Awakening was sent: everything waits for S.Awakening (which closes the window). */
  private busy = false;
  private rows: GearRow[] = [];
  private typesKey = "";

  // — DOM, built once —
  private readonly listBox = h("div", "list");
  private readonly listCount = h("b");
  private readonly gearBox = h("div");
  private readonly listEmpty = h("div", "empty", "No weapons, helmets or armour with a grade in your bag. Awakening works on bag items only: take the item off first.");
  private readonly headName = h("b");
  private readonly headSub = h("small");
  private readonly circle = h("div", "circle");
  private readonly pips: HTMLElement[] = [];
  private readonly typesBox = h("div", "types");
  private readonly levels = h("div", "lv");
  private readonly downNote = h("div", "down");
  private readonly side = h("div", "side");
  private readonly sideBlocked = h("div", "danger");
  private readonly matLabel = h("b");
  private readonly mats: MatRow[] = [];
  private readonly matCells: ItemCell[] = [];
  private readonly odds = h("div");
  private readonly oddsSuccess = h("b");
  private readonly oddsHit = h("b");
  private readonly oddsGain = h("b");
  private readonly priceEl = h("b");
  private readonly dangerText = h("b");
  private readonly go: Btn;
  private readonly why = h("div", "why");
  private readonly matSection = h("div");

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "awwin", 20, 30);
    injectCss("win-awaken", CSS);
    this.m = ui.ctx.models;
    this.aw = this.m.awakening;
    const sig = this.signal;

    // The awakening cells as a drop grid (bag items only, the model's put rules; right-click takes one back out).
    const target: CustomTarget = {
      item: (i) => this.aw.items[i] ?? null,
      accepts: (from, item, i) => this.accepts(from, item, i),
      verb: (_f, _it, i) => i === BASE ? "Awaken this" : "Add material",
      drop: (from, _item, i) => { if (!this.busy) { this.aw.put(i, from.index); this.invalidate(); } },
      use: (i) => { if (!this.busy) { this.aw.takeBack(i); this.invalidate(); } },
    };
    const offTarget = ui.cells.addTarget(GRID, target);
    sig.addEventListener("abort", offTarget, { once: true });

    // Left: the bag's gear
    this.listBox.append(h("div", "lbl", "Your bag", this.listCount), this.gearBox, this.listEmpty);

    // Middle: name, the circle with the base cell and the level pips, type chips, levels
    const svgs = h("div");
    svgs.innerHTML = CIRCLE_SVG; // constant markup (no data)
    this.circle.append(...Array.from(svgs.children));
    const baseCell = ui.cells.cell(GRID, BASE, { size: 72, signal: sig });
    this.circle.append(h("div", "acore", baseCell.el));
    const stage = h("div", "stage", h("div", "head", this.headName, this.headSub), this.circle, this.typesBox, this.levels, this.downNote);

    // Right: materials, odds, price, the warning and the hold-to-confirm Awaken
    for (let i = 0; i < 2; i++) {
      const icon = h("div", "ic"), name = h("div", "n"), count = h("span", "c");
      const el = h("div", "mat", icon, name, count);
      this.mats.push({ el, icon, name, count, img: -2 });
    }
    for (const i of MAT_CELLS) this.matCells.push(ui.cells.cell(GRID, i, { size: 36, signal: sig }));
    this.odds.append(h("div", "lbl", "Odds"),
      h("div", "kv", h("span", "", "Success"), this.oddsSuccess),
      h("div", "kv", h("span", "", "Each hit orb (5)"), this.oddsHit),
      h("div", "kv", h("span", "", "Gain on success"), this.oddsGain));
    this.go = holdButton("HOLD TO AWAKEN", () => this.awaken(), { cls: "main", danger: true, title: "Hold to awaken: a failure destroys the item" });
    this.matSection.append(
      h("div", "lbl", "Materials", this.matLabel), ...this.mats.map((r) => r.el),
      h("div", "mcells", ...this.matCells.map((c) => c.el), h("span", "", "Material stacks used (filled from your bag on Awaken; right-click to take one out)")),
      this.odds,
      h("div", "kv", h("span", "", "Price"), this.priceEl),
      h("div", "danger", this.dangerText, " A failed awakening deletes the item and uses up the gold and materials."),
      h("div", "go", this.go.el), this.why);
    this.side.append(this.sideBlocked, this.matSection);

    this.el.append(h("div", "panel", this.titleBar("AWAKENING", "Close"), h("div", "body", this.listBox, stage, this.side)));

    // Gear list clicks (delegated): put the item in the base cell
    this.listen(this.gearBox, "click", (e) => {
      const row = this.rows.find((r) => r.el.contains(e.target as Node));
      if (row) this.select(row);
    });

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    const inv = () => this.invalidate();
    on(this.m.inventory.subscribe(inv));
    on(this.aw.types.on(inv));
    on(this.aw.changed.on(inv));
    on(this.aw.shown.on(() => { this.busy = false; this.invalidate(); }));
    on(this.aw.hidden.on(() => { this.busy = false; }));
    on(serverRules(ui.ctx.session).changed.on(inv));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }));
  }

  protected override shown(): void {
    this.busy = false;
    this.ui.openWindow("inventory", true); // NPCAwakeDialog.Show opens the bag too (drag materials from it)
  }

  /** Every close path: hand the cells back (C.AwakeningLockedItem Locked false for each, NPCAwakeDialog.Hide). */
  protected override closed(): void {
    this.aw.hide();
  }

  private info(item: UserItem | null | undefined): ItemInfo | undefined {
    return item ? this.m.itemInfos.get(item.ItemIndex) : undefined;
  }
  private rules(): ServerRules | null { return serverRules(this.ui.ctx.session).rules; }

  /** The drop filter for the awakening cells (the model's put rules + the server's refusals for the base). */
  private accepts(from: CellRef, item: UserItem, i: number): string | null {
    if (from.grid !== "inv") return null;
    const info = this.info(item);
    if (this.busy) return "Waiting for the server";
    if (i === BASE) return isGear(info) ? blocked(item, info, this.rules()) : null;
    if (i === MAT_CELLS[0] || i === MAT_CELLS[1]) {
      if (!info || info.Type !== ItemType.Awakening || info.Shape >= 200) return null;
      const base = this.info(this.aw.items[BASE]);
      if (base && info.Grade !== base.Grade) return "Materials must be the item's grade";
      return this.aw.items[i] ? "Take the material out first" : "";
    }
    return null;
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    this.renderList();
    this.renderStage();
    this.renderSide();
  }

  /** The bag's weapons / helmets / armour (rows rebuilt only when the set of items changed). */
  private renderList(): void {
    const inv = this.m.inventory.inventory, rules = this.rules();
    const found: { item: UserItem; index: number; info: ItemInfo }[] = [];
    for (let i = 0; i < inv.length; i++) {
      const item = inv[i], info = this.info(item);
      if (item && isGear(info)) found.push({ item, index: i, info });
    }
    const key = found.map((f) => `${f.item.UniqueID}@${f.index}`).join(",");
    if (key !== this.rows.map((r) => `${r.id}@${r.index}`).join(",")) {
      for (const r of this.rows) r.cell.dispose();
      this.rows = found.map((f) => {
        const cell = this.ui.cells.cell("inv", f.index, { size: 38, readOnly: true, signal: this.signal });
        const name = h("b"), sub = h("small");
        return { id: f.item.UniqueID, index: f.index, el: h("div", "agi", cell.el, h("div", "t", name, sub)), cell, name, sub, key: "" };
      });
      this.gearBox.replaceChildren(...this.rows.map((r) => r.el));
    }
    this.listEmpty.hidden = found.length > 0;
    setText(this.listCount, String(found.length));
    const selId = this.aw.items[BASE]?.UniqueID;
    found.forEach((f, n) => {
      const r = this.rows[n]!, why = blocked(f.item, f.info, rules);
      const level = awakeLevel(f.item), max = rules?.MaxAwakeLevel ?? 0;
      const locked = this.m.inventory.isLocked(f.item) && selId !== f.item.UniqueID;
      const sub = level ? "★".repeat(level) + (max > level ? "☆".repeat(max - level) : "") : why || `${gradeName(f.info.Grade)} ${typeLabel(f.info.Type)}`;
      const k = `${itemName(f.info)}|${f.info.Grade}|${sub}|${why}|${locked}|${selId === f.item.UniqueID}`;
      if (r.key === k) return;
      r.key = k;
      setText(r.name, itemName(f.info));
      r.name.style.color = gradeColour(f.info.Grade);
      setText(r.sub, sub);
      r.sub.className = level ? "stars" : "";
      r.el.classList.toggle("sel", selId === f.item.UniqueID);
      r.el.classList.toggle("no", (!!why && why !== "Fully awakened") || locked);
      r.el.title = locked ? "Waiting for the server" : why;
    });
  }

  private renderStage(): void {
    const base = this.aw.items[BASE] ?? null, info = this.info(base), rules = this.rules();
    this.circle.classList.toggle("run", this.busy);
    if (!base || !info) {
      setText(this.headName, "Choose an item");
      this.headName.style.color = "";
      setText(this.headSub, "Click a weapon, helmet or armour in the list, or drag it from your bag into the circle.");
    } else {
      setText(this.headName, itemName(info));
      this.headName.style.color = gradeColour(info.Grade);
      const level = awakeLevel(base), total = base.AwakeLevels.reduce((a, b) => a + b, 0);
      setText(this.headSub, `${gradeName(info.Grade)} ${typeLabel(info.Type)} · awakening ${level}${rules ? `/${rules.MaxAwakeLevel}` : ""}`
        + (level && TYPE_LABEL[base.AwakeType] ? ` · ${TYPE_LABEL[base.AwakeType]} +${total}` : ""));
    }
    // Level pips: the server's maximum (ServerRules), else only the levels the item has
    const level = base ? awakeLevel(base) : 0;
    const pipCount = Math.max(level, rules?.MaxAwakeLevel ?? 0);
    if (this.pips.length !== pipCount) {
      for (const p of this.pips) p.remove();
      this.pips.length = 0;
      for (let i = 0; i < pipCount; i++) {
        const a = -Math.PI / 2 + i * 2 * Math.PI / pipCount;
        const p = h("div", "pip", String(i + 1));
        p.style.left = 50 + Math.cos(a) * 41.6 + "%";
        p.style.top = 50 + Math.sin(a) * 41.6 + "%";
        this.pips.push(p);
        this.circle.append(p);
      }
    }
    this.pips.forEach((p, i) => {
      const on = !!base && i < level;
      p.classList.toggle("on", on);
      p.title = on ? `Level ${i + 1}: +${base!.AwakeLevels[i]}` : `Level ${i + 1}`;
    });
    this.renderTypes(base, info);
    if (base && level) this.levels.replaceChildren(...base.AwakeLevels.map((v, i) => h("span", "", `Lv${i + 1} `, h("b", "", `+${v}`))));
    else this.levels.replaceChildren(base ? "Not awakened yet: the first awakening fixes the stat." : "");
    setText(this.downNote, base && level && info ? `Removing the last level is done at an NPC that offers Downgrade (${fmt(downgradePrice(base, info))} gold).` : "");
  }

  /** The type chips (SelectAwakeType): a new item picks one (C.AwakeningNeedMaterials), an awakened one shows its own. */
  private renderTypes(base: UserItem | null, info: ItemInfo | undefined): void {
    const texts = this.aw.typeTexts, sel = this.aw.selected;
    const key = `${base?.UniqueID ?? ""}|${base?.AwakeType ?? ""}|${texts.join("/")}|${sel}|${this.busy}`;
    if (key === this.typesKey) return;
    this.typesKey = key;
    if (!base || !info) { this.typesBox.replaceChildren(h("span", "hint", "The type picker appears with an item.")); return; }
    const chipsEls: HTMLElement[] = [];
    if (awakeLevel(base) === 0) {
      typesFor(info.Type).forEach((type, i) => {
        const idx = i + 1, b = h("button", sel === idx ? "on" : "", TYPE_LABEL[type] ?? "");
        b.type = "button";
        b.title = texts[idx] ?? "";
        b.addEventListener("click", () => { if (!this.busy && this.aw.selected !== idx) this.aw.selectType(idx); });
        chipsEls.push(b);
      });
      if (sel === 0) chipsEls.push(h("span", "hint", "Pick the stat to awaken"));
    } else {
      const b = h("button", "on lock", TYPE_LABEL[base.AwakeType] ?? "");
      b.type = "button";
      b.title = `${texts[0] ?? ""}: this item is awakened in ${TYPE_LABEL[base.AwakeType] ?? "this type"}; the type can't change`;
      chipsEls.push(b);
    }
    this.typesBox.replaceChildren(...chipsEls);
  }

  private renderSide(): void {
    const base = this.aw.items[BASE] ?? null, info = this.info(base), rules = this.rules();
    const why = base ? blocked(base, info, rules) : "Choose an item to awaken.";
    this.sideBlocked.hidden = !why;
    if (why) this.sideBlocked.replaceChildren(h("b", "", why), base ? "." : "");
    this.matSection.hidden = !!why;
    if (why || !base || !info) return;

    // The server's materials (S.AwakeningNeedMaterials) with what the bag holds of each
    const need = this.aw.materials;
    setText(this.matLabel, need[0] || need[1] ? `${gradeName(info.Grade)} grade` : this.aw.selected === 0 && awakeLevel(base) === 0 ? "pick a type" : "asking the server…");
    let haveAll = !!(need[0] || need[1]);
    need.forEach((n, i) => {
      const row = this.mats[i]!;
      row.el.hidden = !n;
      if (!n) return;
      const { total } = this.stacks(n.info);
      const ok = total >= n.count;
      if (!ok) haveAll = false;
      if (row.img !== n.info.Image) {
        row.img = n.info.Image;
        row.icon.style.backgroundImage = "";
        const img = n.info.Image;
        void libPicture(Lib.Items, img).then((p) => { if (p && row.img === img) row.icon.style.backgroundImage = `url(${p.url}), radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%)`; });
      }
      setText(row.name, itemName(n.info));
      row.icon.title = n.info.Name;
      setText(row.count, `${fmt(total)} / ${fmt(n.count)}`);
      row.count.className = "c " + (ok ? "ok" : "no");
      row.el.classList.toggle("miss", !ok);
    });

    // Odds (S.ServerRules; hidden until it arrives)
    this.odds.hidden = !rules;
    if (rules) {
      const success = Math.min(100, rules.AwakeSuccessRate + 1); // Random(0, 100) <= rate
      const maxV = Math.max(1, rules.AwakeChanceMax[info.Grade - 1] ?? 1);
      const rate = info.Type === ItemType.Weapon ? rules.Awake_WeaponRate : info.Type === ItemType.Helmet ? rules.Awake_HelmetRate : rules.Awake_ArmorRate;
      setText(this.oddsSuccess, `${success}%`);
      setText(this.oddsHit, `${Math.min(100, rules.AwakeHitRate)}% · +${(maxV / 5).toFixed(1)}`);
      setText(this.oddsGain, `${1}–${Math.max(1, Math.floor(maxV))} ×${rate}`);
      setText(this.dangerText, `${100 - success}% chance to destroy ${itemName(info)}.`);
    } else setText(this.dangerText, `A failure destroys ${itemName(info)}.`);

    const price = this.aw.gold, gold = this.m.inventory.gold;
    setText(this.priceEl, price === null ? "—" : `${fmt(price)} gold`);
    this.priceEl.className = price !== null && gold < price ? "r" : "g";

    const typeChosen = this.aw.selected !== 0 || awakeLevel(base) > 0;
    let reason = "";
    if (this.busy) reason = "Awakening… waiting for the server.";
    else if (!typeChosen) reason = "Pick the stat to awaken.";
    else if (!(need[0] || need[1])) reason = "Waiting for the material list.";
    else if (!haveAll) reason = "Not enough materials in your bag.";
    else if (price !== null && gold < price) reason = "Not enough gold.";
    else if (!this.fillable()) reason = "Each material must be in one stack: stack them in your bag first.";
    setText(this.why, reason);
    this.why.classList.toggle("ok", this.busy);
    this.go.setDisabled(!!reason, reason);
    this.go.setLabel(this.busy ? "AWAKENING…" : "HOLD TO AWAKEN");
  }

  /** Bag stacks of a material: same item (NPCAwakeDialog compares names) and the base item's grade (the server's rule). */
  private stacks(need: ItemInfo): { total: number; best: number; bestCount: number } {
    const inv = this.m.inventory.inventory;
    let total = 0, best = -1, bestCount = 0;
    for (let i = 0; i < inv.length; i++) {
      const it = inv[i], inf = this.info(it);
      if (!it || !inf || inf.Name !== need.Name || inf.Grade !== need.Grade) continue;
      total += it.Count;
      const free = !this.m.inventory.isLocked(it) || this.aw.items.some((c) => c?.UniqueID === it.UniqueID);
      if (free && it.Count > bestCount) { best = i; bestCount = it.Count; }
    }
    return { total, best, bestCount };
  }

  /** Whether each needed material has one stack big enough (NPCAwakeDialog.CheckNeedMaterials checks single cells). */
  private fillable(): boolean {
    for (const n of this.aw.materials) if (n && this.stacks(n.info).bestCount < n.count) return false;
    return true;
  }

  // — Actions —

  /** A gear row: the item goes into the base cell (Awakening.put 0; the previous base is unlocked through the server). */
  private select(row: GearRow): void {
    if (this.busy) return;
    const item = this.m.inventory.inventory[row.index];
    if (!item || item.UniqueID !== row.id || this.aw.items[BASE]?.UniqueID === item.UniqueID) return;
    const why = blocked(item, this.info(item), this.rules());
    if (why && why !== "Fully awakened") { this.ui.drag.toast(why); return; }
    if (this.m.inventory.isLocked(item)) { this.ui.drag.toast("Waiting for the server"); return; }
    this.aw.put(BASE, row.index);
    this.invalidate();
  }

  /** The hold finished: fill the material cells from the bag (largest stack of each), then C.Awakening (the model checks
   *  NPCAwakeDialog.CheckNeedMaterials). The window waits for S.Awakening, which closes it. */
  private awaken(): void {
    if (this.busy) return;
    const base = this.aw.items[BASE], info = this.info(base);
    if (!base || !info || blocked(base, info, this.rules())) return;
    const need = this.aw.materials.filter((n): n is NonNullable<typeof n> => !!n);
    if (!need.length) return;
    // Material cells: drop what is not needed, then put the best stack of each missing material into a free cell
    for (const slot of MAT_CELLS) {
      const it = this.aw.items[slot], inf = this.info(it);
      if (it && !need.some((n) => inf?.Name === n.info.Name && it.Count >= n.count)) this.aw.takeBack(slot);
    }
    for (const n of need) {
      if (MAT_CELLS.some((s) => this.info(this.aw.items[s])?.Name === n.info.Name)) continue;
      const { best, bestCount } = this.stacks(n.info);
      const slot = MAT_CELLS.find((s) => !this.aw.items[s]);
      if (best < 0 || bestCount < n.count || slot === undefined) { this.invalidate(); return; }
      this.aw.put(slot, best);
    }
    // NPCAwakeDialog.CheckNeedMaterials (the model sends nothing without it): never wait for an answer that won't come
    const ready = need.every((n) => MAT_CELLS.some((s) => { const it = this.aw.items[s]; return !!it && this.info(it)?.Name === n.info.Name && it.Count >= n.count; }));
    if (!ready) { this.invalidate(); return; }
    const typeIdx = this.aw.selected;
    const type = awakeLevel(base) > 0 ? base.AwakeType : typesFor(info.Type)[typeIdx - 1] ?? AwakeType.None;
    if (type === AwakeType.None) return;
    awakenSession(this.ui).pending = { id: base.UniqueID, name: itemName(info), level: awakeLevel(base), type };
    this.busy = true;
    this.aw.awaken();
    this.invalidate();
    // The server answers every C.Awakening on its awakening page; if the page moved on it stays silent: free the window.
    later(() => { if (this.busy && this.open) { this.busy = false; awakenSession(this.ui).pending = null; this.invalidate(); } }, ANSWER_TIMEOUT, this.signal);
  }
}

/** A readable ItemType for the three awakening types. */
function typeLabel(type: number): string {
  return type === ItemType.Weapon ? "weapon" : type === ItemType.Helmet ? "helmet" : type === ItemType.Armour ? "armour" : "item";
}

registerWindow(AWAKEN_WINDOW, (host) => new AwakenWindow(host), {
  binds: [],
  // Server-opened: S.NPCAwakening (Awakening.shown) opens it; Awakening.hidden (every S.Awakening answer, Hide) and the
  // NPC session's end / a new page (NPCDialog.Hide, GameScene.NPCResponse: NPCAwakeDialog.Hide) close it.
  watch: (host, open) => {
    const aw = host.ctx.models.awakening, npc = host.ctx.models.npcSession, session = awakenSession(host);
    const offs = [
      aw.shown.on(() => { open(true); }),
      aw.hidden.on(() => { open(false); }),
      aw.result.on(({ text }) => { session.pending = null; void host.rt.popups.confirm({ title: T.Title, text, kind: "warning", buttons: "ok" }); }),
      npc.closed.on(() => aw.hide()),
      npc.page.on(() => aw.hide()),
      host.packets.on((p) => session.handle(p)),
    ];
    if (aw.open) open(true);
    return () => { for (const off of offs) off(); };
  },
});
