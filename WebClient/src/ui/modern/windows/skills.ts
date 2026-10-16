// Ported from play/hudSkills.ts (SkillsMock): the new UI's skills window, wired to the real Magic model
// (src/game/magic.ts) the way the classic skill page (magicBar.ts MagicButton + AssignKeyPanel) and the hero's skill
// page (heroMagicWindow.ts) are. Changes from the mock:
//  - the list is the learned spells the server sent (Magic.magics; the hero tab Magic.heroMagics): the mock's
//    per-class spell table, its unlearned / locked rows, the "Learned only" chip, the role categories and the fake
//    progress are gone (Crystal sends only learned spells and has no spell roles);
//  - level pips and the training bar from UserMagic.Level / Experience / Need1-3; "next level" is the character level
//    the next skill level needs (Level1-3 by skill level); MP cost = BaseCost + LevelCost x Level; the cooldown is
//    UserMagic.delay with a live sweep from castTime (a 100 ms poll while shown); range from ClientMagic.Range;
//  - keys: bar 1 (keys 1-8, Bar1Skill1-8) and bar 2 (keys 9-16, Bar2Skill1-8) plus "No key", sent through
//    Magic.assignKey (the classic AssignKeyPanel rule, shared with MagicBar.assign); passive spells take keys as on the
//    desktop (pressing them does nothing). Key labels follow the user's key bindings;
//  - a Hero tab while the user has a hero: keys 17-24 (HeroSkill1-8) through Magic.assignHeroKey and a per-spell
//    auto-cast switch (C.HeroSpellToggle via Magic.heroSpellToggle). The switch is locked until the server's
//    S.HeroAutoCast answer (no optimistic state); the hero AI only casts spells that have a key AND auto-cast on;
//  - the hotkeys are the registry's (Skills / Skills2, hud/gameMenu.ts MENU_WINDOWS); the close hint reads the binding;
//  - the window renders when shown and is only marked dirty while closed; a narrow layout below a 1040 px layer.

import type { UserMagic } from "../../../game/magic.ts";
import { HERO_KEY_OFFSET, isPassiveSpell } from "../../../game/magic.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange } from "../../../game/keybinds.ts";
import { Lib, libPicture, libPictureNow } from "../../mir/controls.ts";
import { SKILL_DESCRIPTIONS } from "../../skillHints.ts";
import { h, injectCss, later, setStyle, setText } from "../core/dom.ts";
import { className, fmt, fmt1, fmtDuration } from "../core/format.ts";
import { button, chips, searchBox, tabs, toggleSwitch, type Choice } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { slotOption, type SkillView } from "../hud/skillRow.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

type Filter = "all" | "keyed" | "free";
const FILTERS: readonly Choice<Filter>[] = [
  { value: "all", label: "All" },
  { value: "keyed", label: "On a key", title: "Spells with a skill bar key" },
  { value: "free", label: "No key", title: "Spells without a key" },
];
const HERO_TAB = 1;
/** How long an auto-cast switch waits for S.HeroAutoCast before it unlocks again (an old server never answers). */
const AUTOCAST_WAIT_MS = 3000;

/** The skill descriptions' "next level" value: the character level the next skill level needs (magicBar.ts nextLevel). */
const nextLevel = (m: UserMagic) => (m.Level === 0 ? m.Level1 : m.Level === 1 ? m.Level2 : m.Level === 2 ? m.Level3 : 0);
/** Training points the next skill level needs (MagicButton.Update's exp label); 0 at the top level. */
const need = (m: UserMagic) => (m.Level === 0 ? m.Need1 : m.Level === 1 ? m.Need2 : m.Level === 2 ? m.Need3 : 0);
const cost = (m: UserMagic) => m.BaseCost + m.LevelCost * m.Level;
/** SKILL_DESCRIPTIONS placeholders ({0} level, {1} next level, {2} base cost). */
const fillIn = (text: string, ...args: (string | number)[]) => text.replace(/\{(\d+)\}/g, (_, i: string) => String(args[Number(i)] ?? ""));

