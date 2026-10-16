// Ported from play/hudTooltip.ts (the card look: grade header with icon, sections, durability bar, requirement marks,
// bind chips, story, footer, the Shift compare card, the measured-once mouse follow). The new UI's item tooltip.
//
// Content comes from real data only: src/ui/itemLabel.ts (the faithful GameScene.CreateItemLabel port) stays the rules
// source, and this file renders its itemLabelSections() in the new style, so both UIs show the same lines. Grade colours
// come from the theme tokens (--grade-*), not literal hex. The mock's lab data (items.json), name-hash per-copy values,
// set lists / bonuses and gem-rule table are gone: everything per copy is the UserItem the caller passes (keyed by its
// grid + index / UniqueID at the caller), ItemInfo by ItemIndex through the context.
//
// Hidden (no data / not ported, as in the classic label): set pieces and set bonus text (the client only knows the
// ItemInfos it was sent, and has no copy of HumanObject's set stats), the Hero line (no HeroInfoList), the soulbound
// owner's name (no user-name cache: the chip says "Soulbound" only), and the mock's Ctrl+click chat-link hint (the
// cells own that gesture).
//
// Lifetime: one ItemTooltip per UiRuntime (itemTooltip(rt) uses rt.service), drawn in the 'tips' layer (z 50, above the
// window band 10-39, at HUD scale k). It never listens to the keyboard: the integrator calls setShift() from the input
// router's key state. The layer box, scale and card size are measured once per draw; the mouse follow is coalesced to
// one transform write a frame (Performance.md T2-3). A missing ItemInfo is requested once (GameInfo.requestItem) and the
// card redraws when it arrives.

import type { UserItem } from "../../../protocol/server.ts";
import { ItemGrade, ItemType, Stat } from "../../../protocol/enums.generated.ts";
import {
  itemLabelSections, type ItemLabelContext, type ItemLabelData, type ItemLabelLine, type ItemLabelOptions, type ItemLabelSection,
} from "../../itemLabel.ts";
import { Lib, libPicture, libPictureNow } from "../../mir/controls.ts";
import { h, injectCss, rafCoalesce, restartAnim, setStyle } from "./dom.ts";
import { signed } from "./format.ts";
import { gradeVar } from "./styles.ts";
import type { UiRuntime } from "./runtime.ts";

/** Where missing ItemInfos are asked for (GameInfo: requestItem dedupes; onChange("item", index) on S.NewItemInfo). */
export interface ItemInfoSource {
  requestItem(index: number): void;
  onChange(fn: (kind: string, index: number) => void): () => void;
}

export interface ItemTooltipOptions extends ItemLabelOptions {
  /** The worn item in the slot this item would go to (Inventory.equipment): a change row, and its card while Shift is held. */
  compareWith?: UserItem | null;
  /** A short action hint for the footer ("Right-click to equip"); the caller knows the cell's gestures. */
  hint?: string;
}

/** An element to sit beside, or a pointer position in client coordinates (follows later move() calls). */
export type TooltipAnchor = HTMLElement | { x: number; y: number };

/** Stats compared against the worn item: [stat, label]. */
const COMPARE: readonly [number, string][] = [
  [Stat.MaxDC, "DC"], [Stat.MaxMC, "MC"], [Stat.MaxSC, "SC"], [Stat.MaxAC, "AC"], [Stat.MaxMAC, "MAC"], [Stat.Accuracy, "Accuracy"],
  [Stat.Agility, "Agility"], [Stat.AttackSpeed, "A.Speed"], [Stat.HP, "HP"], [Stat.MP, "MP"], [Stat.Luck, "Luck"],
];

