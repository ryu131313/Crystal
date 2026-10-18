// Ported from play/hudOptions.ts (OptionsMock): the new UI's Options window, wired to the real settings the way the
// classic src/ui/optionWindow.ts (OptionDialog + its web "More" panel) and src/ui/chatOptionWindow.ts are. Changes from
// the mock:
//  - the mock's fake catalogue is gone: every row is a real value. settings.ts gameSettings / setSetting (the desktop's
//    [Game] / [Chat] / [Filters] values, Lighting, the web-only SmoothMove / Atmosphere / SmoothShadows / CozyMode and
//    the Modern UI's Ui* values), the two SoundManager volumes (audio.ts persists them), the resolution
//    (localStorage "crystal.resolution", read by startGame: applies at the next log-in) and the social allow toggles
//    (Account: S.AllowToggles state, setToggle sends C.SetAllowToggle);
//  - changes apply at once like the classic window (no draft / Apply / Cancel): the theme, HUD scale, opacity and stat
//    plates go live through modernUi.ts's onSettingChange (runtime setTheme / setScale, hud.reloadPlates), and the
//    allow toggles are the server's, which a draft cannot hold. The per-row reset arrow (back to the default) and a
//    per-page "Restore defaults" stay;
//  - allow toggles are server authoritative: a click sends the request and locks the switch until S.AllowToggles
//    answers (the switch keeps showing the server's value; the server writes its own chat hint). Before the first
//    S.AllowToggles (an older server) only Allow observing works, through C.Chat "@ALLOWOBSERVE" like OptionDialog;
//  - an Interface page: "Classic interface" (saves UiStyle Classic, a popup offers to log out now:
//    ctx.actions.logOut(false, true)), HUD scale / auto scale / opacity, the stat plate picker (UiPlates), the theme
//    preset (the nine of core/theme.ts) and custom colours / knobs over it, and the values only the classic interface
//    reads (grouped and labelled so);
//  - the local chat hints of OptionDialog / ChatDialog (skill mode, HP/MP mode, movement style, lighting);
//  - key bindings are not edited here (one editor: the "keybinds" window); the nav and the search link to it, so the
//    mock's Controls page, its private bind copy and its capture keydown listener are gone (one input router);
//  - hidden, no system for them: display mode, renderer, brightness, weather, effect quality, separate object shadows,
//    auto pick up, PvP confirm, show helmet, low-HP warning, separate effect / ambience / interface volumes, mute in
//    background, chat font size / opacity / timestamps / whisper tab / trade channel / language filter, the social
//    "Friends & guild" tiers, whisper blocking, show online / share location, the whole Accessibility page, language.
//    The Lover / Mentor chat filters are hidden too: ChatDialog.Update has no case for them (they hide nothing);
//  - search is debounced (core searchBox), pages are built on their first visit, model events only mark the window
//    dirty, sliders write at most once a frame; no timers.

import { AllowToggle, ChatType } from "../../../protocol/enums.generated.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange } from "../../../game/keybinds.ts";
import { DEFAULT_SETTINGS, gameSettings, onSettingChange, setSetting, uiStyle, type GameSettings } from "../../../game/settings.ts";
import { h, injectCss, rafCoalesce, setStyle, setText, typingField, type Kid } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { DEFAULT_THEME, THEME_PRESETS, resolveTheme, themePreset, type ThemeColors } from "../core/theme.ts";
import { button, searchBox, segmented, toggleSwitch, type Btn, type Choice } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { PLATE_DEFAULTS, PLATE_STATS, formatPlates, parsePlates } from "../hud/statPlates.ts";
import { windowCatalog, registerWindow, type ModernUiHost } from "../windowRegistry.ts";

type Cat = "Graphics" | "Interface" | "Gameplay" | "Audio" | "Chat" | "Social";
type BoolKey = { [K in keyof GameSettings]: GameSettings[K] extends boolean ? K : never }[keyof GameSettings];

const CATS: readonly [Cat, IconName, string][] = [
  ["Graphics", "eye", "How the world looks."],
  ["Interface", "menu", "The interface, its theme and labels."],
  ["Gameplay", "sword", "How the game behaves while you play."],
  ["Audio", "play", "Sound and music volume."],
  ["Chat", "chat", "Chat tabs, channels and filters."],
  ["Social", "friends", "What other players may ask of you. The server keeps these."],
];

/** The window key of the one key binding editor. */
const KEYBINDS = "keybinds";

/** startGame's resolutions (game.ts: localStorage "crystal.resolution", 1920 when unset). */
const RES_KEY = "crystal.resolution";
const RESOLUTIONS: readonly Choice<string>[] = [
  { value: "1024", label: "1024 x 768" }, { value: "1280", label: "1280 x 800" },
  { value: "1366", label: "1366 x 768" }, { value: "1920", label: "1920 x 1080" },
];
const RES_DEFAULT = "1920";

/** The scene lab's graphics presets, remapped onto the real values. */
const PRESETS: readonly { name: string; vals: Partial<Pick<GameSettings, "Lighting" | "Atmosphere" | "SmoothShadows" | "CozyMode">> }[] = [
  { name: "Performance", vals: { Lighting: "Classic", Atmosphere: false, SmoothShadows: false, CozyMode: false } },
  { name: "Balanced", vals: { Lighting: "Enhanced", Atmosphere: false, SmoothShadows: true, CozyMode: true } },
  { name: "Quality", vals: { Lighting: "Enhanced", Atmosphere: true, SmoothShadows: true, CozyMode: true } },
];

/** The custom theme overrides and their "use the preset's" values. */
const THEME_OVERRIDES: readonly [keyof GameSettings, string | number][] = [
  ["UiAccent", ""], ["UiFrame", ""], ["UiText", ""], ["UiHue", -1], ["UiSat", -1], ["UiLit", -1], ["UiGlow", -1], ["UiRadius", -1],
];

