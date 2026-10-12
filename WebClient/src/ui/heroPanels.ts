// Hero HUD panels (Client/MirScenes/Dialogs/HeroDialogs.cs + MainDialogs.cs): HeroInfoPanel (avatar, name, level, HP / MP /
// experience bars), MainDialog.HeroMenuButton + HeroMenuPanel (hero skills / inventory / character), MainDialog.HeroSummonButton
// (@SUMMONHERO), HeroBehaviourPanel (C.SetHeroBehaviour) and the hero's buff row (GameScene.HeroBuffsDialog, a BuffDialog).
// Visibility follows GameScene.UpdateHeroSpawnState: info panel, menu button and behaviour panel while State > Unsummoned;
// the summon button while HasHero (State > None).

import { ClientPackets } from "../protocol/client.ts";
import { HeroBehaviour, HeroBehaviourName } from "../protocol/enums.generated.ts";
import type { Hero } from "../game/hero.ts";
import { BuffBar, libIcon } from "./buffBar.ts";
import { el } from "./screens.ts";

type Send = (f: Uint8Array) => void;

/** GameScene.HeroAvatar: Prguse index of the hero's face (HeroInfoPanel; HeroManageAvatar adds 370). */
export function heroAvatar(cls: number, gender: number): number {
  return 1400 + cls + 10 * gender;
}

/** Sets an <img> to a Prguse image (async; the frame loop never waits). */
export function setPrguseImage(img: HTMLImageElement, index: number): void {
  img.dataset.index = String(index);
  void libIcon("Prguse", index).then((url) => { if (url && img.dataset.index === String(index)) img.src = url; });
}

/** HeroInfoPanel (MainDialogs.cs creates it inside MainDialog; Update() on HeroInformation / HeroLevelChanged). */
export class HeroInfoPanel {
  readonly root = el("div", { class: "hero-info", hidden: true });
  private readonly avatar = el("img", { class: "hero-avatar", alt: "" });
  private readonly danger = el("img", { class: "hero-avatar hero-danger", alt: "", hidden: true });
  private readonly deadImg = el("img", { class: "hero-avatar hero-dead", alt: "", hidden: true });
  private readonly level = el("span", { class: "hero-level" });
  private readonly name = el("span", { class: "hero-name" });
  private readonly hpFill = el("div", { class: "hero-bar-fill hp" });
  private readonly mpFill = el("div", { class: "hero-bar-fill mp" });
  private readonly expFill = el("div", { class: "hero-bar-fill exp" });
  private readonly hpLabel = el("span", { class: "hero-bar-label" });
  private readonly mpLabel = el("span", { class: "hero-bar-label" });
  private readonly expLabel = el("span", { class: "hero-bar-label" });
  private avatarIndex = -1;
  private nextAvatarChange = 0;

  /** `maxHp` / `maxMp`: Hero.Stats[Stat.HP] / [Stat.MP] (HeroCharacterWindow.maxHp / maxMp). */
  constructor(parent: HTMLElement, private readonly hero: Hero, private readonly maxHp: () => number, private readonly maxMp: () => number) {
    setPrguseImage(this.deadImg, 1379);
    const bar = (fill: HTMLElement, label: HTMLElement) => el("div", { class: "hero-bar" }, fill, label);
    this.root.append(
      el("div", { class: "hero-avatar-box" }, this.avatar, this.danger, this.deadImg),
      el("div", { class: "hero-info-body" },
        el("div", { class: "hero-title" }, this.level, this.name),
        bar(this.hpFill, this.hpLabel), bar(this.mpFill, this.mpLabel), bar(this.expFill, this.expLabel)));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    parent.append(this.root);
  }

  /** Per frame: visibility, bars and labels (HealthBar / ManaBar / ExperienceBar BeforeDraw) and Avatar_BeforeDraw's blink. */
  update(now: number): void {
    const h = this.hero.info;
    const show = this.hero.summoned && !!h;
    if (this.root.hidden === show) this.root.hidden = !show;
    if (!show || !h) return;
    // HeroInfoPanel.Update
    const index = heroAvatar(h.cls, h.gender);
    if (index !== this.avatarIndex) {
      this.avatarIndex = index;
      setPrguseImage(this.avatar, index);
      setPrguseImage(this.danger, index + 350); // DangerAvatar.Index = Avatar.Index + 350
    }
    setText(this.level, String(h.level));
    setText(this.name, h.name);
    const maxHp = this.maxHp(), maxMp = this.maxMp();
    // PercentHealth / PercentMana = (byte)(HP / (float)Stats[HP] * 100) (HeroHealthChanged, RefreshStats)
    const pHealth = maxHp > 0 ? Math.min(255, Math.floor((h.hp / maxHp) * 100)) : 0;
    const pMana = maxMp > 0 ? Math.min(255, Math.floor((h.mp / maxMp) * 100)) : 0;
    setWidth(this.hpFill, Math.min(100, pHealth));
    setWidth(this.mpFill, Math.min(100, pMana));
    setText(this.hpLabel, `${h.hp}/${maxHp}`);
    setText(this.mpLabel, `${h.mp}/${maxMp}`);
    const exp = h.maxExperience > 0n ? (Number(h.experience) / Number(h.maxExperience)) * 100 : 0;
    setWidth(this.expFill, Math.max(0, Math.min(100, exp)));
    setText(this.expLabel, `${exp.toFixed(2)}%`);
    // Avatar_BeforeDraw: dead overlay; below 21 % health the danger face blinks every 400 ms
    if (this.deadImg.hidden === this.hero.dead) this.deadImg.hidden = !this.hero.dead;
    if (pHealth > 20) { this.danger.hidden = true; return; }
    if (now < this.nextAvatarChange) return;
    this.nextAvatarChange = now + 400;
    this.danger.hidden = !this.danger.hidden;
  }
}