const CSS = `
.hudm .itip { position: absolute; left: 0; top: 0; pointer-events: none; display: flex; gap: 6px; align-items: flex-start; will-change: transform; }
.hudm .itip.in { animation: hudm-tipin .12s ease-out; }
@keyframes hudm-tipin { from { opacity: 0; } }
.hudm .itip .tc { position: relative; width: 304px; border-radius: var(--rad); overflow: hidden; color: var(--tx); font-size: 12px;
  background: linear-gradient(180deg, color-mix(in srgb, var(--pb) 94%, #000), color-mix(in srgb, var(--pb) 70%, #000)); border: 1px solid color-mix(in srgb, var(--gc) 70%, var(--fr));
  box-shadow: 0 0 0 1px #000, 0 12px 30px rgba(0,0,0,.75), 0 0 18px color-mix(in srgb, var(--gc) 25%, transparent); }
.hudm .itip .tc.broken { border-color: var(--bad); }
.hudm .itip .tc.shine::before { content: ""; position: absolute; top: 0; bottom: 0; left: 0; width: 45%; z-index: 0; pointer-events: none;
  background: linear-gradient(115deg, transparent, color-mix(in srgb, var(--gc) 22%, transparent) 50%, transparent); transform: translateX(-100%);
  animation: hudm-tipshine 2.6s linear infinite; will-change: transform; }
@keyframes hudm-tipshine { to { transform: translateX(330%); } }
@media (prefers-reduced-motion: reduce) { .hudm .itip .tc.shine::before { display: none; } }
.hudm .itip .tc > * { position: relative; z-index: 1; }
.hudm .itip .hd { display: flex; gap: 10px; align-items: center; padding: 10px 10px 8px; background: linear-gradient(180deg, color-mix(in srgb, var(--gc) 26%, transparent), transparent); }
.hudm .itip .ic { width: 50px; height: 50px; flex: none; border-radius: calc(var(--rad) * .8); border: 1px solid var(--gc); background: #000 center / auto no-repeat; image-rendering: pixelated;
  box-shadow: 0 0 0 1px #000, inset 0 0 12px color-mix(in srgb, var(--gc) 35%, transparent); }
.hudm .itip .nm { flex: 1; min-width: 0; } .hudm .itip .nm b { display: block; font-size: 15px; color: var(--gc); line-height: 1.15; overflow-wrap: anywhere; }
.hudm .itip .nm small { display: block; margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .itip .tag { position: absolute; left: 10px; top: -1px; font-size: 9px; font-weight: 800; letter-spacing: .12em; padding: 1px 8px; border-radius: 0 0 6px 6px; color: #000; background: var(--dim); }
.hudm .itip .sec { padding: 6px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .itip .ln { padding: 1px 0; white-space: pre-line; overflow-wrap: anywhere; }
.hudm .itip .ln.add { color: var(--ok); } .hudm .itip .ln.grade { color: var(--gc); font-weight: 700; } .hudm .itip .ln.bad { color: var(--bad-soft); }
.hudm .itip .ln.hint { font-size: 11px; color: var(--dim); }
.hudm .itip .ln.rental { color: var(--warn); } .hudm .itip .ln.rentalTime { color: var(--gold); }
.hudm .itip .cmp { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; font-size: 10px; color: var(--dim); }
.hudm .itip .cmp i { font-style: normal; font-weight: 700; padding: 1px 6px; border-radius: 6px; font-size: 10px; }
.hudm .itip .cmp i.up { color: var(--ok); background: color-mix(in srgb, var(--ok) 14%, transparent); }
.hudm .itip .cmp i.dn { color: var(--bad-soft); background: color-mix(in srgb, var(--bad) 14%, transparent); }
.hudm .itip .cmp i.eq { color: var(--ac); background: color-mix(in srgb, var(--ac) 15%, transparent); }
.hudm .itip .sk .ln { display: flex; gap: 6px; align-items: center; }
.hudm .itip .sk u { width: 12px; height: 12px; flex: none; border-radius: 50%; text-decoration: none; border: 1px solid var(--fr); background: #05070c; box-shadow: inset 0 0 3px #000; }
.hudm .itip .sk u.full { border-color: var(--gc); background: radial-gradient(circle at 35% 35%, #fff, var(--gc) 60%, #000); }
.hudm .itip .du { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); white-space: pre-line; }
.hudm .itip .du .bar { flex: 1; height: 5px; border-radius: 3px; background: #05070c; overflow: hidden; }
.hudm .itip .du .bar i { display: block; height: 100%; width: 100%; background: var(--ac); transform-origin: 0 0; }
.hudm .itip .du .bar.low i { background: var(--bad); }
.hudm .itip .rq .ln::before { content: "\\2713  "; color: var(--ok); } .hudm .itip .rq .ln.bad::before { content: "\\2715  "; color: var(--bad); }
.hudm .itip .bd { display: flex; flex-wrap: wrap; gap: 4px; }
.hudm .itip .bd span { font-size: 10px; padding: 1px 7px; border-radius: 8px; color: var(--bad-soft); background: color-mix(in srgb, var(--bad) 12%, transparent); border: 1px solid color-mix(in srgb, var(--bad) 35%, transparent); }
.hudm .itip .bd .ln { flex-basis: 100%; font-size: 11px; }
.hudm .itip .story { font-style: italic; color: var(--gold); line-height: 1.4; }
.hudm .itip .story .lbl { font-style: normal; display: block; margin-bottom: 2px; }
.hudm .itip .gm { color: #da70d6; font-weight: 700; }
.hudm .itip .ft { display: flex; justify-content: space-between; gap: 8px; align-items: center; padding: 6px 12px; font-size: 11px; color: var(--dim); background: #0006; }
.hudm .itip .hint { padding: 4px 12px 7px; font-size: 10px; color: var(--dim); background: #0006; }
.hudm .itip .hint kbd { font: 600 9px ui-monospace, Consolas, monospace; padding: 0 4px; border-radius: 3px; border: 1px solid var(--fr); color: var(--tx); }
.hudm .itip .tc.worn { opacity: .92; }
`;

