// The new UI's per-game runtime (replaces the scene-lab mock's module-level singletons: hudWindow's window list and
// z counter, hudDrag's hosts, the popups' window listener): one UiRuntime per startGame, built by modernUi.ts from the
// GameUiContext and thrown away on dispose (log-out), so nothing survives into the next session.
//
// It owns: the '.hudm' root and its layers (layers.ts), the UI scope (an AbortSignal that aborts with the game scope or
// on dispose), the window manager (wm.ts), the popups (popups.ts), the frame ticker (ticker.ts), the theme and HUD
// scale, and the lazy window registry (registerWindow / openWindow: later phases add windows; a window is constructed
// on its first open). Plus a generic per-runtime service registry for core modules owned by other workers (tooltip,
// drag): `service(key, make)` builds once per runtime.
//
// Keys: the runtime never listens to the keyboard. The integrator registers, on the input router (src/game/input.ts):
//   input.register("modal",  (e) => rt.popups.handleKey(e));
//   input.register("window", (e) => rt.wm.handleKey(e));      // front window's key() hook, Esc / Shift+Esc
// and calls rt.frame(t) once a frame (GameUi.frame, one FramePoint).

import { Emitter } from "../../../game/emitter.ts";
import { installIconDefs } from "./icons.ts";
import { Layers, hudScale } from "./layers.ts";
import type { LayoutInfo } from "./layers.ts";
import { Popups } from "./popups.ts";
import { applyTheme } from "./theme.ts";
import type { ThemeColors, ThemeSettings } from "./theme.ts";
import { Ticker } from "./ticker.ts";
import { WindowManager } from "./wm.ts";
import type { UiWindow, WindowHost } from "./wm.ts";

export interface UiRuntimeInit {
  /** #stage (GameUiContext.stage); the root is appended last (after the 2D overlay canvas). */
  stage: HTMLElement;
  /** The game scope (GameUiContext.signal); its abort disposes the runtime too. */
  signal: AbortSignal;
  /** The game resolution [width, height] (GameUiContext.screen). */
  screen: readonly [number, number];
  /** Settings UiScale (default 1) and UiAutoScale (default true). */
  scale?: number;
  autoScale?: boolean;
  theme?: ThemeSettings;
  /** The touch layout (settings.ts isMobileUi): k fits a 1024 px wide windows layer (mobile/mobileUi.ts). */
  mobile?: boolean;
}

/** Builds a window on its first open. */
export type WindowFactory = (rt: UiRuntime) => UiWindow;

export class UiRuntime implements WindowHost {
  readonly layers: Layers;
  readonly wm: WindowManager;
  readonly popups: Popups;
  readonly ticker = new Ticker();
  /** Fires after the layout changed (screen, HUD scale, narrow): HUD parts re-measure here, never per frame. */
  readonly layoutChanged = new Emitter<LayoutInfo>();
  /** Fires after the theme changed (canvas drawers re-read `theme`, Performance.md T3-5). */
  readonly themeChanged = new Emitter<ThemeColors>();
  /** Fires with the key after registerWindow (controls for unregistered windows stay hidden until then). */
  readonly windowsChanged = new Emitter<string>();
  private readonly scope = new AbortController();
  private readonly factories = new Map<string, WindowFactory>();
  private readonly built = new Map<string, UiWindow>();
  private readonly services = new Map<string, unknown>();
  private screenW: number;
  private screenH: number;
  private uiScale: number;
  private autoScale: boolean;
  /** The touch layout (UiRuntimeInit.mobile). */
  readonly mobile: boolean;
  private themeColors: ThemeColors;
  private disposed = false;

  constructor(init: UiRuntimeInit) {
    this.screenW = init.screen[0];
    this.screenH = init.screen[1];
    this.uiScale = init.scale ?? 1;
    this.autoScale = init.autoScale ?? true;
    this.mobile = init.mobile ?? false;
    if (init.signal.aborted) this.scope.abort();
    else init.signal.addEventListener("abort", () => this.dispose(), { once: true, signal: this.scope.signal });
    this.layers = new Layers();
    this.layers.root.classList.toggle("mobile", this.mobile);
    installIconDefs(this.layers.root);
    this.themeColors = applyTheme(this.layers.root, init.theme);
    this.wm = new WindowManager(this.layers, this.signal);
    this.popups = new Popups(this.layers.pops, this.signal);
    this.layers.apply(this.screenW, this.screenH, this.k);
    this.layers.mount(init.stage);
  }