const PLATE_SLOTS = ["Left 1", "Left 2", "Left 3", "Right 1", "Right 2", "Right 3"] as const;

// — Row definitions —

interface Base {
  id: string; cat: Cat; group: string; label: string; desc: string;
  /** Why the control refuses changes now ("" = usable). */
  lock?: () => string;
  /** Not shown (e.g. a toggle the server does not send). */
  hidden?: () => boolean;
  /** Differs from its default (the reset arrow shows); default: get() !== def. */
  mod?: () => boolean;
  /** Back to the default; default: set(def). */
  reset?: () => void;
}
type Def = Base & (
  | { kind: "toggle"; get(): boolean; set(v: boolean): void; def?: boolean }
  | { kind: "slider"; min: number; max: number; step: number; unit: string; get(): number; set(v: number): void; def?: number; deferred?: boolean }
  | { kind: "select"; options: readonly Choice<string>[]; get(): string; set(v: string): void; def?: string }
  | { kind: "seg"; options: readonly Choice<string>[]; get(): string; set(v: string): void; def?: string }
  | { kind: "colour"; get(): string; set(v: string): void }
  | { kind: "plates" }
  | { kind: "action"; text: string; run(): void }
);

interface Row { readonly def: Def; readonly el: HTMLElement; sync(): void }
interface Page { readonly el: HTMLElement; readonly rows: Row[]; readonly heads: [HTMLElement, Row[]][]; presets?: ReturnType<typeof segmented<string>> }

const CSS = `
.hudm .owin { width: 860px; }
.hudm .owin .panel { height: 560px; display: flex; flex-direction: column; }
.hudm .owin .omain { flex: 1; min-height: 0; display: flex; }
.hudm .owin .onav { width: 176px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .owin .onav .ui-search { margin-bottom: 8px; }
.hudm .owin .onav .ui-search input { width: 100%; box-sizing: border-box; }
.hudm .owin .onav .it { display: flex; align-items: center; gap: 10px; height: 32px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad);
  font: inherit; font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; background: none; text-align: left; pointer-events: auto; }
.hudm .owin .onav .it:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .owin .onav .it.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .owin .onav .it.on svg { color: var(--ac); }
.hudm .owin .onav .it .k { margin-left: auto; font-size: 10px; color: var(--dim); font-weight: 600; }
.hudm .owin .onav .sep { height: 1px; margin: 6px 4px; background: color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .owin .opage { flex: 1; min-width: 0; overflow-y: auto; padding: 14px 20px 18px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .owin .ph { display: flex; align-items: center; gap: 12px; margin-bottom: 6px; flex-wrap: wrap; }
.hudm .owin .ph h2 { margin: 0; font-size: 18px; color: #fff; }
.hudm .owin .ph .sub { font-size: 11px; color: var(--dim); }
.hudm .owin .ph .right { margin-left: auto; display: flex; gap: 6px; align-items: center; }
.hudm .owin .gh { margin: 16px 0 4px; font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--acd); display: flex; align-items: center; gap: 10px; }
.hudm .owin .gh::after { content: ""; flex: 1; height: 1px; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 40%, transparent), transparent); }
.hudm .owin .gh span { color: var(--dim); letter-spacing: .08em; }
.hudm .owin .set { display: flex; align-items: center; gap: 14px; padding: 8px 10px; border-radius: var(--rad); border: 1px solid transparent; }
.hudm .owin .set:hover { background: color-mix(in srgb, var(--frl) 5%, transparent); border-color: color-mix(in srgb, var(--fr) 40%, transparent); }
.hudm .owin .set .lb { flex: 1; min-width: 0; }
.hudm .owin .set .lb b { display: block; font-size: 13px; font-weight: 600; color: var(--tx); }
.hudm .owin .set .lb small { display: block; margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .owin .set.plates { align-items: flex-start; }
.hudm .owin .rst { width: 20px; height: 20px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: 50%; cursor: pointer; padding: 0;
  font: inherit; font-size: 13px; color: var(--dim); background: none; border: 0; visibility: hidden; pointer-events: auto; }
.hudm .owin .set.mod .rst { visibility: visible; }
.hudm .owin .rst:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .owin .ctl { flex: none; width: 290px; display: flex; justify-content: flex-end; align-items: center; gap: 10px; }
.hudm .owin .ctl .ui-seg { flex: none; }
.hudm .owin .ctl select.fld { min-width: 190px; cursor: pointer; }
.hudm .owin input.orng { -webkit-appearance: none; appearance: none; width: 200px; height: 4px; border-radius: 2px; outline: none; cursor: pointer; pointer-events: auto;
  background: linear-gradient(90deg, var(--ac) 0 var(--p, 50%), color-mix(in srgb, var(--fr) 70%, #000) var(--p, 50%) 100%); }
.hudm .owin input.orng::-webkit-slider-thumb { -webkit-appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #fff; border: 2px solid var(--ac); cursor: pointer; }
.hudm .owin input.orng::-moz-range-thumb { width: 12px; height: 12px; border-radius: 50%; background: #fff; border: 2px solid var(--ac); cursor: pointer; }
.hudm .owin input.orng:disabled, .hudm .owin input.orng:disabled::-webkit-slider-thumb { cursor: not-allowed; }
.hudm .owin input.orng:focus-visible::-webkit-slider-thumb { box-shadow: 0 0 6px var(--glow); }
.hudm .owin .val { width: 48px; text-align: right; font-size: 12px; font-weight: 600; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .owin input.oclr { width: 46px; height: 24px; padding: 1px 2px; cursor: pointer; pointer-events: auto; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); background: var(--field); }
.hudm .owin .pgrid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px 8px; width: 290px; flex: none; }
.hudm .owin .pgrid label { display: flex; flex-direction: column; gap: 2px; font-size: 10px; color: var(--dim); }
.hudm .owin .pgrid select.fld { width: 100%; min-width: 0; padding: 0 4px; cursor: pointer; }
.hudm .owin .onote { padding: 30px; text-align: center; color: var(--dim); }
.hudm .owin .olink { display: flex; align-items: center; gap: 10px; margin-top: 14px; padding: 8px 10px; font-size: 11px; color: var(--dim); }
.hudm .owin mark { background: color-mix(in srgb, var(--ac) 45%, transparent); color: #fff; border-radius: 2px; }
.hudm .owin .ofoot { display: flex; align-items: center; gap: 8px; padding: 8px 16px 10px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 11px; color: var(--dim); }
.hudm .owin .ofoot .sp { flex: 1; }
.hudm .owin [hidden] { display: none !important; }
/* Narrow layer (below 1040 px): a slimmer nav and controls. */
.hudm.narrow .owin { width: 700px; }
.hudm.narrow .owin .onav { width: 140px; padding: 8px 6px; }
.hudm.narrow .owin .onav .it { gap: 7px; padding: 0 7px; }
.hudm.narrow .owin .onav .it .k { display: none; }
.hudm.narrow .owin .opage { padding: 10px 12px 14px; }
.hudm.narrow .owin .set { gap: 10px; padding: 7px 6px; }
.hudm.narrow .owin .ctl, .hudm.narrow .owin .pgrid { width: 240px; }
.hudm.narrow .owin .ctl select.fld { min-width: 150px; }
.hudm.narrow .owin input.orng { width: 150px; }
`;