interface Shown { item: UserItem; ctx: ItemLabelContext; opts: ItemTooltipOptions; anchor: HTMLElement | null }

export class ItemTooltip {
  readonly el: HTMLElement;
  private cur: Shown | null = null;
  private shift = false;
  private source: ItemInfoSource | null = null;
  private unsource: (() => void) | null = null;
  /** ItemIndexes asked for by the current draw (redraw when one arrives). */
  private readonly waiting = new Set<number>();
  /** The pointer (client coordinates) when following the mouse. */
  private px = 0; private py = 0;
  /** Measured once per draw: the layer's box and scale, its size in HUD pixels, the tooltip's size. */
  private box = { left: 0, top: 0, k: 1, W: 0, H: 0, w: 0, hh: 0 };
  /** The anchor element's box in layer pixels (anchor mode). */
  private anchorBox: { l: number; t: number; r: number } | null = null;
  private readonly follow: () => void;
  private readonly redrawSoon: () => void;

  constructor(private readonly layer: HTMLElement, private readonly signal: AbortSignal, source?: ItemInfoSource) {
    injectCss("modern-tooltip", CSS);
    this.el = h("div", "itip");
    this.el.hidden = true;
    this.el.setAttribute("role", "tooltip");
    layer.append(this.el);
    this.follow = rafCoalesce(() => this.place(), signal);
    this.redrawSoon = rafCoalesce(() => { if (this.cur) this.draw(false); }, signal);
    if (source) this.setInfoSource(source);
    signal.addEventListener("abort", () => this.dispose(), { once: true });
  }

  /** Where missing ItemInfos are requested (GameUiContext's gameInfo). Replaces an earlier source. */
  setInfoSource(source: ItemInfoSource | null): void {
    if (source === this.source) return;
    this.unsource?.();
    this.unsource = null;
    this.source = source;
    if (!source || this.signal.aborted) return;
    this.unsource = source.onChange((kind, index) => {
      if (kind === "item" && this.cur && this.waiting.has(index)) this.redrawSoon();
    });
  }

