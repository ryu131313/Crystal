// Ported from play/hudHero.ts (its on-screen frame, HeroMock.renderFrame): the summoned hero's frame on the left of the
// screen, the desktop's HeroInfoPanel + HeroBehaviourPanel (classic port src/ui/heroPanels.ts), over the Hero model
// (src/game/hero.ts: spawn state, info, behaviour) and HeroStats (maximum HP / MP, UserHeroObject.RefreshStats).
// Portrait = GameScene.HeroAvatar (Prguse 1400 + class + 10 x gender) with the level, HP / MP / EXP bars, and a
// behaviour chip that asks the server for the next HeroBehaviour (C.SetHeroBehaviour; the chip changes when
// S.SetHeroBehaviour answers, never before). Shown while the hero is summoned (HeroSpawnState > Unsummoned with
// S.HeroInformation), greyed when it died. A click on the portrait opens the "hero" window. The mock's fight / EXP / mana
// simulation and its summon cooldown are gone.

import { HeroBehaviour, HeroBehaviourName } from "../../../protocol/enums.generated.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { h, injectCss, rafCoalesce, setText, setStyle, asButton, later } from "../core/dom.ts";
import { fmt, pretty } from "../core/format.ts";
import type { UiRuntime } from "../core/runtime.ts";

const BEHAVIOURS = [HeroBehaviour.Attack, HeroBehaviour.CounterAttack, HeroBehaviour.Follow, HeroBehaviour.Custom] as const;

const CSS = `
.hudm .hframe { position: absolute; z-index: 2; left: 12px; top: 41%; width: 196px; display: flex; gap: 7px; padding: 5px; pointer-events: auto; border-radius: var(--rad); color: var(--tx);
  background: linear-gradient(90deg, color-mix(in srgb, var(--pb) 88%, transparent), color-mix(in srgb, var(--pb) 45%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6); }
.hudm .hframe.fallen { opacity: .6; }
.hudm .hframe.fallen .pt { filter: grayscale(1); }
.hudm .hframe .pt { position: relative; width: 42px; height: 42px; flex: none; border-radius: 50%; overflow: hidden; cursor: pointer; border: 2px solid var(--ac);
  background: var(--pb) center / cover no-repeat; image-rendering: pixelated; }
.hudm .hframe .pt.off { cursor: default; }
.hudm .hframe .pt b { position: absolute; left: 50%; bottom: -1px; transform: translateX(-50%); font-size: 9px; font-weight: 800; padding: 0 4px; border-radius: 5px; color: #fff; background: #000c; }
.hudm .hframe .in2 { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.hudm .hframe .n { display: flex; justify-content: space-between; align-items: center; gap: 6px; font-size: 11px; font-weight: 700; color: #fff; text-shadow: 0 1px 1px #000; }
.hudm .hframe .n > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .hframe .md { flex: none; font-size: 8px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; cursor: pointer; color: var(--ac); border: 1px solid var(--ac); }
.hudm .hframe .md:hover { color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .hframe .md.wait { opacity: .6; cursor: progress; }
.hudm .hframe .b { position: relative; height: 7px; border-radius: 3px; background: #04060a; overflow: hidden; }
.hudm .hframe .b i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); transition: transform .4s; }
.hudm .hframe .b.hp i { background: #d23a3a; } .hudm .hframe .b.mp i { background: #3a72e0; } .hudm .hframe .b.xp { height: 3px; } .hudm .hframe .b.xp i { background: var(--ac); }
`;

const frac = (v: number, max: number) => (max > 0 ? Math.max(0, Math.min(1, v / max)) : 0);

export class HeroFrame {
  readonly el: HTMLElement;
  private readonly pt: HTMLElement;
  private readonly lvl = h("b");
  private readonly name = h("span");
  private readonly mode: HTMLElement;
  private readonly hp = h("i");
  private readonly mp = h("i");
  private readonly xp = h("i");
  private avatar = -1;
  private asked = -1;
  private readonly refresh: () => void;
  private readonly slow: { on(): void; off(): void };