/** HeroMenuPanel: three buttons (HeroMagicsButton, HeroInventoryButton, HeroEquipmentButton), toggled by HeroMenuButton. */
export class HeroMenuPanel {
  readonly root = el("div", { class: "hero-menu", hidden: true });

  constructor(parent: HTMLElement, open: HeroWindowOpeners) {
    this.root.append(
      el("button", { title: "Hero Skills (Ctrl+S)", onclick: () => open.openSkills() }, "Skills"),
      el("button", { title: "Hero Inventory (Ctrl+I)", onclick: () => open.openInventory() }, "Bag"),
      el("button", { title: "Hero Character (Ctrl+C)", onclick: () => open.openCharacter() }, "Char"));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    parent.append(this.root);
  }

  get visible(): boolean { return !this.root.hidden; }

  /** HeroMenuPanel.Toggle. */
  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
  }
}

/** HeroBehaviourPanel: one button per HeroBehaviour; the current one is disabled (UpdateBehaviour). */
export class HeroBehaviourPanel {
  readonly root = el("div", { class: "hero-behaviour", hidden: true });
  private readonly buttons: HTMLButtonElement[] = [];

  constructor(parent: HTMLElement, private readonly hero: Hero, send: Send) {
    for (const b of [HeroBehaviour.Attack, HeroBehaviour.CounterAttack, HeroBehaviour.Follow, HeroBehaviour.Custom]) {
      const name = HeroBehaviourName[b] ?? String(b);
      // SetBehaviour; Hint = ClientTextKeys.HeroBehaviourFormat
      const button = el("button", { title: `Hero Behaviour: ${name}`, onclick: () => send(ClientPackets.SetHeroBehaviour({ Behaviour: b })) }, name);
      this.buttons[b] = button;
      this.root.append(button);
    }
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    parent.append(this.root);
  }

  update(): void {
    const show = this.hero.summoned;
    if (this.root.hidden === show) this.root.hidden = !show;
    // UpdateBehaviour: Enabled = (byte)behaviour != i
    this.buttons.forEach((b, i) => { if (b.disabled !== (this.hero.behaviour === i)) b.disabled = this.hero.behaviour === i; });
  }
}

/** Opens the hero windows owned elsewhere (HeroDialog skill / character pages, HeroInventoryDialog). */
export interface HeroWindowOpeners {
  openSkills(): void;
  openInventory(): void;
  openCharacter(): void;
}

/**
 * All hero HUD parts. `hudButtons` gets two buttons: "Summon" (HeroSummonButton, while HasHero) and "Hero" (HeroMenuButton,
 * while summoned; toggles the menu panel). Call `update(now)` every frame.
 */
export class HeroPanels {
  readonly info: HeroInfoPanel;
  readonly menu: HeroMenuPanel;
  readonly behaviour: HeroBehaviourPanel;
  /** GameScene.HeroBuffsDialog (top right, under the user's buffs). */
  readonly buffs: BuffBar;
  /** MainDialog.HeroSummonButton: C.Chat "@SUMMONHERO" (the server summons or dismisses). */
  readonly summonButton: HTMLButtonElement;
  /** MainDialog.HeroMenuButton: toggles the HeroMenuPanel. */
  readonly menuButton: HTMLButtonElement;
  private readonly box = el("div", { class: "hero-panels" });

  constructor(stage: HTMLElement, private readonly hero: Hero, send: Send, open: HeroWindowOpeners, maxHp: () => number, maxMp: () => number) {
    this.info = new HeroInfoPanel(this.box, hero, maxHp, maxMp);
    this.behaviour = new HeroBehaviourPanel(this.box, hero, send);
    this.menu = new HeroMenuPanel(this.box, open);
    stage.append(this.box);
    this.buffs = new BuffBar(stage, "hero-buff-bar");
    this.summonButton = el("button", { title: "Summon / dismiss your hero", hidden: true, onclick: () => send(ClientPackets.Chat({ Message: "@SUMMONHERO" })) }, "Summon");
    this.menuButton = el("button", { title: "Hero menu", hidden: true, onclick: () => this.menu.toggle() }, "Hero");
  }

  update(now: number): void {
    const h = this.hero;
    if (this.summonButton.hidden === h.hasHero) this.summonButton.hidden = !h.hasHero; // GameScene.HasHero setter
    if (this.menuButton.hidden === h.summoned) this.menuButton.hidden = !h.summoned;
    // HeroMenuPanel.Visible = HeroMenuPanel.Visible && HeroMenuButton.Visible
    if (!h.summoned && this.menu.visible) this.menu.toggle(false);
    this.info.update(now);
    this.behaviour.update();
    this.buffs.update(h.info ? h.buffs : [], now);
  }
}

function setText(e: HTMLElement, text: string): void {
  if (e.textContent !== text) e.textContent = text;
}

function setWidth(e: HTMLElement, percent: number): void {
  const w = `${percent}%`;
  if (e.style.width !== w) e.style.width = w;
}