  get visible(): boolean { return this.cur !== null; }
  /** The item shown now (null when hidden). */
  get item(): UserItem | null { return this.cur?.item ?? null; }

  /** Shift held (from the input router's key state; false on blur): shows the worn item's card beside the tooltip. */
  setShift(held: boolean): void {
    if (held === this.shift) return;
    this.shift = held;
    if (this.cur?.opts.compareWith) this.draw(false);
  }

  /** Shows the item's card beside `anchor` (an element) or at a client point (then follows move()). */
  show(anchor: TooltipAnchor, item: UserItem, ctx: ItemLabelContext, opts: ItemTooltipOptions = {}): void {
    if (this.signal.aborted) return;
    const wasShown = this.cur !== null;
    const isEl = anchor instanceof HTMLElement;
    if (!isEl) { this.px = anchor.x; this.py = anchor.y; }
    this.cur = { item, ctx, opts, anchor: isEl ? anchor : null };
    this.draw(!wasShown);
  }

  /** Redraws the current item (its UserItem changed: durability, added stats, sockets ...). */
  refresh(): void { if (this.cur) this.draw(false); }

  /** Follows the pointer (client coordinates): at most one transform write a frame, no layout reads. */
  move(x: number, y: number): void {
    if (!this.cur) return;
    this.px = x; this.py = y;
    if (this.cur.anchor) { this.cur.anchor = null; this.anchorBox = null; }
    this.follow();
  }

  /** Re-measures after a layout change (screen size, HUD scale). */
  remeasure(): void { if (this.cur) this.measure(); }

  hide(): void {
    if (!this.cur) return;
    this.cur = null;
    this.waiting.clear();
    this.el.hidden = true;
    this.el.classList.remove("in");
    this.el.replaceChildren();
  }

  dispose(): void {
    this.hide();
    this.unsource?.();
    this.unsource = null;
    this.source = null;
    this.el.remove();
  }

  // — Drawing —

  private draw(fresh: boolean): void {
    const s = this.cur!;
    this.waiting.clear();
    const main = itemLabelSections(s.item, s.ctx, s.opts);
    this.need(s.item, s.ctx, s.opts.info !== undefined);
    let worn: ItemLabelData | null = null;
    const cmp = s.opts.compareWith;
    if (cmp && cmp !== s.item) {
      const wornOpts: ItemLabelOptions = { inspect: s.opts.inspect, hideDura: s.opts.hideDura, hideAdded: s.opts.hideAdded };
      worn = itemLabelSections(cmp, s.ctx, wornOpts);
      this.need(cmp, s.ctx, false);
    }
    const cards = [this.card(main, s, worn, false)];
    if (this.shift && worn && cmp) cards.push(this.card(worn, { ...s, item: cmp, opts: {} }, null, true));
    this.el.replaceChildren(...cards);
    this.el.hidden = false;
    if (fresh) restartAnim(this.el, "in");
    this.measure();
  }

  /** Requests the ItemInfos a draw lacked (the item and its socket items). */
  private need(item: UserItem, ctx: ItemLabelContext, hasOwnInfo: boolean): void {
    const ask = (it: UserItem) => {
      if (it.ItemIndex > 0 && !ctx.info(it)) { this.waiting.add(it.ItemIndex); this.source?.requestItem(it.ItemIndex); }
    };
    if (!hasOwnInfo) ask(item);
    for (const slot of item.Slots ?? []) if (slot) ask(slot);
  }

