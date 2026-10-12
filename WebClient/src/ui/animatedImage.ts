// MirAnimatedControl (Client/MirControls/MirAnimatedControl.cs) on top of MirImage: Index + OffSet, advanced every
// AnimationDelay ms up to AnimationCount, then looped or stopped (Loop) with AfterAnimation; optional FadeIn
// (Opacity += FadeInRate every FadeInDelay ms). One shared timer drives every live control (MirAnimatedControl.Animations,
// updated from CMain's loop); a control leaves it on dispose(), so a hidden scene stops animating.

import { MirImage, libPicture, type ImageOptions } from "./mir/controls.ts";

export interface AnimatedOptions extends ImageOptions {
  animated?: boolean;
  animationCount?: number;
  animationDelay?: number;
  /** Loop (MirAnimatedControl's default is true). */
  loop?: boolean;
  fadeIn?: boolean;
  fadeInDelay?: number;
  fadeInRate?: number;
  /** Starting Opacity (MirImageControl default 1). */
  opacity?: number;
}

const animations = new Set<MirAnimated>();
let timer = 0;

function tick(): void {
  const now = performance.now();
  for (const a of [...animations]) a.updateOffSet(now);
}

function track(a: MirAnimated): void {
  animations.add(a);
  if (!timer) timer = window.setInterval(tick, 16);
}

function untrack(a: MirAnimated): void {
  animations.delete(a);
  if (!animations.size && timer) { clearInterval(timer); timer = 0; }
}

export class MirAnimated extends MirImage {
  private offSet = 0;
  private _animated = false;
  private animationCount: number;
  private readonly animationDelay: number;
  private readonly loop: boolean;
  private fadeIn: boolean;
  private readonly fadeInDelay: number;
  private readonly fadeInRate: number;
  private opacity: number;
  private nextOffSet: number;
  private nextFadeTime: number;
  /** AfterAnimation: fires when OffSet reaches AnimationCount (every cycle when looping). */
  afterAnimation: (() => void) | null = null;
  /** Called whenever the drawn frame changes (AfterDraw users such as the ChrSel blend layer). */
  onFrame: ((frame: number) => void) | null = null;

  constructor(opts: AnimatedOptions) {
    super(opts);
    this.animationCount = opts.animationCount ?? 0;
    this.animationDelay = opts.animationDelay ?? 0;
    this.loop = opts.loop ?? true;
    this.fadeIn = opts.fadeIn ?? false;
    this.fadeInDelay = opts.fadeInDelay ?? 0;
    this.fadeInRate = opts.fadeInRate ?? 0;
    this.opacity = opts.opacity ?? 1;
    this.picOpacity = this.opacity;
    const now = performance.now();
    this.nextOffSet = now;
    this.nextFadeTime = now + this.fadeInDelay;
    this.animated = opts.animated ?? false;
    this.preload();
    track(this);
  }

  /** Index + OffSet: the frame on screen. */
  get frame(): number { return this.index + (this.offSet ?? 0); }

  get animated(): boolean { return this._animated; }
  set animated(v: boolean) {
    if (v === this._animated) return;
    this._animated = v;
    this.nextOffSet = performance.now() + this.fadeInDelay;
  }

  override get index(): number { return super.index; }
  override set index(i: number) {
    if (i === super.index) return;
    super.index = i;
    this.preload();
    this.onFrame?.(this.frame);
  }

  private setOffSet(v: number): void {
    if (v === this.offSet) return;
    this.offSet = v;
    this.redraw();
    this.onFrame?.(this.frame);
  }

  protected override displayIndex(): number {
    return super.displayIndex() + (this.offSet ?? 0);
  }

  /** Warms the picture cache for the whole cycle so frames swap without a gap. */
  private preload(): void {
    if (!this.library) return;
    for (let i = 0; i < (this.animationCount ?? 0); i++) void libPicture(this.library, this.index + i);
  }

  /** MirAnimatedControl.UpdateOffSet. */
  updateOffSet(now: number): void {
    if (this.fadeIn && now > this.nextFadeTime) {
      this.opacity += this.fadeInRate;
      if (this.opacity > 1) { this.opacity = 1; this.fadeIn = false; }
      this.picOpacity = this.opacity;
      this.nextFadeTime = now + this.fadeInDelay;
    }

    if (!this.visible || !this._animated || this.animationDelay === 0 || this.animationCount === 0) return;
    if (now < this.nextOffSet) return;
    this.nextOffSet = now + this.animationDelay;

    if (this.offSet + 1 < this.animationCount) { this.setOffSet(this.offSet + 1); return; }
    this.offSet++; // ++OffSet reached AnimationCount

    const after = this.afterAnimation;
    if (!this.loop) {
      this._animated = false;
      this.redraw();
      this.onFrame?.(this.frame);
    } else this.setOffSet(0);
    after?.();
  }

  override dispose(): void {
    untrack(this);
    this.afterAnimation = null;
    this.onFrame = null;
    super.dispose();
  }
}