/** The key binding behind a magic key (1-8 bar 1, 9-16 bar 2, 17-24 hero). */
function keyOption(key: number) {
  const view: SkillView = key > HERO_KEY_OFFSET ? 2 : key > 8 ? 1 : 0;
  return slotOption(view, (key - 1) % 8);
}
/** The binding's label ("Ctrl + F1"), or the desktop default when unbound. */
function keyText(key: number): string {
  const fallback = `${key > HERO_KEY_OFFSET ? "Shift + " : key > 8 ? "Ctrl + " : ""}F${((key - 1) % 8) + 1}`;
  return keyLabel(keyOption(key)) || fallback;
}
/** A short label for badges and key buttons ("^F1", "⇧F1", "Alt+F1"). */
function keyShort(key: number): string {
  return keyText(key).replace(/Ctrl \+ /g, "^").replace(/Shift \+ /g, "⇧").replace(/ \+ /g, "+");
}

/** The description's type line ("Toggle Skill", "Passive Skill", ...) as a badge. */
function spellType(spell: number): string {
  if (isPassiveSpell(spell)) return "Passive";
  const raw = SKILL_DESCRIPTIONS[spell];
  const line = raw?.split("\n").map((l) => l.trim()).find((l) => /\bSkill\b/.test(l) && l.length < 24) ?? "";
  return /Passive/.test(line) ? "Passive" : /Toggle/.test(line) ? "Toggle" : /Buff/.test(line) ? "Buff" : "Active";
}

/** The description lines (classic hint text, filled in), without the name line. */
function describe(m: UserMagic): string[] {
  const raw = SKILL_DESCRIPTIONS[m.Spell];
  if (!raw) return [];
  const lines = fillIn(raw, m.Level, nextLevel(m), m.BaseCost).split("\n").map((l) => l.trim()).filter((l) => l !== "");
  if (lines[0] === m.Name) lines.shift();
  return lines;
}

function cooldownText(ms: number): string {
  if (ms <= 0) return "None";
  return ms < 60000 ? `${fmt1(ms / 1000)} s` : fmtDuration(ms / 1000);
}