  private card(d: ItemLabelData, s: Shown, worn: ItemLabelData | null, isWorn: boolean): HTMLElement {
    const c = h("div", "tc" + (d.grade >= ItemGrade.Legendary ? " shine" : "") + (isWorn ? " worn" : "") + (d.broken ? " broken" : ""));
    c.style.setProperty("--gc", gradeVar(d.grade));
    if (isWorn) c.append(h("div", "tag", "EQUIPPED"));

    // Header: icon, name, grade · type
    const ic = h("div", "ic");
    if (d.info) {
      const index = d.info.Image;
      const now = libPictureNow(Lib.Items, index);
      if (now) ic.style.backgroundImage = `url(${now.url})`;
      else if (now === undefined) void libPicture(Lib.Items, index).then((p) => { if (p && ic.isConnected) ic.style.backgroundImage = `url(${p.url})`; });
    }
    const nameLine = d.sections[0]?.kind === "name" ? d.sections[0].lines[0] : undefined;
    const title = d.head ? d.head.name : nameLine?.text ?? "";
    const sub = d.head ? [d.head.gradeText, d.head.typeText].filter((t) => t).join(" · ") : "Loading item details…";
    c.append(h("div", "hd", ic, h("div", "nm", h("b", "", title), sub ? h("small", "", sub) : null)));

    // Change against the worn item
    if (worn && !isWorn) c.append(this.compareRow(d, worn, s));

    // Durability / weight
    if (d.head && (d.head.duraText || d.head.weight > 0)) {
      const du = h("div", "sec du");
      if (d.head.dura && d.head.dura.max > 0) {
        const frac = Math.max(0, Math.min(1, d.head.dura.current / d.head.dura.max));
        const fill = h("i");
        fill.style.transform = `scaleX(${frac})`;
        du.append(h("div", "bar" + (frac < 0.3 ? " low" : ""), fill));
      }
      du.append([d.head.duraText.trim(), d.head.weight > 0 ? `W: ${d.head.weight}` : ""].filter((t) => t).join("  "));
      c.append(du);
    }

    let price: string | null = null;
    for (const sec of d.sections) {
      if (sec.kind === "name") continue;
      if (sec.kind === "need") {
        const p = sec.lines.find((l) => l.tone === "price");
        if (p) price = p.text;
        const rest = sec.lines.filter((l) => l.tone !== "price");
        const el = this.section("rq", rest);
        if (el) c.append(el);
        continue;
      }
      const el = this.sectionFor(sec);
      if (el) c.append(el);
    }

    if (!isWorn) {
      const hint = s.opts.hint ?? "";
      if (price || hint) c.append(h("div", "ft", price ? h("span", "gold", price) : h("span"), hint ? h("span", "", hint) : null));
      if (worn && !this.shift) c.append(h("div", "hint", "Hold ", h("kbd", "", "Shift"), " to compare with the worn item"));
    }
    return c;
  }

  private sectionFor(sec: ItemLabelSection): HTMLElement | null {
    switch (sec.kind) {
      case "socket": {
        const lines = sec.lines.filter((l) => l.text);
        if (!lines.length) return null;
        const el = h("div", "sec sk");
        for (const l of lines) {
          const row = this.line(l);
          if (l.slot) row.prepend(h("u", l.slot === "full" ? "full" : ""));
          el.append(row);
        }
        return el;
      }
      case "bind": {
        const lines = sec.lines.filter((l) => l.text);
        if (!lines.length) return null;
        const el = h("div", "sec bd");
        for (const l of lines) {
          if (l.tone === "notice") el.append(h("span", "", l.text));
          else if (l.tone === "soulbound") el.append(h("span", "", "Soulbound")); // the owner's name is not ported
          else el.append(this.line(l));
        }
        return el;
      }
      case "story": {
        const lines = sec.lines.filter((l) => l.text);
        if (!lines.length) return null;
        const el = h("div", "sec story");
        for (const l of lines) el.append(l.tone === "storyTitle" ? h("span", "lbl", l.text) : h("div", "ln", l.text));
        return el;
      }
      case "gm": return this.section("gm", sec.lines);
      case "overlap": return this.section("ov", sec.lines);
      case "awake": return this.section("aw", sec.lines);
      default: return this.section("", sec.lines);
    }
  }