export class OptionsWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  private cat: Cat = "Graphics";
  private query = "";
  private readonly nav = h("div", "onav");
  private readonly page = h("div", "opage");
  private readonly navItems = new Map<Cat, HTMLElement>();
  private readonly keysItem: HTMLElement;
  private readonly pages = new Map<Cat, Page>();
  /** The search results (rebuilt per query); null while not searching. */
  private results: Page | null = null;
  private readonly defs: Def[];
  /** Allow toggles sent and not answered yet (the switch stays locked). */
  private readonly pending = new Set<number>();
  /** The resolution preference (not a settings.ts value: game.ts reads the storage key at log-in). */
  private resPref = readRes();
  /** The live key labels (render): the Key bindings nav chip and the close cross's hint. */
  private readonly keysChip = h("span", "k");
  private readonly closeX: HTMLElement | null;

  constructor(ui: ModernUiHost) {
    super(ui.rt, "owin", 52, 30);
    this.ui = ui;
    injectCss("win-options", CSS);
    const sig = this.signal;
    this.defs = this.buildDefs();

    // Nav: the search, the pages and the link to the key binding editor.
    const search = searchBox("Search settings", (q) => this.search(q), { signal: sig });
    this.nav.append(search.el);
    for (const [c, icon] of CATS) {
      const it = this.navButton(svg(icon), c);
      it.addEventListener("click", () => {
        if (this.cat === c && !this.query) return;
        this.cat = c;
        if (this.query) { search.set(""); this.search(""); }
        this.invalidate();
      }, { signal: sig });
      this.nav.append(it);
      this.navItems.set(c, it);
    }
    this.keysItem = this.navButton(svg("keyboard"), "Key bindings", this.keysChip);
    this.keysItem.title = "Key bindings are edited in their own window";
    this.keysItem.addEventListener("click", () => this.openKeybinds(), { signal: sig });
    this.nav.append(h("div", "sep"), this.keysItem);

    const title = this.titleBar("OPTIONS", "Close");
    this.closeX = title.querySelector<HTMLElement>(".x");
    this.el.append(h("div", "panel",
      title,
      h("div", "omain", this.nav, this.page),
      h("div", "ofoot", "Changes apply at once and are kept in this browser. Social settings are kept by the server.", h("div", "sp"))));

    // Model events only mark the window dirty (synced on the next frame while open, else on the next open).
    const acc = ui.ctx.models.account;
    const off = [
      onSettingChange(() => this.invalidate()),
      acc.togglesChanged.on(() => { this.pending.clear(); this.invalidate(); }), // S.AllowToggles answers every request
      acc.changed.on(() => { this.pending.delete(AllowToggle.Observe); this.invalidate(); }), // S.AllowObserve (older servers)
      ui.rt.themeChanged.on(() => this.invalidate()),
      onKeyBindsChange(() => this.invalidate()),
    ];
    sig.addEventListener("abort", () => { for (const f of off) f(); }, { once: true });
  }

  protected shown(): void {
    this.resPref = readRes(); // another tab may have changed it
  }

  protected closed(): void {
    // A request still unanswered keeps no lock across a reopen (the next S.AllowToggles corrects the view anyway).
    this.pending.clear();
  }

  protected render(): void {
    const key = keyLabel(KeybindOptions.Keybind);
    setText(this.keysChip, key);
    this.keysChip.hidden = !key;
    if (this.closeX) {
      const close = keyLabel(KeybindOptions.Options) || "F12";
      const close2 = keyLabel(KeybindOptions.Options2);
      this.closeX.title = `Close (${close2 ? `${close} / ${close2}` : close}) · Esc closes the front window, Shift+Esc all`;
    }
    for (const [c, it] of this.navItems) it.classList.toggle("on", c === this.cat && !this.query);
    this.keysItem.hidden = !windowCatalog().has(KEYBINDS);
    const page = this.results ?? this.pageFor(this.cat);
    if (page.el.parentElement !== this.page || this.page.childNodes.length !== 1) {
      this.page.replaceChildren(page.el);
      this.page.scrollTop = 0;
    }
    for (const r of page.rows) r.sync();
    for (const [head, rows] of page.heads) head.hidden = rows.every((r) => r.el.hidden);
    if (page.presets) {
      const on = PRESETS.find((p) => (Object.keys(p.vals) as (keyof typeof p.vals)[]).every((k) => gameSettings[k] === p.vals[k]));
      page.presets.set(on?.name ?? "");
    }
  }

  // — Pages —

  private pageFor(cat: Cat): Page {
    let page = this.pages.get(cat);
    if (page) return page;
    const [, , info] = CATS.find(([c]) => c === cat)!;
    const right = h("div", "right");
    const el = h("div", "", h("div", "ph", h("div", "", h("h2", "", cat), h("div", "sub", info)), right));
    page = { el, rows: [], heads: [] };
    if (cat === "Graphics") {
      const pre = segmented<string>(PRESETS.map((p) => ({ value: p.name, label: p.name })), "", (name) => {
        const p = PRESETS.find((x) => x.name === name);
        if (!p) return;
        for (const k of Object.keys(p.vals) as (keyof typeof p.vals)[]) setSetting(k, p.vals[k] as never);
        this.invalidate();
      });
      pre.el.title = "Lighting, atmosphere, shadows and colour grade in one go";
      right.append(h("span", "sub", "Preset"), pre.el);
      page.presets = pre;
    }
    const resettable = this.defs.filter((d) => d.cat === cat && canReset(d));
    if (resettable.length) {
      right.append(button("Restore defaults", () => {
        void this.ui.rt.popups.ask(`Put every ${cat} setting back to its default?`, { title: "Restore defaults", kind: "question" }).then((yes) => {
          if (!yes || this.signal.aborted) return;
          for (const d of resettable) if (isMod(d)) resetDef(d);
          this.invalidate();
        });
      }, { title: `Every ${cat} setting back to its default` }).el);
    }
    this.fill(page, this.defs.filter((d) => d.cat === cat), false);
    this.pages.set(cat, page);
    return page;
  }

  /** Appends the rows of `defs` under their group headings. */
  private fill(page: Page, defs: readonly Def[], counts: boolean): void {
    let group = "", rows: Row[] = [];
    for (const d of defs) {
      if (d.group !== group) {
        group = d.group;
        rows = [];
        const n = counts ? defs.filter((x) => x.group === group && x.cat === d.cat).length : 0;
        const head = h("div", "gh", counts ? `${d.cat} · ${group}` : group, n ? h("span", "", String(n)) : null);
        page.el.append(head);
        page.heads.push([head, rows]);
      }
      const r = this.row(d);
      page.rows.push(r);
      rows.push(r);
      page.el.append(r.el);
    }
  }

  /** Search: matching rows of every page (fresh rows; the pages keep theirs), with the hits marked. */
  private search(q: string): void {
    this.query = q;
    const low = q.toLowerCase();
    if (!low) { this.results = null; this.invalidate(); return; }
    const page: Page = { el: h("div", "", h("div", "ph", h("h2", "", `Results for “${q}”`))), rows: [], heads: [] };
    const hits = this.defs.filter((d) => !d.hidden?.() && `${d.label} ${d.desc} ${d.group} ${d.cat}`.toLowerCase().includes(low));
    this.fill(page, hits, true);
    if (!hits.length) page.el.append(h("div", "onote", "No settings match."));
    for (const el of page.el.querySelectorAll<HTMLElement>(".lb b, .lb small")) mark(el, low);
    if (windowCatalog().has(KEYBINDS)) {
      const open = button("Key bindings", () => this.openKeybinds(), { icon: "keyboard" });
      page.el.append(h("div", "olink", "Looking for a key? Keys are changed in the Key bindings window.", open.el));
    }
    this.results = page;
    this.invalidate();
  }

  // — Rows —

  private row(d: Def): Row {
    const ctl = h("div", "ctl");
    const rst = h("button", "rst", "↺");
    rst.type = "button";
    rst.title = "Back to the default";
    rst.setAttribute("aria-label", `Reset ${d.label}`);
    const el = h("div", "set", h("div", "lb", h("b", "", d.label), h("small", "", d.desc)), rst, ctl);
    rst.addEventListener("click", () => { resetDef(d); this.invalidate(); });
    if (!canReset(d)) rst.remove();
    const changed = () => this.invalidate();
    let lastWhy = "\u0000";
    const why = () => d.lock?.() ?? "";
    let sync: () => void;

    switch (d.kind) {
      case "toggle": {
        const sw = toggleSwitch(d.get(), (v) => { d.set(v); changed(); }, { label: d.label });
        ctl.append(sw.el);
        sync = () => {
          sw.set(d.get());
          const w = why();
          if (w !== lastWhy) { lastWhy = w; sw.lock(w !== "", w); }
        };
        break;
      }
      case "slider": {
        const r = h("input", "orng");
        Object.assign(r, { type: "range", min: String(d.min), max: String(d.max), step: String(d.step) });
        r.setAttribute("aria-label", d.label);
        const val = h("span", "val");
        const show = (n: number) => {
          setText(val, `${n}${d.unit}`);
          setStyle(r, "--p", (((n - d.min) / (d.max - d.min)) * 100).toFixed(1) + "%");
        };
        const apply = rafCoalesce((n: number) => { d.set(n); changed(); }, this.signal);
        // A deferred slider (HUD scale) rescales this window, which moves the slider under the pointer and feeds the
        // drag back into itself: show the number while dragging, apply on release (change also fires for keys).
        let held = false;
        r.addEventListener("input", () => { const n = Number(r.value); show(n); if (d.deferred) held = true; else apply(n); });
        if (d.deferred) r.addEventListener("change", () => { held = false; d.set(Number(r.value)); changed(); });
        ctl.append(r, val);
        sync = () => {
          const v = d.get();
          if (!held && Number(r.value) !== v) r.value = String(v);
          show(Number(r.value));
          const w = why();
          if (w !== lastWhy) { lastWhy = w; r.disabled = w !== ""; r.title = w; }
        };
        break;
      }
      case "select": {
        const sel = h("select", "fld");
        sel.setAttribute("aria-label", d.label);
        for (const o of d.options) sel.append(Object.assign(h("option", "", o.label), { value: o.value }));
        sel.addEventListener("change", () => { d.set(sel.value); changed(); });
        ctl.append(sel);
        sync = () => { const v = d.get(); if (sel.value !== v) sel.value = v; };
        break;
      }
      case "seg": {
        const seg = segmented<string>(d.options, d.get(), (v) => { d.set(v); changed(); });
        ctl.append(seg.el);
        sync = () => seg.set(d.get());
        break;
      }
      case "colour": {
        const inp = typingField(h("input", "oclr"));
        inp.type = "color";
        inp.setAttribute("aria-label", d.label);
        const val = h("span", "val");
        const apply = rafCoalesce((v: string) => { d.set(v); changed(); }, this.signal);
        inp.addEventListener("input", () => { setText(val, inp.value); apply(inp.value); });
        ctl.append(val, inp);
        sync = () => {
          const v = d.get();
          if (inp.value.toLowerCase() !== v.toLowerCase() && document.activeElement !== inp) inp.value = v;
          setText(val, v);
        };
        break;
      }
      case "plates": {
        el.classList.add("plates");
        ctl.remove();
        const grid = h("div", "pgrid");
        const sels = PLATE_SLOTS.map((slot, i) => {
          const sel = h("select", "fld");
          sel.setAttribute("aria-label", `Stat plate ${slot}`);
          let og: HTMLOptGroupElement | null = null;
          for (const s of PLATE_STATS) {
            if (!og || og.label !== s.group) { og = h("optgroup"); og.label = s.group; sel.append(og); }
            og.append(Object.assign(h("option", "", s.name), { value: s.key }));
          }
          sel.addEventListener("change", () => { this.pickPlate(i, sel.value); changed(); });
          grid.append(h("label", "", slot, sel));
          return sel;
        });
        el.append(grid);
        sync = () => {
          const keys = parsePlates(gameSettings.UiPlates);
          sels.forEach((s, i) => { const k = keys[i] ?? ""; if (s.value !== k) s.value = k; });
        };
        break;
      }
      case "action": {
        const b: Btn = button(d.text, () => { if (!why()) { d.run(); changed(); } });
        ctl.append(b.el);
        sync = () => { const w = why(); if (w !== lastWhy) { lastWhy = w; b.setDisabled(w !== "", w); } };
        break;
      }
    }

    const row: Row = {
      def: d, el,
      sync: () => {
        const hide = d.hidden?.() ?? false;
        el.hidden = hide;
        if (hide) return;
        sync();
        el.classList.toggle("mod", canReset(d) && isMod(d));
      },
    };
    return row;
  }

  /** A stat plate pick; a stat already on another plate swaps with it (parsePlates needs six distinct keys). */
  private pickPlate(slot: number, key: string): void {
    const keys = parsePlates(gameSettings.UiPlates);
    const was = keys.indexOf(key);
    if (was === slot) return;
    if (was >= 0) keys[was] = keys[slot]!;
    keys[slot] = key;
    setSetting("UiPlates", formatPlates(keys));
  }

  // — Actions —

  private openKeybinds(): void {
    if (!this.ui.openWindow(KEYBINDS, true)) this.ui.drag.toast("Key bindings are not available yet");
  }

  private hint(text: string): void { this.ui.ctx.models.chatLog.receive(text, ChatType.Hint); }

  /** The "Classic interface" switch: UiStyle is read at log-in, so offer to log out now (the game menu's switch). */
  private setClassic(on: boolean): void {
    if (!on) { if (gameSettings.UiStyle !== "Modern") setSetting("UiStyle", "Modern"); return; }
    if (gameSettings.UiStyle !== "Classic") setSetting("UiStyle", "Classic");
    void this.ui.rt.popups.ask("The classic interface applies at your next log-in. Log out now to switch?",
      { title: "Classic interface", kind: "question", labels: { yes: "Log out now", no: "Later" } })
      .then((yes) => { if (yes && !this.signal.aborted) this.ui.ctx.actions.logOut(false, true); });
  }

  /** C.SetAllowToggle (server authoritative: locked until S.AllowToggles). Allow observing on a server without
   *  S.AllowToggles falls back to OptionDialog.ToggleObserve (C.Chat "@ALLOWOBSERVE" flips it; S.AllowObserve answers). */
  private setToggle(t: AllowToggle, v: boolean): void {
    const acc = this.ui.ctx.models.account;
    if (this.pending.has(t)) return;
    if (!acc.togglesKnown) {
      if (t !== AllowToggle.Observe || acc.allowObserve === v) return;
      this.ui.ctx.send(ClientPackets.Chat({ Message: "@ALLOWOBSERVE" }));
    } else {
      if (acc.toggle(t) === v || !acc.setToggle(t, v)) return;
    }
    this.pending.add(t);
  }

  private toggleState(t: AllowToggle): boolean {
    const acc = this.ui.ctx.models.account;
    return !acc.togglesKnown && t === AllowToggle.Observe ? acc.allowObserve : acc.toggle(t);
  }

  private toggleLock(t: AllowToggle): string {
    if (this.pending.has(t)) return "Waiting for the server";
    if (!this.ui.ctx.models.account.togglesKnown && t !== AllowToggle.Observe) return "This server does not send this setting";
    return "";
  }

  // — The catalogue —

  private buildDefs(): Def[] {
    const ctx = this.ui.ctx;
    const bool = (cat: Cat, group: string, key: BoolKey, label: string, desc: string, after?: (v: boolean) => void): Def =>
      ({ id: key, cat, group, label, desc, kind: "toggle", get: () => gameSettings[key], set: (v) => { setSetting(key, v); after?.(v); }, def: DEFAULT_SETTINGS[key] });
    const theme = (): ThemeColors => resolveTheme({
      theme: gameSettings.UiTheme,
      custom: { accent: gameSettings.UiAccent, frame: gameSettings.UiFrame, text: gameSettings.UiText, hue: gameSettings.UiHue,
        sat: gameSettings.UiSat, lit: gameSettings.UiLit, glow: gameSettings.UiGlow, radius: gameSettings.UiRadius },
    });
    const colour = (key: "UiAccent" | "UiFrame" | "UiText", field: "accent" | "frame" | "text", label: string, desc: string): Def =>
      ({ id: key, cat: "Interface", group: "Theme", label, desc, kind: "colour", get: () => theme()[field], set: (v) => setSetting(key, v.toLowerCase()),
        mod: () => gameSettings[key] !== "", reset: () => setSetting(key, "") });
    /** A theme knob in display units (`k` = display per setting unit). */
    const knob = (key: "UiHue" | "UiSat" | "UiLit" | "UiGlow" | "UiRadius", field: "hue" | "sat" | "lit" | "glow" | "radius", label: string, desc: string,
      min: number, max: number, step: number, unit: string, k: number): Def =>
      ({ id: key, cat: "Interface", group: "Theme", label, desc, kind: "slider", min, max, step, unit,
        get: () => Math.round(theme()[field] * k), set: (v) => setSetting(key, v / k), mod: () => gameSettings[key] >= 0, reset: () => setSetting(key, -1) });
    const allow = (t: AllowToggle, label: string, desc: string): Def =>
      ({ id: `allow${t}`, cat: "Social", group: "Who can reach you", label, desc, kind: "toggle", get: () => this.toggleState(t), set: (v) => this.setToggle(t, v), lock: () => this.toggleLock(t) });
    const [sw, shh] = ctx.screen;

    return [
      // Graphics
      { id: "res", cat: "Graphics", group: "Display", label: "Resolution", kind: "select", options: RESOLUTIONS, def: RES_DEFAULT,
        desc: `The game's screen size. Applies at your next log-in (now ${sw} x ${shh}; the server may allow less).`,
        get: () => this.resPref, set: (v) => { this.resPref = v; writeRes(v); } },
      { id: "Lighting", cat: "Graphics", group: "Lighting & effects", label: "Lighting", kind: "seg",
        desc: "Enhanced adds soft light, glow and night shading; Classic matches the original client.",
        options: [{ value: "Classic", label: "Classic" }, { value: "Enhanced", label: "Enhanced" }], def: DEFAULT_SETTINGS.Lighting,
        get: () => gameSettings.Lighting,
        set: (v) => { // ChatDialog.TryLightingCommand (@LIGHT NEW / OLD)
          const mode = v === "Classic" ? "Classic" : "Enhanced";
          if (gameSettings.Lighting === mode) return;
          setSetting("Lighting", mode);
          this.hint(mode === "Enhanced" ? "[Lighting: Enhanced]" : "[Lighting: Classic]");
        } },
      bool("Graphics", "Lighting & effects", "Atmosphere", "Atmosphere effects", "Glow, spell flashes, lit embers and heat haze (Enhanced lighting only)."),
      bool("Graphics", "Lighting & effects", "SmoothShadows", "Smooth shadows", "Solid soft shadows for bodies and map objects instead of the checkerboard dither."),
      bool("Graphics", "Lighting & effects", "CozyMode", "Cozy colour grade", "Slightly richer colour and contrast over the map."),
      bool("Graphics", "Lighting & effects", "Effect", "Effect lights", "The light and glow of spell and item effects (desktop: Effect)."),
      bool("Graphics", "Lighting & effects", "LevelEffect", "Level effects", "The desktop's level effects (desktop: @LEVELEFFECT)."),
      bool("Graphics", "Motion", "SmoothMove", "Smooth movement", "Players, monsters and the camera glide between steps."),
      // Interface
      { id: "UiStyle", cat: "Interface", group: "Interface style", label: "Classic interface", kind: "toggle",
        desc: "Switch back to the desktop-look interface. Applies at your next log-in.",
        get: () => uiStyle() === "Classic", set: (v) => this.setClassic(v) },
      { id: "MobileUi", cat: "Interface", group: "Interface style", label: "Touch controls", kind: "seg",
        desc: "The phone layout: joystick, big buttons and full-screen windows. Auto turns it on for touch screens. Applies at your next log-in.",
        options: [{ value: "Auto", label: "Auto" }, { value: "On", label: "On" }, { value: "Off", label: "Off" }], def: DEFAULT_SETTINGS.MobileUi,
        get: () => gameSettings.MobileUi,
        set: (v) => { if (v === "Auto" || v === "On" || v === "Off") setSetting("MobileUi", v); } },
      bool("Interface", "Interface style", "MobileLeftHanded", "Left-handed touch controls", "Joystick on the right, Attack and skills on the left (touch layout)."),
      bool("Interface", "Interface style", "MobileHaptics", "Vibration", "Vibrate on hits, low HP, level ups and skills that cannot cast (touch layout; Android)."),
      bool("Interface", "Interface style", "MobileAutoLoot", "Auto-loot", "When idle, walk to and pick up items within 2 cells (touch layout)."),
      { id: "MobileTextSize", cat: "Interface", group: "Interface style", label: "Touch text size", kind: "seg",
        desc: "The size of the text on the touch buttons, bars and target frame (touch layout).",
        options: [{ value: "Small", label: "Small" }, { value: "Normal", label: "Normal" }, { value: "Large", label: "Large" }], def: DEFAULT_SETTINGS.MobileTextSize,
        get: () => gameSettings.MobileTextSize,
        set: (v) => { if (v === "Small" || v === "Normal" || v === "Large") setSetting("MobileTextSize", v); } },
      bool("Interface", "Interface style", "MobileBatterySaver", "Battery saver", "30 frames a second and no atmosphere effects (touch layout)."),
      { id: "MobileReset", cat: "Interface", group: "Interface style", label: "Touch layout", kind: "action", text: "Reset buttons and zoom",
        desc: "Put the touch buttons back where they started and zoom out. Hold Menu in game to move and resize them.",
        run: () => { setSetting("MobileLayout", ""); setSetting("MobileZoom", 1); },
        lock: () => (gameSettings.MobileLayout || gameSettings.MobileZoom !== 1 ? "" : "Nothing changed") },
      { id: "UiScale", cat: "Interface", group: "HUD", label: "HUD scale", kind: "slider", min: 50, max: 200, step: 5, unit: "%", def: 100, deferred: true,
        desc: "Size of the HUD and windows (on top of auto scale). Applies when you let go of the slider.",
        get: () => Math.round(gameSettings.UiScale * 100), set: (v) => setSetting("UiScale", v / 100) },
      bool("Interface", "HUD", "UiAutoScale", "Auto scale", "Grow the HUD with the screen height (1 at 768 px)."),
      { id: "UiOpacity", cat: "Interface", group: "HUD", label: "HUD opacity", kind: "slider", min: 20, max: 100, step: 5, unit: "%", def: 100,
        desc: "How solid the bottom HUD is.",
        get: () => Math.round(gameSettings.UiOpacity * 100), set: (v) => setSetting("UiOpacity", v / 100) },
      { id: "UiPlates", cat: "Interface", group: "HUD", label: "Stat plates", kind: "plates",
        desc: "The six small plates above the HP / MP bars (you can also click a plate). Picking a stat that is on another plate swaps them.",
        mod: () => formatPlates(parsePlates(gameSettings.UiPlates)) !== formatPlates(PLATE_DEFAULTS), reset: () => setSetting("UiPlates", formatPlates(PLATE_DEFAULTS)) },
      { id: "UiTheme", cat: "Interface", group: "Theme", label: "Theme", kind: "select", def: DEFAULT_THEME,
        desc: "The colours of the HUD and every window. Choosing a theme clears your custom colours.",
        options: THEME_PRESETS.map((t) => ({ value: t.name, label: t.name })),
        get: () => themePreset(gameSettings.UiTheme).name,
        set: (v) => { for (const [k, none] of THEME_OVERRIDES) setSetting(k, none as never); setSetting("UiTheme", v); } },
      colour("UiAccent", "accent", "Accent colour", "Highlights, glows and the EXP ring."),
      colour("UiFrame", "frame", "Frame colour", "Window and slot borders."),
      colour("UiText", "text", "Text colour", "Most interface text."),
      knob("UiHue", "hue", "Panel hue", "The colour of the panels.", 0, 360, 1, "°", 1),
      knob("UiSat", "sat", "Panel saturation", "How colourful the panels are.", 0, 200, 5, "%", 100),
      knob("UiLit", "lit", "Panel brightness", "How light the panels are.", 30, 250, 5, "%", 100),
      knob("UiGlow", "glow", "Glow", "Strength of the glows.", 0, 200, 5, "%", 100),
      knob("UiRadius", "radius", "Corner radius", "Rounding of windows, slots and buttons.", 0, 12, 1, " px", 1),
      { id: "UiCustomReset", cat: "Interface", group: "Theme", label: "Custom colours", kind: "action", text: "Use the theme's colours",
        desc: "Drop every custom colour and knob above.",
        run: () => { for (const [k, none] of THEME_OVERRIDES) setSetting(k, none as never); },
        lock: () => (THEME_OVERRIDES.some(([k, none]) => gameSettings[k] !== none) ? "" : "No custom colours set") },
      bool("Interface", "World labels", "NameView", "Names", "Show player and monster names."),
      bool("Interface", "World labels", "DropView", "Item names on the ground", "Label dropped items."),
      bool("Interface", "World labels", "DisplayDamage", "Damage numbers", "Floating damage over players and monsters."),
      bool("Interface", "World labels", "DisplayBodyName", "Body names", "Show dead monsters' names under the mouse."),
      bool("Interface", "World labels", "HighlightTarget", "Highlight target", "Outline the monster or player you are attacking."),
      bool("Interface", "Classic interface only", "SkillMode", "Skill mode ~", "The classic skill mode label: ~ (on) or Ctrl (off).",
        (v) => this.hint(v ? "[Skill Mode: ~]" : "[Skill Mode: Ctrl]")), // GameScene.ChangeSkillMode
      bool("Interface", "Classic interface only", "SkillBar", "Skill bar", "Show the classic skill bar."),
      bool("Interface", "Classic interface only", "HPView", "HP / MP mode 1", "The classic HP / MP display mode (off = mode 2).",
        (v) => this.hint(v ? "[HP/MP Mode 1]" : "[HP/MP Mode 2]")),
      bool("Interface", "Classic interface only", "ModeView", "Mode view", "Show the attack and pet mode on the classic main panel."),
      bool("Interface", "Classic interface only", "DuraView", "Durability view", "Show worn equipment that is close to breaking."),
      bool("Interface", "Classic interface only", "TransparentChat", "Transparent chat", "A see-through classic chat box."),
      bool("Interface", "Classic interface only", "ExpandedBuffWindow", "Expanded buff window", "Show your buffs expanded."),
      bool("Interface", "Classic interface only", "ExpandedHeroBuffWindow", "Expanded hero buff window", "Show your hero's buffs expanded."),
      // Gameplay
      bool("Gameplay", "Combat", "TargetDead", "Target dead monsters", "Allow clicking corpses, e.g. to harvest them (desktop: @TARGETDEAD)."),
      bool("Gameplay", "Movement", "NewMove", "New movement style", "The desktop's movement style option.",
        (v) => this.hint(v ? "[New Movement Style]" : "[Old Movement Style]")), // NewMovementStyle / OldMovementStyle
      bool("Gameplay", "Auto potion", "AutoPot", "Auto potion", "Drink your best HP / MP potion from the bag or belt when you drop below the levels set here."),
      { id: "AutoPotHP", cat: "Gameplay", group: "Auto potion", label: "HP below", kind: "slider", min: 0, max: 99, step: 1, unit: "%", def: DEFAULT_SETTINGS.AutoPotHP,
        desc: "Drink an HP potion under this much health (0% = never).", lock: () => (gameSettings.AutoPot ? "" : "Auto potion is off"),
        get: () => gameSettings.AutoPotHP, set: (v) => setSetting("AutoPotHP", v) },
      { id: "AutoPotMP", cat: "Gameplay", group: "Auto potion", label: "MP below", kind: "slider", min: 0, max: 99, step: 1, unit: "%", def: DEFAULT_SETTINGS.AutoPotMP,
        desc: "Drink an MP potion under this much mana (0% = never).", lock: () => (gameSettings.AutoPot ? "" : "Auto potion is off"),
        get: () => gameSettings.AutoPotMP, set: (v) => setSetting("AutoPotMP", v) },
      // Audio
      { id: "volume", cat: "Audio", group: "Volume", label: "Sound", kind: "slider", min: 0, max: 100, step: 1, unit: "%", def: 70,
        desc: "Spells, hits, footsteps, monsters and the interface.", get: () => Math.round(ctx.sound.volume), set: (v) => { ctx.sound.volume = v; } },
      { id: "musicVolume", cat: "Audio", group: "Volume", label: "Music", kind: "slider", min: 0, max: 100, step: 1, unit: "%", def: 40,
        desc: "Map and login music.", get: () => Math.round(ctx.sound.musicVolume), set: (v) => { ctx.sound.musicVolume = v; } },
      // Chat
      bool("Chat", "Tabs", "ShowNormalChat", "Local tab", "Say, shouts and notices around you."),
      bool("Chat", "Tabs", "ShowWhisperChat", "Whispers tab", "Whispers, lover and mentor messages."),
      bool("Chat", "Tabs", "ShowGroupChat", "Group tab", "Your group's messages."),
      bool("Chat", "Tabs", "ShowGuildChat", "Guild tab", "Your guild's messages and notices."),
      bool("Chat", "Channels you can talk in", "ShowYellChat", "Shout", "Offer the shout channel (!) in the chat box."),
      bool("Chat", "Channels you can talk in", "ShowMentorChat", "Mentor", "Offer the mentor channel (!#) in the chat box."),
      bool("Chat", "Channels you can talk in", "ShowLoverChat", "Lover", "Offer the lover channel (:)) in the chat box."),
      bool("Chat", "Hide messages", "FilterNormalChat", "Hide say", "Hide normal chat from players around you."),
      bool("Chat", "Hide messages", "FilterShoutChat", "Hide shouts", "Hide shouts."),
      bool("Chat", "Hide messages", "FilterWhisperChat", "Hide whispers", "Hide whispers."),
      bool("Chat", "Hide messages", "FilterGroupChat", "Hide group", "Hide your group's messages."),
      bool("Chat", "Hide messages", "FilterGuildChat", "Hide guild", "Hide your guild's messages."),
      bool("Chat", "Hide messages", "FilterSystemChat", "Hide system", "Hide system messages."),
      // Social (the server's; S.AllowToggles)
      allow(AllowToggle.Group, "Group invites", "Let other players invite you to a group."),
      allow(AllowToggle.Trade, "Trade requests", "Let other players ask you to trade."),
      allow(AllowToggle.GuildInvite, "Guild invites", "Let guilds invite you."),
      allow(AllowToggle.FriendRequests, "Friend requests", "Let other players send you friend requests."),
      allow(AllowToggle.GroupRecall, "Group recall", "Let your group leader recall you to them."),
      allow(AllowToggle.Marriage, "Marriage requests", "Let other players ask for your hand in marriage."),
      allow(AllowToggle.LoverRecall, "Lover recall", "Let your partner recall you."),
      allow(AllowToggle.Mentor, "Mentor requests", "Let other players ask you to be their mentor."),
      allow(AllowToggle.Observe, "Allow observing", "Let other players watch you play."),
    ];
  }

  private navButton(icon: SVGElement, label: string, ...extra: Kid[]): HTMLButtonElement {
    const b = h("button", "it", icon, label, ...extra);
    b.type = "button";
    return b;
  }
}