  /** The UI scope: aborts on dispose (or with the game scope). Pass it to every listener / timer. */
  get signal(): AbortSignal { return this.scope.signal; }
  get root(): HTMLElement { return this.layers.root; }
  /** The current HUD scale k. */
  get k(): number {
    // mobile: every window still gets its 1024 px layer (webclient.md 3.6), as large as the phone's width allows
    if (this.mobile) return Math.min(4, Math.max(0.25, (this.uiScale > 0 ? this.uiScale : 1) * this.screenW / 1024));
    return hudScale(this.uiScale, this.autoScale, this.screenH);
  }
  get layout(): LayoutInfo { return this.layers.layout; }
  /** The resolved theme colours (for canvas drawing). */
  get theme(): ThemeColors { return this.themeColors; }

  // — Layout / theme —

  /** New HUD scale settings (UiScale, UiAutoScale). */
  setScale(scale: number, autoScale: boolean): void {
    this.uiScale = scale; this.autoScale = autoScale;
    this.relayout();
  }
  /** The game resolution changed. */
  setScreen(width: number, height: number): void {
    this.screenW = width; this.screenH = height;
    this.relayout();
  }
  private relayout(): void {
    if (this.disposed) return;
    if (!this.layers.apply(this.screenW, this.screenH, this.k)) return;
    this.wm.reclampAll();
    this.layoutChanged.emit(this.layers.layout);
  }
  /** Applies theme settings (preset + custom colours) to the root. */
  setTheme(s: ThemeSettings | undefined): void {
    if (this.disposed) return;
    this.themeColors = applyTheme(this.layers.root, s);
    this.themeChanged.emit(this.themeColors);
  }
  /** Shows / hides the whole UI (screenshots, cut scenes). Keys stop with it (wm / popups check root.hidden). */
  setVisible(show: boolean): void { this.layers.root.hidden = !show; }

  // — Frame —

  /** Once a frame (GameUi.frame at one FramePoint): drives the ticker. */
  frame(now: number): void { if (!this.disposed) this.ticker.tick(now); }

  // — Window registry (lazy construction) —

  /** Registers a window by key ("inventory", "character", ...). Later phases call it; the factory runs on the first open.
   *  Registering a key again replaces the factory (a built window stays). */
  registerWindow(key: string, factory: WindowFactory): void {
    if (this.disposed) return;
    this.factories.set(key, factory);
    this.windowsChanged.emit(key);
  }
  /** Whether a window is registered (the menu strip / hotkeys skip keys nobody registered yet). */
  hasWindow(key: string): boolean { return this.factories.has(key) || this.built.has(key); }
  /** The window for `key`, built now if needed (null when not registered). */
  window<T extends UiWindow = UiWindow>(key: string): T | null {
    const w = this.built.get(key);
    if (w) return w as T;
    const f = this.factories.get(key);
    if (!f || this.disposed) return null;
    const made = f(this);
    this.built.set(key, made);
    return made as T;
  }
  /** The window if it was built already (never builds: model events use it to skip closed, unbuilt windows). */
  peekWindow<T extends UiWindow = UiWindow>(key: string): T | null { return (this.built.get(key) as T | undefined) ?? null; }
  /** Opens / closes / toggles (show undefined) a registered window. False when the key has no window. */
  toggleWindow(key: string, show?: boolean): boolean {
    if (show === false) { this.built.get(key)?.toggle(false); return this.hasWindow(key); }
    const w = this.window(key);
    if (!w) return false;
    w.toggle(show ?? !w.open);
    return true;
  }
  /** Whether that window is open. */
  isOpen(key: string): boolean { return this.built.get(key)?.open ?? false; }
  /** Registered window keys. */
  get windowKeys(): readonly string[] { return [...new Set([...this.factories.keys(), ...this.built.keys()])]; }

  // — Services (per-runtime singletons for core modules: tooltip, drag, item cells) —

  /** The runtime's instance of a service, built on first use with `make` (dropped on dispose). */
  service<T>(key: string, make: (rt: UiRuntime) => T): T {
    if (this.services.has(key)) return this.services.get(key) as T;
    const v = make(this);
    this.services.set(key, v);
    return v;
  }

  // — Dispose —

  /** Closes every window (their closed() hooks run), cancels popups, aborts the UI scope (listeners / timers stop),
   *  drops the ticker, the registries and services, and removes the root. Safe to call twice. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    try { this.wm.dispose(); } catch (err) { console.error(err); }
    try { this.popups.closeAll(); } catch (err) { console.error(err); }
    for (const s of this.services.values()) {
      const d = (s as { dispose?: unknown } | null)?.dispose;
      if (typeof d === "function") { try { (d as () => void).call(s); } catch (err) { console.error(err); } }
    }
    this.scope.abort();
    this.ticker.clear();
    this.factories.clear();
    this.built.clear();
    this.services.clear();
    this.layers.remove();
  }
}