  /** A section of plain lines (empty lines are skipped like the desktop's empty labels; nothing left = no section). */
  private section(cls: string, lines: readonly ItemLabelLine[]): HTMLElement | null {
    const shown = lines.filter((l) => l.text);
    if (!shown.length) return null;
    const el = h("div", "sec" + (cls ? " " + cls : ""));
    for (const l of shown) el.append(this.line(l));
    return el;
  }

  private line(l: ItemLabelLine): HTMLElement {
    const tone = l.tone ?? "plain";
    const cls = tone === "added" ? "add" : tone === "grade" || tone === "bad" || tone === "hint" || tone === "rental" || tone === "rentalTime" ? tone : "";
    return h("div", "ln" + (cls ? " " + cls : ""), l.text);
  }

  private compareRow(d: ItemLabelData, worn: ItemLabelData, s: Shown): HTMLElement {
    const row = h("div", "sec cmp");
    const cmp = s.opts.compareWith!;
    if (cmp.UniqueID === s.item.UniqueID) { row.append(h("i", "eq", "Equipped")); return row; }
    if (!d.info || !worn.info || d.info.Type === ItemType.Gem) {
      row.append(h("i", "eq", "Worn: " + (worn.head?.name ?? "…")));
      return row;
    }
    row.append(`vs ${worn.head?.name ?? ""}:`);
    let any = false;
    for (const [stat, label] of COMPARE) {
      const a = (d.stats[stat] ?? 0) - (worn.stats[stat] ?? 0);
      if (!a) continue;
      any = true;
      row.append(h("i", a > 0 ? "up" : "dn", `${a > 0 ? "▲" : "▼"} ${signed(a)} ${label}`));
    }
    if (!any) row.append(h("i", "eq", "no change"));
    return row;
  }

  // — Placement —

  /** One layout read per draw: the layer's box and scale, the tooltip's size and the anchor's box. */
  private measure(): void {
    const r = this.layer.getBoundingClientRect();
    const lw = this.layer.offsetWidth;
    const k = (lw ? r.width / lw : 1) || 1;
    this.box = { left: r.left, top: r.top, k, W: lw, H: this.layer.offsetHeight, w: this.el.offsetWidth, hh: this.el.offsetHeight };
    const a = this.cur?.anchor;
    if (a && a.isConnected) {
      const ar = a.getBoundingClientRect();
      this.anchorBox = { l: (ar.left - r.left) / k, t: (ar.top - r.top) / k, r: (ar.right - r.left) / k };
    } else this.anchorBox = null;
    this.place();
  }

  private place(): void {
    if (!this.cur) return;
    const { left, top, k, W, H, w, hh } = this.box;
    let x: number, y: number;
    if (this.anchorBox) {
      const a = this.anchorBox;
      x = a.r + 6;
      if (x + w > W - 4) x = a.l - w - 6;
      y = a.t;
    } else {
      const lx = (this.px - left) / k, ly = (this.py - top) / k;
      x = lx + 18;
      if (x + w > W - 4) x = lx - w - 14;
      y = ly + 18;
    }
    if (y + hh > H - 4) y = H - hh - 4;
    x = Math.round(Math.max(4, Math.min(x, W - w - 4)));
    y = Math.round(Math.max(4, y));
    setStyle(this.el, "transform", `translate3d(${x}px, ${y}px, 0)`);
  }
}

/** The runtime's item tooltip (built on first use in the 'tips' layer; disposed with the runtime). Pass the info
 *  source (GameUiContext's gameInfo) once, from the integrator. */
export function itemTooltip(rt: UiRuntime, source?: ItemInfoSource): ItemTooltip {
  const t = rt.service("tooltip", (r) => {
    const tip = new ItemTooltip(r.layers.tips, r.signal);
    const off = r.layoutChanged.on(() => tip.remeasure());
    r.signal.addEventListener("abort", off, { once: true });
    return tip;
  });
  if (source) t.setInfoSource(source);
  return t;
}