const CSS = `
.hudm .swin { --winh: 476px; width: 640px; }
.hudm .swin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .swin .stabs { margin: 6px 14px 0; }
.hudm .swin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .swin .left { width: 330px; display: flex; flex-direction: column; padding: 10px 10px 8px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin .tools { display: flex; flex-direction: column; gap: 6px; margin-bottom: 6px; }
.hudm .swin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .swin .row { position: relative; display: flex; align-items: center; gap: 9px; height: 46px; padding: 0 8px 0 4px; margin-bottom: 3px; cursor: pointer;
  border: 1px solid transparent; border-radius: var(--rad); }
.hudm .swin .row:nth-child(odd) { background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .swin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .swin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .swin .ic { position: relative; width: 38px; height: 36px; flex: none; border-radius: calc(var(--rad) * .8); overflow: hidden; border: 1px solid var(--fr);
  background: center / auto no-repeat color-mix(in srgb, var(--pb) 70%, #000); image-rendering: pixelated; box-shadow: 0 0 0 1px #000; }
.hudm .swin .ic .cd { position: absolute; inset: 0; background: rgba(0, 0, 0, .62); transform-origin: bottom; transform: scaleY(0); pointer-events: none; }
.hudm .swin .mid { flex: 1; min-width: 0; }
.hudm .swin .nm { display: flex; align-items: center; gap: 6px; font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; }
.hudm .swin .badge { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .swin .badge.off { color: var(--dim); border-color: var(--fr); }
.hudm .swin .sub { display: flex; align-items: center; gap: 7px; margin-top: 4px; }
.hudm .swin .pips { display: flex; gap: 2px; }
.hudm .swin .pips b { width: 9px; height: 4px; border-radius: 1px; background: color-mix(in srgb, var(--fr) 60%, #000); }
.hudm .swin .pips b.on { background: var(--ac); box-shadow: 0 0 4px var(--glow); }
.hudm .swin .tbar { flex: 1; height: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .swin .tbar i { display: block; width: 0; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .swin .rt { flex: none; text-align: right; font-size: 10px; line-height: 1.3; color: var(--dim); }
.hudm .swin .rt b { display: block; font-size: 11px; color: var(--tx); font-weight: 600; }
.hudm .swin .key { position: absolute; left: 1px; top: 1px; z-index: 1; font-size: 9px; font-weight: 700; padding: 0 3px; border-radius: 3px; color: #fff;
  background: color-mix(in srgb, var(--ac) 70%, #000); box-shadow: 0 0 4px var(--glow); }
.hudm .swin .empty { padding: 30px 10px; text-align: center; color: var(--dim); font-size: 12px; line-height: 1.5; }
.hudm .swin .foot { padding-top: 6px; font-size: 11px; color: var(--dim); display: flex; justify-content: space-between; gap: 8px; }
.hudm .swin .foot b { color: var(--tx); font-weight: 600; }
.hudm .swin .detail { flex: 1; min-width: 0; padding: 14px 14px 12px; display: flex; flex-direction: column; gap: 10px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .swin .head { display: flex; gap: 12px; align-items: center; }
.hudm .swin .big { position: relative; width: 76px; height: 72px; flex: none; overflow: hidden; border-radius: var(--rad); border: 1px solid var(--ac); image-rendering: pixelated;
  background: center / 72px 68px no-repeat color-mix(in srgb, var(--pb) 70%, #000); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .swin .head h3 { margin: 0 0 4px; font-size: 16px; color: #fff; }
.hudm .swin .head .tags { display: flex; gap: 5px; margin-bottom: 6px; }
.hudm .swin .head .pips b { width: 14px; height: 5px; }
.hudm .swin .desc { font-size: 12px; line-height: 1.5; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); }
.hudm .swin .facts { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 10px; }
.hudm .swin .facts div { display: flex; justify-content: space-between; gap: 6px; font-size: 11px; padding: 3px 7px; border-radius: calc(var(--rad) * .5); background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .swin .facts span { color: var(--dim); } .hudm .swin .facts b { color: var(--tx); font-weight: 600; }
.hudm .swin .facts b.warn { color: var(--warn); }
.hudm .swin .train { font-size: 11px; color: var(--dim); }
.hudm .swin .train .tbar { height: 6px; margin-top: 4px; border: 1px solid var(--fr); }
.hudm .swin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; color: var(--dim); }
.hudm .swin .note { font-size: 11px; color: var(--dim); line-height: 1.4; }
.hudm .swin .note.warn { color: var(--warn); }
.hudm .swin .keys { display: grid; grid-template-columns: repeat(8, 1fr); gap: 4px; margin-top: 5px; }
.hudm .swin .keys .btn { padding: 0; min-width: 0; font-size: 11px; }
.hudm .swin .keys .btn.on { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 30%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .swin .keyrow { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.hudm .swin .auto { display: flex; align-items: center; justify-content: space-between; gap: 8px; font-size: 12px; }
.hudm.narrow .swin { width: 580px; }
.hudm.narrow .swin .left { width: 270px; }
.hudm.narrow .swin .keys { grid-template-columns: repeat(4, 1fr); }
`;

/** One list row's live parts (the cooldown sweep is written by the poll). */
interface RowRef { m: UserMagic; cd: HTMLElement }