// — Helpers —

function readRes(): string {
  try {
    const v = localStorage.getItem(RES_KEY) ?? "";
    return RESOLUTIONS.some((r) => r.value === v) ? v : RES_DEFAULT;
  } catch { return RES_DEFAULT; }
}

function writeRes(v: string): void {
  try { localStorage.setItem(RES_KEY, v); } catch { /* storage blocked: not kept */ }
}

/** The default value of a definition, or undefined (no reset). */
function defaultOf(d: Def): unknown {
  return "def" in d ? d.def : undefined;
}

function canReset(d: Def): boolean { return !!d.reset || defaultOf(d) !== undefined; }

function isMod(d: Def): boolean {
  if (d.mod) return d.mod();
  const def = defaultOf(d);
  return def !== undefined && "get" in d && d.get() !== def;
}

function resetDef(d: Def): void {
  if (d.reset) { d.reset(); return; }
  const def = defaultOf(d);
  if (def === undefined || !("set" in d)) return;
  (d.set as (v: unknown) => void)(def);
}

/** Wraps the first hit of `q` in a text node of `el` in <mark> (text nodes only, never markup). */
function mark(el: HTMLElement, q: string): void {
  const t = el.firstChild;
  if (!t || t.nodeType !== Node.TEXT_NODE) return;
  const s = t.textContent ?? "", i = s.toLowerCase().indexOf(q);
  if (i < 0) return;
  t.replaceWith(s.slice(0, i), h("mark", "", s.slice(i, i + q.length)), s.slice(i + q.length));
}

registerWindow("options", (host) => new OptionsWindow(host));