  constructor(private readonly rt: UiRuntime, private readonly ctx: GameUiContext) {
    injectCss("hudm-heroframe-css", CSS);
    const sig = { signal: rt.signal };
    this.pt = h("div", "pt", this.lvl);
    this.pt.addEventListener("click", () => { if (rt.hasWindow("hero")) rt.toggleWindow("hero"); }, sig);
    this.pt.addEventListener("keydown", (e) => {
      if ((e.key === "Enter" || e.key === " ") && !e.repeat && e.target === this.pt && rt.hasWindow("hero")) { e.preventDefault(); e.stopPropagation(); this.pt.click(); }
    }, sig);
    // The portrait is a button only once the Hero window is registered (an unregistered key is a no-op).
    const syncPortrait = () => {
      const on = rt.hasWindow("hero");
      this.pt.classList.toggle("off", !on);
      if (on) { this.pt.setAttribute("role", "button"); this.pt.tabIndex = 0; this.pt.title = "Open the Hero window"; this.pt.setAttribute("aria-label", "Open the Hero window"); }
      else { this.pt.removeAttribute("role"); this.pt.tabIndex = -1; this.pt.removeAttribute("title"); this.pt.removeAttribute("aria-label"); }
    };
    syncPortrait();
    this.mode = asButton(h("span", "md"), "Change the hero's behaviour");
    this.mode.title = "Hero behaviour (click for the next one)";
    this.mode.addEventListener("click", () => this.nextBehaviour(), sig);
    this.el = h("div", "hframe", this.pt, h("div", "in2", h("div", "n", this.name, this.mode),
      h("div", "b hp", this.hp), h("div", "b mp", this.mp), h("div", "b xp", this.xp)));
    this.el.hidden = true;
    rt.layers.wins.append(this.el);
    this.refresh = rafCoalesce(() => this.render(), rt.signal);
    // HeroStats recomputes at most once a second (item infos / magic levels arriving later): follow it while shown.
    this.slow = rt.ticker.every(1000, () => this.render());
    const off = ctx.models.hero.onChange(this.refresh);
    const offWin = rt.windowsChanged.on((key) => { if (key === "hero") syncPortrait(); });
    rt.signal.addEventListener("abort", () => { off(); offWin(); this.slow.off(); }, { once: true });
    this.render();
  }

  dispose(): void {
    this.slow.off();
    this.el.remove();
  }

  private render(): void {
    const hero = this.ctx.models.hero;
    const x = hero.info;
    const show = hero.summoned && !!x;
    if (this.el.hidden === show) this.el.hidden = !show;
    if (!show) { this.slow.off(); return; }
    this.slow.on();
    this.el.classList.toggle("fallen", hero.dead);
    const av = 1400 + x!.cls + 10 * x!.gender; // GameScene.HeroAvatar (heroPanels.ts heroAvatar)
    if (av !== this.avatar) {
      this.avatar = av;
      void libPicture(Lib.Prguse, av).then((p) => { if (p && this.avatar === av) this.pt.style.backgroundImage = `url(${p.url})`; });
    }
    setText(this.lvl, String(x!.level));
    setText(this.name, hero.dead ? `${x!.name} ✝` : x!.name);
    const stats = this.ctx.models.heroStats;
    const maxHp = stats.maxHp(), maxMp = stats.maxMp();
    setStyle(this.hp, "transform", `scaleX(${frac(x!.hp, maxHp).toFixed(3)})`);
    setStyle(this.mp, "transform", `scaleX(${frac(x!.mp, maxMp).toFixed(3)})`);
    const xp = x!.maxExperience > 0n ? Number((x!.experience * 10000n) / x!.maxExperience) / 10000 : 0;
    setStyle(this.xp, "transform", `scaleX(${Math.max(0, Math.min(1, xp)).toFixed(4)})`);
    const title = `${x!.name} · Lv ${x!.level}\nHP ${fmt(x!.hp)} / ${fmt(maxHp)} · MP ${fmt(x!.mp)} / ${fmt(maxMp)}\nEXP ${(xp * 100).toFixed(2)}%`;
    if (this.el.title !== title) this.el.title = title;
    if (this.asked >= 0 && this.asked === hero.behaviour) this.asked = -1;
    setText(this.mode, hero.behaviour === HeroBehaviour.CounterAttack ? "Counter" : pretty(HeroBehaviourName[hero.behaviour] ?? "?"));
    this.mode.classList.toggle("wait", this.asked >= 0);
  }

  /** HeroBehaviourPanel: C.SetHeroBehaviour for the next behaviour (the server answers S.SetHeroBehaviour). */
  private nextBehaviour(): void {
    const hero = this.ctx.models.hero;
    if (!hero.summoned || hero.dead || this.asked >= 0) return;
    const i = BEHAVIOURS.indexOf(hero.behaviour as (typeof BEHAVIOURS)[number]);
    const next = BEHAVIOURS[(i + 1) % BEHAVIOURS.length]!;
    this.asked = next;
    this.ctx.send(ClientPackets.SetHeroBehaviour({ Behaviour: next }));
    this.mode.classList.add("wait");
    // A refused change never answers: allow another click after a while.
    later(() => { if (this.asked === next) { this.asked = -1; this.refresh(); } }, 1500, this.rt.signal);
  }
}