export class SkillsWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  private readonly tabBar: ReturnType<typeof tabs>;
  private readonly list = h("div", "list");
  private readonly foot = h("div", "foot");
  private readonly detail = h("div", "detail");
  private tab = 0;
  private query = "";
  private filter: Filter = "all";
  /** The picked spell per tab (Spell value), -1 = the first in the list. */
  private readonly picked = [-1, -1];
  private rows: RowRef[] = [];
  /** The detail's live parts (cooldown sweep and the remaining-time text), null when no spell is shown. */
  private live: { m: UserMagic; cd: HTMLElement; left: HTMLElement } | null = null;
  /** Hero spells whose auto-cast switch waits for S.HeroAutoCast -> the state asked for (shown, locked, meanwhile). */
  private readonly pending = new Map<number, boolean>();
  private cancelWait: (() => void) | null = null;
  private level = -1;
  /** The title bar's close cross: its hint names the live Skills key (render). */
  private readonly closeX: HTMLElement | null;

  constructor(ui: ModernUiHost) {
    super(ui.rt, "swin", 380, 60);
    this.ui = ui;
    injectCss("win-skills", CSS);
    const m = ui.ctx.models, sig = this.signal;

    this.tabBar = tabs(["Skills", "Hero"], 0, (i) => this.selectTab(i));
    this.tabBar.el.classList.add("stabs");
    const search = searchBox("Search skills", (q) => { this.query = q.toLowerCase(); this.invalidate(); }, { signal: sig });
    const filterRow = chips<Filter>(FILTERS, ["all"], (sel) => { this.filter = sel.values().next().value ?? "all"; this.invalidate(); });
    const left = h("div", "left", h("div", "tools", search.el, filterRow.el), this.list, this.foot);
    const title = this.titleBar("SKILLS", "Close");
    this.closeX = title.querySelector<HTMLElement>(".x");
    this.el.append(h("div", "panel", title, this.tabBar.el, h("div", "cols", left, this.detail)));

    // Row picks (one delegated listener).
    this.listen(this.list, "click", (e) => {
      const row = e.target instanceof Element ? e.target.closest<HTMLElement>(".row") : null;
      const spell = row ? Number(row.dataset["spell"]) : NaN;
      if (!Number.isFinite(spell) || this.picked[this.tab] === spell) return;
      this.picked[this.tab] = spell;
      this.invalidate();
    });

    // Model events only mark the window dirty (rendered on the next frame while open, else on the next open).
    const off = [
      m.magic.subscribe(() => this.invalidate()),
      m.magic.onHeroChange(() => this.invalidate()),
      m.hero.onChange(() => this.invalidate()),
      onKeyBindsChange(() => this.invalidate()),
      ui.packets.on((p) => { if (p.type === "HeroAutoCast" || p.type === "HeroInformation") this.answered(); }),
    ];
    sig.addEventListener("abort", () => { for (const f of off) f(); this.cancelWait?.(); }, { once: true });

    // The cooldown sweeps and the character level (no model event): a light poll while shown.
    this.everyShown(100, (now) => this.tick(now));
  }

  protected shown(): void { this.tick(this.ui.ctx.now()); }

  // — Tabs —

  private hasHeroTab(): boolean { return this.ui.ctx.models.hero.hasHero; }

  private selectTab(i: number): void {
    if (i === this.tab) return;
    this.tab = i;
    this.invalidate();
  }

  // — Render —

  protected render(): void {
    if (this.closeX) {
      const close = keyLabel(KeybindOptions.Skills2) || keyLabel(KeybindOptions.Skills);
      this.closeX.title = `Close${close ? ` (${close})` : ""} · Esc closes the front window, Shift+Esc all`;
    }
    const heroTab = this.hasHeroTab();
    const heroBtn = this.tabBar.el.children[HERO_TAB] as HTMLElement | undefined;
    if (heroBtn) heroBtn.hidden = !heroTab;
    if (!heroTab && this.tab === HERO_TAB) this.tab = 0;
    this.tabBar.select(this.tab);
    this.level = this.ui.ctx.player.level;
    this.renderList();
    this.renderDetail();
  }

  private spells(): readonly UserMagic[] {
    const magic = this.ui.ctx.models.magic;
    return this.tab === HERO_TAB ? magic.heroMagics : magic.magics;
  }

  private filtered(): UserMagic[] {
    const q = this.query, f = this.filter, out: UserMagic[] = [];
    for (const m of this.spells()) {
      if (f === "keyed" && m.Key === 0) continue;
      if (f === "free" && m.Key !== 0) continue;
      if (q && !m.Name.toLowerCase().includes(q) && !spellType(m.Spell).toLowerCase().includes(q)) continue;
      out.push(m);
    }
    return out;
  }

  /** The shown spell: the picked one if it is still learned and listed, else the first listed. */
  private current(items: readonly UserMagic[]): UserMagic | undefined {
    const want = this.picked[this.tab]!;
    return items.find((m) => m.Spell === want) ?? items[0];
  }

  private renderList(): void {
    const ctx = this.ui.ctx, magic = ctx.models.magic, hero = ctx.models.hero;
    const all = this.spells(), items = this.filtered(), sel = this.current(items);
    const heroTab = this.tab === HERO_TAB;
    this.rows = [];
    this.list.replaceChildren();
    if (!items.length) {
      const text = heroTab && !magic.heroId ? "Summon your hero to see and set up its skills."
        : all.length ? "No skills match." : heroTab ? "Your hero has not learned any skills yet." : "You have not learned any skills yet.";
      this.list.append(h("div", "empty", text));
    }
    for (const m of items) {
      const cd = h("i", "cd");
      const ic = h("div", "ic", cd);
      this.setIcon(ic, m.Icon);
      if (m.Key) ic.append(h("span", "key", keyShort(m.Key)));
      const pips = h("div", "pips");
      for (let i = 0; i < 3; i++) pips.append(h("b", i < m.Level ? "on" : ""));
      const n = need(m), fill = h("i");
      fill.style.width = m.Level >= 3 ? "100%" : n > 0 ? Math.min(100, (m.Experience / n) * 100).toFixed(0) + "%" : "0";
      const badges: HTMLElement[] = [h("span", "badge", spellType(m.Spell))];
      if (heroTab && !isPassiveSpell(m.Spell) && !magic.heroAutoCast(m.Spell)) badges.push(h("span", "badge off", "Manual"));
      const c = cost(m);
      const row = h("div", "row" + (m === sel ? " sel" : ""), ic,
        h("div", "mid", h("div", "nm", m.Name, ...badges), h("div", "sub", pips, h("div", "tbar", fill))),
        h("div", "rt", h("b", "", m.Level >= 3 ? "MAX" : `Lv ${m.Level}`), c ? `${fmt(c)} MP` : "No MP"));
      row.dataset["spell"] = String(m.Spell);
      row.title = m.Key ? `${m.Name}: ${keyText(m.Key)}` : `${m.Name}: no key`;
      this.rows.push({ m, cd });
      this.list.append(row);
    }
    const keyed = all.filter((m) => m.Key !== 0).length;
    const who = heroTab
      ? (hero.info ? `${hero.info.name} · ${className(hero.info.cls)} · Level ${hero.info.level}` : "Hero")
      : `${className(ctx.player.user?.look.class ?? 0)} · Level ${ctx.player.level}`;
    this.foot.replaceChildren(h("span", "", h("b", "", String(all.length)), ` learned, ${keyed} on a key`), h("span", "", who));
  }

  private renderDetail(): void {
    const ctx = this.ui.ctx, hero = ctx.models.hero;
    const heroTab = this.tab === HERO_TAB;
    const m = this.current(this.filtered());
    this.detail.replaceChildren();
    this.live = null;
    if (!m) return;

    const cd = h("i", "cd");
    const big = h("div", "big", cd);
    this.setIcon(big, m.Icon);
    const pips = h("div", "pips");
    for (let i = 0; i < 3; i++) pips.append(h("b", i < m.Level ? "on" : ""));
    const type = spellType(m.Spell), passive = isPassiveSpell(m.Spell);
    const head = h("div", "head", big, h("div", "", h("h3", "", m.Name), h("div", "tags", h("span", "badge", type)), pips));

    const lines = describe(m);
    const desc = h("div", "desc", ...(lines.length ? lines.map((l) => h("div", "", l)) : ["No description."]));

    const fact = (k: string, v: HTMLElement | string) => h("div", "", h("span", "", k), typeof v === "string" ? h("b", "", v) : v);
    const owner = heroTab ? hero.info?.level ?? 0 : ctx.player.level;
    const nl = nextLevel(m);
    const nextEl = h("b", m.Level < 3 && nl > owner ? "warn" : "", m.Level >= 3 ? "Mastered" : `Lv ${nl}`);
    nextEl.title = m.Level >= 3 ? "" : `${heroTab ? "Your hero needs" : "You need"} character level ${nl} for skill level ${m.Level + 1}`;
    const left = h("b", "", "Ready");
    const c = cost(m);
    const facts = h("div", "facts",
      fact("MP cost", c ? fmt(c) : "None"), fact("Cooldown", cooldownText(m.delay)),
      fact("Range", m.Range ? `${m.Range} cells` : "Self / melee"), fact("Skill level", `${m.Level} / 3`),
      fact("Next level", nextEl), fact("Recharge", left));

    const parts: HTMLElement[] = [head, desc, facts];
    if (m.Level < 3) {
      const n = need(m), fill = h("i");
      fill.style.width = n > 0 ? Math.min(100, (m.Experience / n) * 100).toFixed(1) + "%" : "0";
      parts.push(h("div", "train", `Training ${fmt(m.Experience)} / ${fmt(n)}`, h("div", "tbar", fill)));
    }
    parts.push(heroTab ? this.heroKeys(m, passive) : this.userKeys(m, passive));
    this.detail.append(...parts);
    this.live = { m, cd, left };
  }

  /** Bar 1 (keys 1-8) and bar 2 (keys 9-16) buttons + "No key" (Magic.assignKey). */
  private userKeys(m: UserMagic, passive: boolean): HTMLElement {
    const magic = this.ui.ctx.models.magic;
    const bar = (first: number, label: string) => {
      const keys = h("div", "keys");
      for (let k = first; k < first + 8; k++) keys.append(this.keyButton(m, k, magic.magics, () => magic.assignKey(m, k), null));
      return h("div", "", h("div", "lbl", label), keys);
    };
    const none = button("No key", () => magic.assignKey(m, 0), { title: `Take ${m.Name} off its key` });
    none.setDisabled(m.Key === 0, "It has no key");
    const note = passive ? h("div", "note", "Passive: always on. A key does nothing, but you can still put it on the bar.") : null;
    return h("div", "", h("div", "keyrow", h("div", "lbl", "Skill bar keys"), none.el), note, bar(1, "Bar 1"), bar(9, "Bar 2"));
  }

  /** Hero keys 17-24 (Magic.assignHeroKey) and the auto-cast switch (Magic.heroSpellToggle). */
  private heroKeys(m: UserMagic, passive: boolean): HTMLElement {
    const magic = this.ui.ctx.models.magic, hero = this.ui.ctx.models.hero;
    const why = !magic.heroId ? "Summon your hero first" : hero.dead ? "Your hero is dead" : null;
    const keys = h("div", "keys");
    for (let k = HERO_KEY_OFFSET + 1; k <= HERO_KEY_OFFSET + 8; k++) keys.append(this.keyButton(m, k, magic.heroMagics, () => magic.assignHeroKey(m, k), why));
    const none = button("No key", () => magic.assignHeroKey(m, 0), { title: `Take ${m.Name} off its key` });
    none.setDisabled(m.Key === 0 || why !== null, why ?? "It has no key");
    const out = h("div", "", h("div", "keyrow", h("div", "lbl", "Hero skill keys"), none.el), keys);
    if (passive) {
      out.append(h("div", "note", "Passive: always on."));
      return out;
    }
    const asked = this.pending.get(m.Spell), waiting = asked !== undefined;
    const on = asked ?? magic.heroAutoCast(m.Spell);
    const sw = toggleSwitch(on, (v) => this.setAutoCast(m.Spell, v), { small: true, label: "Auto-cast", title: "Let your hero cast this on its own" });
    if (waiting) sw.lock(true, "Waiting for the server");
    else if (!magic.heroId) sw.lock(true, "Summon your hero first");
    const auto = h("div", "auto", h("span", "", "Hero casts it on its own"), sw.el);
    const note = !on ? h("div", "note", "Manual: your hero only casts it when you press its key.")
      : m.Key === 0 ? h("div", "note warn", "Your hero only casts spells that have a key: give it one above.")
      : h("div", "note", "Your hero casts it on its own in combat, or when you press its key.");
    out.append(h("div", "", h("div", "lbl", "Auto-cast"), auto, note));
    return out;
  }

  private keyButton(m: UserMagic, key: number, list: readonly UserMagic[], assign: () => void, why: string | null): HTMLElement {
    const holder = list.find((x) => x.Key === key && x !== m);
    const text = keyText(key);
    const b = button(keyShort(key), () => { if (m.Key !== key) assign(); },
      { cls: m.Key === key ? "on" : "", title: m.Key === key ? `${m.Name} is on ${text}` : `Put ${m.Name} on ${text}` + (holder ? ` (replaces ${holder.Name})` : "") });
    if (why) b.setDisabled(true, why);
    return b.el;
  }

  // — Hero auto-cast (server authoritative: locked until S.HeroAutoCast) —

  private setAutoCast(spell: number, on: boolean): void {
    const magic = this.ui.ctx.models.magic;
    if (!magic.heroId || this.pending.has(spell)) { this.invalidate(); return; }
    this.pending.set(spell, on);
    magic.heroSpellToggle(spell, on);
    this.cancelWait?.();
    this.cancelWait = later(() => { this.cancelWait = null; this.answered(); }, AUTOCAST_WAIT_MS, this.signal);
    this.invalidate();
  }

  /** S.HeroAutoCast (or a new S.HeroInformation, or the wait ran out): unlock the switches and show the server's state. */
  private answered(): void {
    if (!this.pending.size) return;
    this.pending.clear();
    this.cancelWait?.();
    this.cancelWait = null;
    this.invalidate();
  }

  // — Polled state (while shown) —

  private tick(now: number): void {
    for (const r of this.rows) this.sweep(r.cd, r.m, now);
    const live = this.live;
    if (live) {
      const leftMs = this.sweep(live.cd, live.m, now);
      setText(live.left, leftMs > 0 ? `${fmt1(leftMs / 1000)} s` : "Ready");
    }
    if (this.level !== this.ui.ctx.player.level) this.invalidate();
  }

  /** The cooldown sweep (magicBar.ts coolDownFrame: none under 100 ms left). Returns the ms left (0 = ready). */
  private sweep(el: HTMLElement, m: UserMagic, now: number): number {
    const left = Number.isFinite(m.castTime) && m.delay > 0 ? m.castTime + m.delay - now : 0;
    const f = left >= 100 ? Math.min(1, left / m.delay) : 0;
    setStyle(el, "transform", `scaleY(${f.toFixed(3)})`);
    return left >= 100 ? left : 0;
  }

  /** MagIcon2[Icon * 2] as the element's background (cached pictures set at once). */
  private setIcon(el: HTMLElement, icon: number): void {
    const index = icon * 2;
    const now = libPictureNow(Lib.MagIcon2, index);
    if (now) { el.style.backgroundImage = `url(${now.url})`; return; }
    if (now === null) return;
    void libPicture(Lib.MagIcon2, index).then((pic) => { if (pic && !this.signal.aborted) el.style.backgroundImage = `url(${pic.url})`; });
  }
}

registerWindow("skills", (host) => new SkillsWindow(host));
